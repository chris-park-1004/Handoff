namespace Handoff.WinUI.Services;

/// <summary>
/// Result of a single SyncService polling cycle.
/// FetchSucceeded == false does not necessarily mean the cycle failed —
/// discovery may still have run against cached remote refs. ErrorMessage
/// is the canonical "did this cycle fail?" signal (null = success).
/// </summary>
public sealed record SyncCycleResult(
    bool FetchSucceeded,
    int BranchesDiscovered,
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
     * Description: Wires up the daemon against an existing GitProcess and ConfigStore.
     *              The discovery instance is created internally — callers that need a
     *              different exclusion set can extend this constructor later. The loop
     *              does NOT start until Start() is called, so a caller can subscribe to
     *              CycleCompleted before the first cycle fires.
     * Parameters:
     *   git              - GitProcess wrapper for invoking git commands
     *   configStore      - ConfigStore bound to the user's config.local.json path
     *   userRepoPath     - absolute path to the user's main repo (where origin lives)
     *   teamMembersPath  - absolute path to the team-members/ folder for EnsureFolders
     *   interval         - polling cadence; defaults to 30s when null
     * Return Values: (constructor)
     * ======================================================================================
     */
    public SyncService(
        GitProcess git,
        ConfigStore configStore,
        string userRepoPath,
        string teamMembersPath,
        TimeSpan? interval = null)
    {
        this._git = git;
        this._configStore = configStore;
        this._userRepoPath = userRepoPath;
        this._discovery = new TeamMemberDiscovery(git, userRepoPath, teamMembersPath);
        this._interval = interval ?? DefaultInterval;
    }

    /* ======================================================================================
     * Start
     * Description: Kicks off the polling loop on the thread pool. No-op-but-throws if
     *              the service was already started. The loop runs until StopAsync (or
     *              Dispose) is called, surfacing each cycle's result via CycleCompleted.
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
     *   ct - cancellation token; cancellation propagates into the underlying git/file ops
     * Return Values:
     *   SyncCycleResult on success/failure of this cycle, or null if another cycle was
     *   in progress and this call did nothing.
     * ======================================================================================
     */
    public async Task<SyncCycleResult?> RunOnceAsync(CancellationToken ct = default)
    {
        // Try-acquire with zero wait — if the loop is mid-cycle, return null so the
        // caller can present "busy" rather than queuing a redundant pass.
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
        // First-run bootstrap. Wrapped so a transient git failure here does not kill
        // the loop — config can still be created later when a user provides Self via UI.
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
            // Non-blocking acquire. If the previous cycle is still in flight (e.g. user
            // clicked Discover button and it's mid-execution), we skip this tick rather
            // than serialize behind it.
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

            // Inter-tick sleep — cancellation throws OperationCanceledException which
            // we catch to break out of the loop cleanly.
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
     *                1. fetch origin (best-effort — partial cycle still useful)
     *                2. discover branches
     *                3. ensure team-members/{member}/{branch}/ folders
     *                4. merge new member names into config.local.json
     *              All failures are caught and packed into the returned SyncCycleResult,
     *              so the loop above can report and continue without dying.
     * Parameters:
     *   ct - cancellation token; propagates into git invocations
     * Return Values:
     *   SyncCycleResult describing what happened this cycle.
     * ======================================================================================
     */
    private async Task<SyncCycleResult> RunCycleAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: refresh remote refs. We do not abort the cycle on fetch failure
            // because cached refs still allow folder/config maintenance against the
            // last known state — better than skipping the whole tick.
            GitResult fetchResult = await this._git.RunAsync(
                this._userRepoPath,
                new[] { "fetch", "origin", "--quiet" },
                ct).ConfigureAwait(false);
            bool fetchOk = fetchResult.Success;
            if (!fetchOk)
            {
                Logger.Log("SyncService", "fetch failed (continuing): " + fetchResult.Stderr);
            }

            // Step 2 + 3: walk for-each-ref and create folders. Discovery already
            // tolerates missing/malformed lines internally, so an empty list here
            // genuinely means "nothing to track" rather than "something broke".
            IReadOnlyList<TeamBranch> branches = await this._discovery.DiscoverAsync(ct).ConfigureAwait(false);
            this._discovery.EnsureFolders(branches);

            // Step 4: merge unique member names into the roster (self filtered inside
            // ConfigStore). Future daemon work (commit watching, summary write,
            // push) plugs in here, before the result is reported.
            IEnumerable<string> uniqueMembers = branches.Select(b => b.Member).Distinct();
            HandoffConfig merged = this._configStore.MergeDiscoveredMembers(uniqueMembers);

            return new SyncCycleResult(
                FetchSucceeded: fetchOk,
                BranchesDiscovered: branches.Count,
                MembersTotal: merged.TeamMembers.Count,
                CompletedAt: DateTime.Now,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation up — the loop interprets it as "exit cleanly".
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("SyncService", "Cycle", ex);
            return new SyncCycleResult(
                FetchSucceeded: false,
                BranchesDiscovered: 0,
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
            // Bounded wait — we can't block indefinitely during disposal.
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
