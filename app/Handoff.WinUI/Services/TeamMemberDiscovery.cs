using System.Text.RegularExpressions;

namespace Handoff.WinUI.Services;

/// <summary>
/// One remote branch's identity for the team-members/{member}/{branch}/ folder layout.
/// Member and Branch are folder-safe normalized values (the keys we use on disk).
/// OriginalCommitter / OriginalBranch / AuthorEmail keep raw values from git
/// for diagnostics, UI display, and tie-breaking when names collide.
/// </summary>
public sealed record TeamBranch(
    string Member,
    string Branch,
    string OriginalCommitter,
    string OriginalBranch,
    string AuthorEmail,
    string LastCommitSha,
    string LastCommitDate);

public sealed class TeamMemberDiscovery
{
    // Standard branches that are not "personal work" — always filtered out.
    // Override via constructor if a project uses different convention names.
    private static readonly IReadOnlySet<string> DefaultExcludedBranches =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HEAD", "main", "master", "develop",
        };

    // Compiled once: matches any run of non-alphanumeric ASCII characters.
    // Used for both committer and branch normalization (project agreed: ASCII-only).
    private static readonly Regex NonAlphanumericRun =
        new Regex("[^a-z0-9]+", RegexOptions.Compiled);

    // Format string for `git for-each-ref --format=...`. Fields separated by '|'.
    // Order: short refname, committer name, author email, committer date (ISO 8601), short sha.
    // Why committername (not authorname) as the Member key:
    //   - Context-sharing fires on commit, so "who committed last" is the active worker.
    //   - On a clean personal branch authorname == committername anyway.
    //   - On rebase/cherry-pick they diverge, and committer reflects current activity better.
    // authoremail is captured for diagnostics — useful when two committers normalize to the
    // same folder name (rare but possible). It is not used as a grouping key.
    private const string ForEachRefFormat =
        "--format=%(refname:short)|%(committername)|%(authoremail)|%(committerdate:iso8601)|%(objectname:short)";

    private const string OriginPrefix = "origin/";

    private readonly GitProcess _git;
    private readonly string _userRepoPath;
    private readonly string _teamMembersPath;
    private readonly IReadOnlySet<string> _excludedBranches;

    /* ======================================================================================
     * TeamMemberDiscovery (constructor)
     * Description: Wires up the discovery service against an existing GitProcess.
     *              The caller (SyncService) is responsible for fetching the user's
     *              repo before calling DiscoverAsync — this class never auto-fetches
     *              so that fetch frequency stays under SyncService control.
     * Parameters:
     *   git              - GitProcess wrapper for invoking git commands
     *   userRepoPath     - absolute path to the user's main project repo (where origin lives)
     *   teamMembersPath  - absolute path to the team-members/ folder where {member}/{branch}/
     *                      directories will be created
     *   excludedBranches - optional set of branch names (without "origin/" prefix) to skip;
     *                      if null, defaults to {HEAD, main, master, develop}
     * Return Values: (constructor)
     * ======================================================================================
     */
    public TeamMemberDiscovery(
        GitProcess git,
        string userRepoPath,
        string teamMembersPath,
        IReadOnlySet<string>? excludedBranches = null)
    {
        this._git = git;
        this._userRepoPath = userRepoPath;
        this._teamMembersPath = teamMembersPath;
        this._excludedBranches = excludedBranches ?? DefaultExcludedBranches;
    }

    /* ======================================================================================
     * DiscoverAsync
     * Description: Lists remote-tracking branches under refs/remotes/origin and parses
     *              each into a TeamBranch. Filters out shared branches and any malformed
     *              lines. Does NOT fetch — caller must run `git fetch origin` beforehand
     *              if fresh remote data is required.
     * Parameters:
     *   ct - cancellation token; propagates into the underlying git invocation
     * Return Values:
     *   IReadOnlyList<TeamBranch> with one entry per personal remote branch.
     *   Empty list when the git command itself fails (the failure is logged but not thrown,
     *   so the polling loop can still proceed to other steps this tick).
     * ======================================================================================
     */
    public async Task<IReadOnlyList<TeamBranch>> DiscoverAsync(CancellationToken ct = default)
    {
        // Each token kept separate so that ProcessStartInfo.ArgumentList delivers them
        // unparsed to git (no shell tokenization, no escaping concerns).
        IReadOnlyList<string> args = new[]
        {
            "for-each-ref",
            "refs/remotes/origin",
            ForEachRefFormat,
        };

        GitResult result = await this._git.RunAsync(this._userRepoPath, args, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            Logger.Log("Discovery", "for-each-ref failed (exit=" + result.ExitCode + "): " + result.Stderr);
            return Array.Empty<TeamBranch>();
        }

        List<TeamBranch> branches = new List<TeamBranch>();
        // Split on '\n'; ParseLine strips trailing '\r' to handle CRLF on Windows git.
        string[] lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            try
            {
                TeamBranch? parsed = ParseLine(line);
                if (parsed is not null)
                {
                    branches.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                // A single malformed line must not kill the whole discovery cycle —
                // the daemon would lose track of every other teammate over one bad row.
                Logger.LogError("Discovery", "ParseLine '" + line + "'", ex);
            }
        }
        return branches;
    }

    /* ======================================================================================
     * EnsureFolders
     * Description: Creates team-members/{member}/{branch}/ for each branch if missing.
     *              Idempotent — Directory.CreateDirectory is a no-op when the path
     *              already exists, and existing shared-context.json files are NEVER
     *              touched (we only create directories, never write files here).
     *              One failed CreateDirectory is logged and skipped; remaining branches
     *              are still processed.
     * Parameters:
     *   branches - the discovered branches whose folders should exist
     * Return Values: (none)
     * ======================================================================================
     */
    public void EnsureFolders(IReadOnlyList<TeamBranch> branches)
    {
        foreach (TeamBranch branch in branches)
        {
            string folderPath = Path.Combine(this._teamMembersPath, branch.Member, branch.Branch);
            try
            {
                Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                // One bad path (permissions, invalid chars on disk, drive missing)
                // shouldn't stop folders for the other branches.
                Logger.LogError("Discovery", "CreateDirectory '" + folderPath + "'", ex);
            }
        }
    }

    /* ======================================================================================
     * NormalizeForFolder
     * Description: Converts an arbitrary string (committer name, branch name) into a
     *              folder-safe identifier: lowercase ASCII alphanumerics, with any
     *              run of other characters collapsed to a single '-' separator,
     *              and leading/trailing '-' removed.
     *              The hook (Node.js side) and daemon (this method) MUST share this
     *              exact rule — otherwise the user's `self` value will not match the
     *              daemon-created folder name and the hook will not skip its own context.
     * Parameters:
     *   s - any string ("Chris Park", "feature/auth", "release/v2.1.0", null, etc.)
     * Return Values:
     *   Normalized string (e.g. "chris-park", "feature-auth", "release-v2-1-0").
     *   Empty string if the input is null, whitespace, or has no normalizable chars.
     * ======================================================================================
     */
    public static string NormalizeForFolder(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }
        string lowered = s.Trim().ToLowerInvariant();
        string replaced = NonAlphanumericRun.Replace(lowered, "-");
        return replaced.Trim('-');
    }

    /* ======================================================================================
     * GetGitUserNameAsync
     * Description: Reads `git config user.name` for the given repo and normalizes the
     *              result to a folder-safe identifier. Used by the daemon to bootstrap
     *              config.local.json's Self value on first run when the user has not
     *              set it explicitly. Returns null when git config is missing or empty
     *              so that the caller can decide whether to fall back to a UI prompt
     *              or leave Self blank.
     * Parameters:
     *   git      - GitProcess wrapper for invoking git
     *   repoPath - absolute path to the repo whose git config to read
     *   ct       - cancellation token; propagates into the git invocation
     * Return Values:
     *   Normalized self string when git config user.name is set; null otherwise
     *   (including all error / empty cases — never throws).
     * ======================================================================================
     */
    public static async Task<string?> GetGitUserNameAsync(
        GitProcess git,
        string repoPath,
        CancellationToken ct = default)
    {
        GitResult result = await git.RunAsync(
            repoPath,
            new[] { "config", "user.name" },
            ct).ConfigureAwait(false);

        // Failure or unset key → null. We deliberately do not throw: missing
        // git config is not an error condition for the polling loop, and the
        // daemon may still receive a Self value via the UI later.
        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        string normalized = NormalizeForFolder(result.Stdout);
        // A name made only of punctuation normalizes to empty string —
        // treat that as "no usable value" rather than a valid Self.
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /* ======================================================================================
     * ParseLine
     * Description: Parses a single output line from `git for-each-ref --format=...`
     *              into a TeamBranch. Returns null (not throws) for any line that is
     *              malformed, references a non-origin ref, or names an excluded
     *              branch — these are SKIP cases, not errors.
     * Parameters:
     *   line - one line of for-each-ref output, fields separated by '|'
     * Return Values:
     *   TeamBranch on success; null when the line should be skipped silently.
     * ======================================================================================
     */
    private TeamBranch? ParseLine(string line)
    {
        // Trim trailing CR — Windows git can emit CRLF even when stdout is captured.
        string cleaned = line.TrimEnd('\r');
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        string[] parts = cleaned.Split('|');
        if (parts.Length != 5)
        {
            // Wrong number of pipes — could be an annotated tag or a manually pushed
            // ref without a committer. Skip silently rather than misinterpreting.
            return null;
        }

        string refnameShort = parts[0];
        string committerName = parts[1];
        string authorEmail = parts[2];
        string committerDate = parts[3];
        string shortSha = parts[4];

        // Only origin/* is considered. Other namespaces (refs/remotes/upstream/...)
        // are out of scope for this iteration; SyncService can be taught to scan
        // additional remotes later if the project gains forks.
        if (!refnameShort.StartsWith(OriginPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        string rawBranch = refnameShort.Substring(OriginPrefix.Length);

        // Filter shared branches that don't represent any one person's active work.
        if (this._excludedBranches.Contains(rawBranch))
        {
            return null;
        }

        string memberFolder = NormalizeForFolder(committerName);
        string branchFolder = NormalizeForFolder(rawBranch);
        if (string.IsNullOrEmpty(memberFolder) || string.IsNullOrEmpty(branchFolder))
        {
            // Committer with no normalizable chars (only punctuation) or branch
            // normalizing to empty — neither can become a folder name. Skip.
            return null;
        }

        return new TeamBranch(
            Member: memberFolder,
            Branch: branchFolder,
            OriginalCommitter: committerName,
            OriginalBranch: rawBranch,
            AuthorEmail: authorEmail,
            LastCommitSha: shortSha,
            LastCommitDate: committerDate);
    }
}
