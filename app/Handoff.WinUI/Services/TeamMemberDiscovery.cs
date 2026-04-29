using System.Text.RegularExpressions;

namespace Handoff.WinUI.Services;

/// <summary>
/// One row's identity returned from Supabase. Member and Branch are the
/// raw values the producer-side daemon wrote — the consumer trusts those
/// without re-normalizing because the producer is the single source of
/// truth for the folder-style key.
/// </summary>
public sealed record TeamBranch(
    string Member,
    string Branch,
    string? CommitSha,
    string? CommitMessage,
    DateTime? UpdatedAt);

public sealed class TeamMemberDiscovery
{
    // Compiled once: matches any run of non-alphanumeric ASCII characters.
    // Used by NormalizeForFolder; kept here so the producer-side daemon and
    // any future bootstrap code share one normalization rule.
    private static readonly Regex NonAlphanumericRun =
        new Regex("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly SupabaseClient _supabase;

    /* ======================================================================================
     * TeamMemberDiscovery (constructor)
     * Description: Wires up the discovery service against the Supabase client. The daemon
     *              owns one shared SupabaseClient instance and hands it to every service
     *              that needs DB access — this avoids spinning up an HttpClient per cycle.
     * Parameters:
     *   supabase - configured Supabase HTTP wrapper
     * Return Values: (constructor)
     * ======================================================================================
     */
    public TeamMemberDiscovery(SupabaseClient supabase)
    {
        this._supabase = supabase;
    }

    /* ======================================================================================
     * DiscoverAsync
     * Description: Pulls every shared_contexts row and converts each into a TeamBranch.
     *              The roster is derived from the rows themselves — there is no separate
     *              "members" table — so a member who has not yet pushed a context will
     *              not appear here. That is intentional: the UI only needs to show
     *              teammates whose context is actually available to consume.
     * Parameters:
     *   ct - cancellation token; propagates into the HTTP call
     * Return Values:
     *   IReadOnlyList<TeamBranch> with one entry per row. Empty list when the network
     *   fails or no rows exist (the SupabaseClient logs the underlying failure).
     * ======================================================================================
     */
    public async Task<IReadOnlyList<TeamBranch>> DiscoverAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SharedContextRow> rows = await this._supabase.SelectAllAsync(ct).ConfigureAwait(false);
        List<TeamBranch> branches = new List<TeamBranch>(rows.Count);
        foreach (SharedContextRow row in rows)
        {
            // Reject rows missing the keys we use as identity. PostgREST will not
            // normally return such rows (NOT NULL on member, branch in schema),
            // but defending against malformed data keeps the loop robust.
            if (string.IsNullOrEmpty(row.Member) || string.IsNullOrEmpty(row.Branch))
            {
                continue;
            }
            branches.Add(new TeamBranch(
                Member: row.Member,
                Branch: row.Branch,
                CommitSha: row.CommitSha,
                CommitMessage: row.CommitMessage,
                UpdatedAt: row.UpdatedAt));
        }
        return branches;
    }

    /* ======================================================================================
     * NormalizeForFolder
     * Description: Converts an arbitrary string into a normalized identifier: lowercase
     *              ASCII alphanumerics with any run of other characters collapsed to a
     *              single '-' separator and leading/trailing '-' removed. The producer
     *              daemon uses this when writing the Member/Branch values into Supabase,
     *              so the hook (Node.js side) and daemon stay consistent.
     * Parameters:
     *   s - any string ("Chris Park", "feature/auth", null, etc.)
     * Return Values:
     *   Normalized string (e.g. "chris-park"). Empty string when input is null,
     *   whitespace, or contains no normalizable chars.
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
     * Description: Reads `git config user.name` for the given repo and normalizes it for
     *              use as the Self value in config.local.json. The daemon calls this on
     *              first run when Self is unset, so the user does not have to fill it in
     *              by hand. Returns null on any failure (missing git, unset key, name made
     *              entirely of punctuation) so the caller can fall back to a UI prompt.
     * Parameters:
     *   git      - GitProcess wrapper for invoking git
     *   repoPath - absolute path to the repo whose git config to read
     *   ct       - cancellation token; propagates into the git invocation
     * Return Values:
     *   Normalized self string when git config user.name is set; null otherwise.
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

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        string normalized = NormalizeForFolder(result.Stdout);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
