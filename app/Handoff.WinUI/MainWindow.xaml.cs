using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;

namespace Handoff.WinUI;

public sealed partial class MainWindow : Window
{
    // Owned by the window for now. Will move to App.xaml.cs once we add tray /
    // background-survives-close semantics, per the agreed migration plan.
    private SyncService? _syncService;

    public MainWindow()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
        this.StartSyncService();
    }

    /* ======================================================================================
     * StartSyncService
     * Description: Builds the daemon's collaborators (GitProcess, ConfigStore) against
     *              the discovered repo root, hooks up the CycleCompleted event for live
     *              status, and kicks off the polling loop. Failures here are surfaced
     *              into StatusText rather than thrown — a missing .git or filesystem
     *              error should not crash the window before the user can see why.
     * Parameters: (none)
     * Return Values: (none)
     * ======================================================================================
     */
    private void StartSyncService()
    {
        try
        {
            string repoRoot = FindRepoRoot();
            string teamMembersPath = Path.Combine(repoRoot, "team-members");
            string configPath = Path.Combine(repoRoot, "config.local.json");
            string logPath = Path.Combine(repoRoot, ".local", "daemon.log");

            Logger.Configure(logPath);

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);

            this._syncService = new SyncService(git, configStore, repoRoot, teamMembersPath);
            this._syncService.CycleCompleted += this.OnSyncCycleCompleted;
            this._syncService.Start();

            this.StatusText.Text = "Daemon started. First cycle running...";
        }
        catch (Exception ex)
        {
            // The daemon failed to start. Discover button still works as a manual fallback,
            // so we surface the error but keep the window usable.
            this.StatusText.Text = "Failed to start daemon: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /* ======================================================================================
     * OnSyncCycleCompleted
     * Description: CycleCompleted handler — fires from a background thread, so we
     *              must marshal back to the UI thread before touching XAML elements.
     *              Renders a one-line status of the cycle. Detailed output (member /
     *              branch lists) stays in the manual Discover button for demo use.
     * Parameters:
     *   sender - the SyncService instance (unused)
     *   result - the cycle's outcome
     * Return Values: (none)
     * ======================================================================================
     */
    private void OnSyncCycleCompleted(object? sender, SyncCycleResult result)
    {
        // Events from SyncService run on the thread pool. WinUI XAML can only be
        // touched from the dispatcher thread — TryEnqueue marshals the update.
        this.DispatcherQueue.TryEnqueue(() =>
        {
            string timestamp = result.CompletedAt.ToString("HH:mm:ss");
            if (result.HadError)
            {
                this.StatusText.Text = "Sync error at " + timestamp + ": " + result.ErrorMessage;
                return;
            }

            string fetchSuffix = result.FetchSucceeded
                ? string.Empty
                : " (fetch warning, used cached refs)";
            this.StatusText.Text =
                "Sync at " + timestamp + " - "
                + result.BranchesDiscovered + " branches, "
                + result.MembersTotal + " members"
                + fetchSuffix;
        });
    }

    /* ======================================================================================
     * OnWindowClosed
     * Description: Stops and disposes the daemon when the window is closed. Async-void
     *              is the standard pattern for event handlers; the loop's StopAsync
     *              completes within a few seconds (bounded by the cycle's git work).
     * Parameters:
     *   sender - the window (unused)
     *   args   - close event args (unused)
     * Return Values: (async void event handler — no return)
     * ======================================================================================
     */
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (this._syncService is null)
        {
            return;
        }
        try
        {
            await this._syncService.StopAsync();
        }
        catch (Exception ex)
        {
            // Window is closing — nowhere to surface this. Log and swallow.
            Logger.LogError("MainWindow", "StopAsync during close", ex);
        }
        finally
        {
            this._syncService.Dispose();
            this._syncService = null;
        }
    }

    /* ======================================================================================
     * FindRepoRoot
     * Description: Walks up from the exe's BaseDirectory looking for a directory that
     *              contains a ".git" entry, returning the first match. This is more
     *              robust than a fixed `..\..\..\..` style relative path because the
     *              depth changes with build configuration (Debug/Release/RID/framework
     *              folder), and a friend's machine with a different config would break
     *              a hardcoded depth.
     * Parameters: (none)
     * Return Values:
     *   Absolute path to the repo root (parent directory of the .git entry).
     *   Throws DirectoryNotFoundException when no .git is found above the exe.
     * ======================================================================================
     */
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // .git is a directory in normal repos; in worktrees it is a file. Either is enough
            // to identify the repo root, so we accept both forms.
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No .git found above exe at " + AppContext.BaseDirectory);
    }

    /* ======================================================================================
     * DiscoverButton_Click
     * Description: Manual on-demand sync — kept for demo & debug. Runs one cycle through
     *              SyncService.RunOnceAsync so the same SemaphoreSlim guard applies and
     *              we never race the auto-tick. After the cycle completes, this handler
     *              also reads the now-merged config and renders a detailed dump (member
     *              list, branch list) into StatusText — info the periodic cycle would
     *              otherwise summarize into a single line.
     * Parameters:
     *   sender - the Discover button (unused)
     *   e      - routed event args (unused)
     * Return Values: (async void event handler — no return)
     * ======================================================================================
     */
    private async void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        this.DiscoverButton.IsEnabled = false;
        this.StatusText.Text = "Manual sync...";

        try
        {
            if (this._syncService is null)
            {
                this.StatusText.Text = "Daemon not running — see startup error above.";
                return;
            }

            // Run via the daemon's gate so the auto-tick and this manual run cannot
            // both touch git/config simultaneously. RunOnceAsync returns null when a
            // cycle is already in flight — surface that to the user as "busy".
            SyncCycleResult? result = await this._syncService.RunOnceAsync();
            if (result is null)
            {
                this.StatusText.Text = "Auto-cycle in progress — try again in a moment.";
                return;
            }
            if (result.HadError)
            {
                this.StatusText.Text = "Sync error: " + result.ErrorMessage;
                return;
            }

            // Read the now-merged config + repeat discovery to produce the detailed
            // dump. Yes this is a second discovery pass, but it is cheap (just for-each-ref,
            // no fetch) and gives us the full per-branch info the periodic cycle drops.
            string repoRoot = FindRepoRoot();
            string teamMembersPath = Path.Combine(repoRoot, "team-members");
            string configPath = Path.Combine(repoRoot, "config.local.json");

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);
            TeamMemberDiscovery discovery = new TeamMemberDiscovery(git, repoRoot, teamMembersPath);

            IReadOnlyList<TeamBranch> branches = await discovery.DiscoverAsync();
            HandoffConfig merged = configStore.Read();

            string memberLines = string.Join(
                "\n",
                merged.TeamMembers.Select(m => "  - " + m.Name + " (subscribe=" + m.Subscribe + ")"));
            string branchLines = string.Join(
                "\n",
                branches.Select(b => "  - " + b.Member + "/" + b.Branch + "  [" + b.LastCommitSha + "]"));

            this.StatusText.Text =
                "repo: " + repoRoot + "\n" +
                "self: " + merged.Self + "\n" +
                "discovered " + branches.Count + " branches across " + merged.TeamMembers.Count + " members.\n\n" +
                "members:\n" + memberLines + "\n\n" +
                "branches:\n" + branchLines;
        }
        catch (Exception ex)
        {
            // Catch-all — the test button must surface failures to the UI rather
            // than crash the app or silently swallow them.
            this.StatusText.Text = "Error: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            this.DiscoverButton.IsEnabled = true;
        }
    }
}
