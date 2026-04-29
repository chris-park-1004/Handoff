using Handoff.WinUI.Models;

namespace Handoff.WinUI.Services;

/// <summary>
/// Result of a single SyncService polling cycle.
/// SupabaseReachable == false does not necessarily mean the cycle failed —
/// the cached roster in config.local.json is still usable, and the next
/// tick may recover. ErrorMessage is the canonical "did this cycle fail?"
/// signal (null = success).
/// </summary>
public sealed record SyncCycleResult(
    bool SupabaseReachable,
    int RowsDiscovered,
    int MembersTotal,
    DateTime CompletedAt,
    string? ErrorMessage)
{
    public bool HadError => this.ErrorMessage is not null;
}

public sealed class SyncService : IDisposable
{
    // Default polling cadence — 30s as per the design agreement. Overridable via
    // the constructor for tests or different deployment profiles.
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly GitProcess _git;
    private readonly ConfigStore _configStore;
    private readonly SupabaseClient _supabase;
    private readonly GitHubClient _github;
    private readonly TeamMemberDiscovery _discovery;
    private readonly string _userRepoPath;
    private readonly TimeSpan _interval;

    // SemaphoreSlim(1, 1) used as a non-reentrant cycle gate. WaitAsync(0)
    // returns false immediately if a cycle is already running, so we can SKIP
    // overlapping ticks instead of queuing them.
    private readonly SemaphoreSlim _cycleLock = new SemaphoreSlim(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private bool _disposed;

    /// <summary>Fires once per completed cycle (success or failure). Marshal to UI thread before touching XAML.</summary>
    public event EventHandler<SyncCycleResult>? CycleCompleted;

    /* ======================================================================================
     * SyncService (constructor)
     * Description: Wires up the daemon against an existing GitProcess, ConfigStore, and
     *              SupabaseClient. GitProcess is retained only for the first-run
     *              `git config user.name` lookup that bootstraps Self in config.local.json
     *              — the cycle itself no longer touches git. Discovery is created
     *              internally so callers do not have to manage one extra dependency.
     * Parameters:
     *   git           - GitProcess wrapper (used only for the bootstrap user.name read)
     *   configStore   - ConfigStore bound to the user's config.local.json path
     *   supabase      - configured Supabase HTTP wrapper
     *   userRepoPath  - absolute path to the user's main repo (passed through to git config)
     *   interval      - polling cadence; defaults to 30s when null
     * Return Values: (constructor)
     * ======================================================================================
     */
    public SyncService(
        GitProcess git,
        ConfigStore configStore,
        SupabaseClient supabase,
        GitHubClient github,
        string userRepoPath,
        TimeSpan? interval = null)
    {
        this._git = git;
        this._configStore = configStore;
        this._supabase = supabase;
        this._github = github;
        this._userRepoPath = userRepoPath;
        this._discovery = new TeamMemberDiscovery(supabase);
        this._interval = interval ?? DefaultInterval;
    }

    /* ======================================================================================
     * Start
     * Description: Kicks off the polling loop on the thread pool. Throws if the service
     *              was already started. The loop runs until StopAsync (or Dispose) is
     *              called, surfacing each cycle's result via CycleCompleted.
     * Parameters: (none)
     * Return Values: (none)
     * ======================================================================================
     */
    public void Start()
    {
        if (this._runLoop != null)
        {
            throw new InvalidOperationException("SyncService is already running.");
        }
        this._cts = new CancellationTokenSource();
        // Task.Run pushes the loop onto the thread pool — important because some callers
        // (e.g. MainWindow ctor) live on the UI thread, and we must not block it.
        this._runLoop = Task.Run(() => this.RunLoopAsync(this._cts.Token));
        Logger.Log("SyncService", "Started (interval=" + this._interval.TotalSeconds + "s)");
    }

    /* ======================================================================================
     * StopAsync
     * Description: Requests shutdown via the cancellation token and awaits the loop's
     *              exit. Safe to call when not running (no-op). After this returns the
     *              service can be Started again with a fresh CTS.
     * Parameters: (none)
     * Return Values:
     *   Task that completes when the loop has fully exited.
     * ======================================================================================
     */
    public async Task StopAsync()
    {
        if (this._runLoop == null)
        {
            return;
        }
        Logger.Log("SyncService", "Stopping...");
        try
        {
            this._cts?.Cancel();
            await this._runLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the loop exits via cancellation. Not an error.
        }
        catch (Exception ex)
        {
            Logger.LogError("SyncService", "StopAsync", ex);
        }
        finally
        {
            this._cts?.Dispose();
            this._cts = null;
            this._runLoop = null;
            Logger.Log("SyncService", "Stopped");
        }
    }

    /* ======================================================================================
     * RunOnceAsync
     * Description: Runs a single cycle on demand (e.g., from a "Discover" button) while
     *              respecting the same SemaphoreSlim guard as the timer loop. Returns
     *              null when a cycle is ALREADY running, so the caller can show "busy"
     *              instead of stomping on disk state.
     * Parameters:
     *   ct - cancellation token; propagates into the underlying HTTP call
     * Return Values:
     *   SyncCycleResult on success/failure of this cycle, or null if another cycle was
     *   in progress and this call did nothing.
     * ======================================================================================
     */
    public async Task<SyncCycleResult?> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await this._cycleLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            SyncCycleResult result = await this.RunCycleAsync(ct).ConfigureAwait(false);
            this.CycleCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            this._cycleLock.Release();
        }
    }

    /* ======================================================================================
     * RunLoopAsync
     * Description: The polling driver. Runs first-run bootstrap, then loops:
     *                - try to acquire the cycle lock without waiting (skip tick if busy)
     *                - run a cycle, fire CycleCompleted
     *                - sleep until the next tick (cancellation interrupts immediately)
     *              Per-cycle exceptions are caught and surfaced via SyncCycleResult so
     *              the loop itself stays alive and self-heals on the next tick.
     * Parameters:
     *   ct - cancellation token from the owning CancellationTokenSource
     * Return Values: (Task — completes when the loop exits cleanly)
     * ======================================================================================
     */
    private async Task RunLoopAsync(CancellationToken ct)
    {
        // First-run bootstrap of Self. Wrapped so a transient git failure here does not
        // kill the loop — the user can later set Self via the UI and the daemon will
        // pick it up on the next read.
        try
        {
            if (!this._configStore.Exists())
            {
                Logger.Log("SyncService", "Bootstrap: config missing, deriving self from git");
                string? gitUserName = await TeamMemberDiscovery
                    .GetGitUserNameAsync(this._git, this._userRepoPath, ct)
                    .ConfigureAwait(false);
                this._configStore.EnsureExists(gitUserName);
            }
            else
            {
                Logger.Log("SyncService", "Bootstrap: config exists, skipping");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError("SyncService", "Bootstrap", ex);
        }

        while (!ct.IsCancellationRequested)
        {
            bool acquired = await this._cycleLock.WaitAsync(0, ct).ConfigureAwait(false);
            if (acquired)
            {
                try
                {
                    SyncCycleResult result = await this.RunCycleAsync(ct).ConfigureAwait(false);
                    this.CycleCompleted?.Invoke(this, result);
                }
                finally
                {
                    this._cycleLock.Release();
                }
            }
            else
            {
                Logger.Log("SyncService", "Tick skipped (previous cycle still running)");
            }

            try
            {
                await Task.Delay(this._interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /* ======================================================================================
     * RunCycleAsync
     * Description: One end-to-end discovery pass:
     *                1. Pull all rows from Supabase via TeamMemberDiscovery
     *                2. Merge unique member names into config.local.json (self filtered
     *                   out inside ConfigStore)
     *              All failures are caught and packed into the returned SyncCycleResult,
     *              so the loop above can report and continue without dying.
     * Parameters:
     *   ct - cancellation token; propagates into HTTP call
     * Return Values:
     *   SyncCycleResult describing what happened this cycle.
     * ======================================================================================
     */
    private async Task<SyncCycleResult> RunCycleAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: register every known login in team_members. We pull profiles
            // from GitHub (login, email, avatar_url) and upsert each — this is what
            // populates the table with metadata. Sources of "known logins":
            //   - Self from config (always upserted, even if alone)
            //   - Every name in config.local.json["team-members"] (locally observed roster)
            // The user is asking why their daemon should also push others: because
            // GitHub profiles are public and one teammate running the daemon
            // populates everyone's row, removing the chicken-and-egg of "nobody
            // sees anyone until each individual runs their own daemon".
            HandoffConfig snapshot = this._configStore.Read();
            HashSet<string> knownLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(snapshot.Self))
            {
                knownLogins.Add(snapshot.Self);
            }
            foreach (TeamMemberEntry entry in snapshot.TeamMembers)
            {
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    knownLogins.Add(entry.Name);
                }
            }

            if (this._supabase.IsConfigured())
            {
                foreach (string login in knownLogins)
                {
                    GitHubProfile? profile = await this._github
                        .FetchProfileAsync(login, ct).ConfigureAwait(false);

                    // For self, fall back to git config user.email when GitHub has
                    // hidden it. For other logins, leave email null — we cannot
                    // discover their email without authentication.
                    string? email = profile?.Email;
                    if (string.IsNullOrEmpty(email) && string.Equals(login, snapshot.Self, StringComparison.OrdinalIgnoreCase))
                    {
                        email = await TeamMemberDiscovery
                            .GetGitUserEmailAsync(this._git, this._userRepoPath, ct)
                            .ConfigureAwait(false);
                    }

                    // Avatar: prefer GitHub-returned URL (handles renames + custom uploads);
                    // fall back to the login-based pattern when the API call failed.
                    string avatar = !string.IsNullOrEmpty(profile?.AvatarUrl)
                        ? profile!.AvatarUrl!
                        : "https://github.com/" + login + ".png";

                    TeamMember row = new TeamMember
                    {
                        Name = login,
                        Email = email,
                        AvatarUrl = avatar,
                    };
                    await this._supabase.UpsertTeamMemberAsync(row, ct).ConfigureAwait(false);
                }
            }

            // Step 2: pull the roster (now includes everyone we just pushed).
            IReadOnlyList<TeamMember> members = await this._discovery.DiscoverMembersAsync(ct).ConfigureAwait(false);
            bool reachable = this._supabase.IsConfigured();

            IEnumerable<string> uniqueNames = members.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n));
            HandoffConfig merged = this._configStore.MergeDiscoveredMembers(uniqueNames);

            return new SyncCycleResult(
                SupabaseReachable: reachable,
                RowsDiscovered: members.Count,
                MembersTotal: merged.TeamMembers.Count,
                CompletedAt: DateTime.Now,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("SyncService", "Cycle", ex);
            return new SyncCycleResult(
                SupabaseReachable: false,
                RowsDiscovered: 0,
                MembersTotal: 0,
                CompletedAt: DateTime.Now,
                ErrorMessage: ex.Message);
        }
    }

    /* ======================================================================================
     * Dispose
     * Description: Best-effort synchronous cleanup. Prefer StopAsync() for graceful
     *              shutdown — Dispose is the fallback for paths that cannot await
     *              (e.g., finalizers, IDisposable using-blocks). Waits up to 5 seconds
     *              for the loop to exit before disposing the underlying resources.
     * Parameters: (none)
     * Return Values: (none)
     * ======================================================================================
     */
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }
        this._disposed = true;

        try
        {
            this._cts?.Cancel();
            this._runLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Logger.LogError("SyncService", "Dispose ignored", ex);
        }

        this._cts?.Dispose();
        this._cycleLock.Dispose();
    }
}
