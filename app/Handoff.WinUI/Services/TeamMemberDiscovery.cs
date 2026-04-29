using System.Text.RegularExpressions;
using Handoff.WinUI.Models;

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
     * DiscoverMembersAsync
     * Description: Pulls every row from team_members. This is the canonical roster
     *              of teammates — a member shows up here as soon as the producer-side
     *              daemon registers them, before any shared_contexts row exists, so the
     *              UI can render an empty entry for someone who has just joined.
     * Parameters:
     *   ct - cancellation token; propagates into the HTTP call
     * Return Values:
     *   IReadOnlyList<TeamMember> with one entry per row. Empty list on network failure.
     * ======================================================================================
     */
    public async Task<IReadOnlyList<TeamMember>> DiscoverMembersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TeamMember> rows = await this._supabase.SelectTeamMembersAsync(ct).ConfigureAwait(false);
        List<TeamMember> members = new List<TeamMember>(rows.Count);
        foreach (TeamMember row in rows)
        {
            // Defensive: PostgREST should never hand us a primary-key-empty row,
            // but a malformed entry must not poison the whole roster.
            if (string.IsNullOrEmpty(row.Name))
            {
                continue;
            }
            members.Add(row);
        }
        return members;
    }

    /* ======================================================================================
     * DiscoverContextsAsync
     * Description: Pulls every shared_contexts row and converts each into a TeamBranch.
     *              Used for the manual Discover button to show "what context is available
     *              right now" — the periodic cycle uses DiscoverMembersAsync instead.
     * Parameters:
     *   ct - cancellation token; propagates into the HTTP call
     * Return Values:
     *   IReadOnlyList<TeamBranch> with one entry per shared_contexts row.
     * ======================================================================================
     */
    public async Task<IReadOnlyList<TeamBranch>> DiscoverContextsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SharedContext> rows = await this._supabase.SelectAllAsync(ct).ConfigureAwait(false);
        List<TeamBranch> branches = new List<TeamBranch>(rows.Count);
        foreach (SharedContext row in rows)
        {
            if (string.IsNullOrEmpty(row.MemberName) || string.IsNullOrEmpty(row.Branch))
            {
                continue;
            }
            branches.Add(new TeamBranch(
                Member: row.MemberName,
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
     * GetGitUserEmailAsync
     * Description: Reads `git config user.email` for the given repo. Used by the daemon
     *              to populate the email column on the user's own team_members row.
     *              Returns null on any failure (missing git, unset key) so the caller
     *              can simply leave the column null rather than fail the cycle.
     * Parameters:
     *   git      - GitProcess wrapper for invoking git
     *   repoPath - absolute path to the repo whose git config to read
     *   ct       - cancellation token; propagates into the git invocation
     * Return Values:
     *   The email string when set; null otherwise.
     * ======================================================================================
     */
    public static async Task<string?> GetGitUserEmailAsync(
        GitProcess git,
        string repoPath,
        CancellationToken ct = default)
    {
        GitResult result = await git.RunAsync(
            repoPath,
            new[] { "config", "user.email" },
            ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }
        return result.Stdout.Trim();
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
