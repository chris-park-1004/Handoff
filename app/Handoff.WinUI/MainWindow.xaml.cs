using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Handoff.WinUI.Models;
using Handoff.WinUI.Pages;
using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Handoff.WinUI;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 1100;
    private const int MinimumWindowHeight = 720;
    private const uint MinimumSizeSubclassId = 1;
    private const uint WmGetMinMaxInfo = 0x0024;

    // Owned by the window for now. Will move to App.xaml.cs once tray /
    // background-survives-close semantics are added.
    private SyncService? _syncService;
    private SupabaseClient? _supabase;
    private GitHubClient? _github;
    private ConfigStore? _configStore;
    private string? _repoRoot;
    private IntPtr _hwnd;
    private SubclassProc? _minimumSizeSubclassProc;

    private readonly ObservableCollection<string> _members = new ObservableCollection<string>();
    private readonly ObservableCollection<BranchListRow> _branches = new ObservableCollection<BranchListRow>();
    private IReadOnlyList<TeamMember> _teamMetadata = Array.Empty<TeamMember>();

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hwnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    public MainWindow()
    {
        this.InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);
        this.ConfigureMinimumWindowSize();
        this.MembersList.ItemsSource = this._members;
        this.BranchesList.ItemsSource = this._branches;
        this.TeamRoot.SubscriptionChanged += this.OnTeamSubscriptionChanged;
        this.Closed += this.OnWindowClosed;
        this.StartSyncService();
    }

    private void StartSyncService()
    {
        try
        {
            string repoRoot = FindRepoRoot();
            string configPath = Path.Combine(repoRoot, "config.local.json");
            string logPath = Path.Combine(repoRoot, ".local", "daemon.log");

            this._repoRoot = repoRoot;
            this.RepoPathText.Text = repoRoot;

            Logger.Configure(logPath);

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);
            this._configStore = configStore;

            // SupabaseClient is built from whatever is currently in config.local.json.
            // Empty url/key short-circuit to no-ops in the client itself, so a half-
            // configured install still boots; the user just sees empty sync results.
            HandoffConfig snapshot = configStore.Read();
            string supabaseUrl = snapshot.Supabase?.Url ?? string.Empty;
            string supabaseKey = snapshot.Supabase?.Key ?? string.Empty;
            this._supabase = new SupabaseClient(supabaseUrl, supabaseKey);
            this._github = new GitHubClient();

            this._syncService = new SyncService(git, configStore, this._supabase, this._github, repoRoot);
            this._syncService.CycleCompleted += this.OnSyncCycleCompleted;
            this._syncService.Start();

            this.LoadWorkspaceConfig();
            _ = this.RefreshTeamAsync();
            this.SetStatus("Daemon started", "First sync cycle is running.", InfoBarSeverity.Informational);
            this.SyncProgress.IsActive = true;
        }
        catch (Exception ex)
        {
            this.SetStatus(
                "Failed to start daemon",
                ex.GetType().Name + ": " + ex.Message,
                InfoBarSeverity.Error);
            this.SyncProgress.IsActive = false;
        }
    }

    private void OnSyncCycleCompleted(object? sender, SyncCycleResult result)
    {
        // Marshal back to the UI thread before touching XAML. Inside the lambda we
        // also fire-and-forget a contexts fetch; the daemon's RunCycleAsync only
        // pulls team_members, so branches/shared_contexts have to be queried
        // separately when the UI wants them.
        this.DispatcherQueue.TryEnqueue(async () =>
        {
            string timestamp = result.CompletedAt.ToString("HH:mm:ss");
            this.LastSyncText.Text = timestamp;
            this.MemberCountText.Text = result.MembersTotal.ToString();
            // Branch count is overwritten below once the contexts fetch lands;
            // showing the member-cycle's row count first would be misleading.
            this.BranchCountText.Text = "...";
            // The label still reads "Fetch" in XAML; we repurpose it as the Supabase
            // reachability indicator since the legacy git fetch step no longer exists.
            this.FetchStatusText.Text = result.SupabaseReachable ? "Current" : "Supabase unreachable";
            this.SyncProgress.IsActive = false;
            this.LoadWorkspaceConfig();
            await this.RefreshTeamAsync();

            if (result.HadError)
            {
                this.SetStatus(
                    "Sync error at " + timestamp,
                    result.ErrorMessage ?? "Unknown sync failure.",
                    InfoBarSeverity.Error);
                return;
            }

            string suffix = result.SupabaseReachable
                ? string.Empty
                : " Supabase unreachable; using cached roster.";
            this.SetStatus(
                "Sync complete",
                result.MembersTotal + " members."
                + suffix,
                result.SupabaseReachable ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

            // Pull shared_contexts to render the branches panel. Failures fall
            // through silently — the panel keeps its last-known content rather
            // than blanking out on a transient Supabase blip.
            await this.RefreshBranchesAsync();
            await this.RefreshActivityAsync(result.SupabaseReachable);
        });
    }

    private async Task RefreshTeamAsync()
    {
        if (this._configStore is null)
        {
            return;
        }

        try
        {
            if (this._supabase is not null && this._supabase.IsConfigured())
            {
                this._teamMetadata = await this._supabase.SelectTeamMembersAsync();
            }

            HandoffConfig config = this._configStore.Read();
            this.TeamRoot.RenderTeam(config, this._teamMetadata);
        }
        catch (Exception ex)
        {
            Logger.LogError("MainWindow", "RefreshTeam", ex);
            HandoffConfig config = this._configStore.Read();
            this.TeamRoot.RenderTeam(config, this._teamMetadata);
        }
    }

    /* ======================================================================================
     * RefreshBranchesAsync
     * Description: Queries shared_contexts via the live SupabaseClient and renders the
     *              result into the BranchesList. Called from the cycle-completed handler
     *              so the panel updates automatically every 30s, not only on manual
     *              Discover clicks. Errors are swallowed (logged via the inner client)
     *              because this runs on the UI thread; we never want a refresh failure
     *              to crash the window.
     * Parameters: (none)
     * Return Values:
     *   Task that completes when the fetch + render is done.
     * ======================================================================================
     */
    private async Task RefreshBranchesAsync()
    {
        if (this._supabase is null)
        {
            return;
        }
        try
        {
            TeamMemberDiscovery discovery = new TeamMemberDiscovery(this._supabase);
            IReadOnlyList<TeamBranch> rows = await discovery.DiscoverContextsAsync();

            this._branches.Clear();
            foreach (TeamBranch row in rows
                .OrderBy(r => r.Member, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Branch, StringComparer.OrdinalIgnoreCase))
            {
                string sha = string.IsNullOrEmpty(row.CommitSha) ? "(no sha)" : row.CommitSha;
                string ts = row.UpdatedAt.HasValue
                    ? row.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "(no timestamp)";
                this._branches.Add(new BranchListRow(
                    row.Member + "/" + row.Branch,
                    sha + " - " + ts));
            }

            this.BranchCountText.Text = rows.Count.ToString();
        }
        catch (Exception ex)
        {
            // Swallow — the SupabaseClient already logged the underlying failure;
            // the panel just keeps its previous state on this cycle.
            Logger.LogError("MainWindow", "RefreshBranches", ex);
        }
    }

    private async Task RefreshActivityAsync(bool supabaseReachable)
    {
        if (this._supabase is null)
        {
            return;
        }

        try
        {
            this.ActivityRoot.RenderLoading();
            IReadOnlyList<SharedContext> rows = await this._supabase.SelectAllAsync();
            this.ActivityRoot.RenderSharedContexts(rows, supabaseReachable);
        }
        catch (Exception ex)
        {
            Logger.LogError("MainWindow", "RefreshActivity", ex);
            this.ActivityRoot.RenderSharedContexts(Array.Empty<SharedContext>(), false);
        }
    }

    private void ConfigureMinimumWindowSize()
    {
        this._hwnd = WindowNative.GetWindowHandle(this);
        this._minimumSizeSubclassProc = this.MinimumSizeWindowProc;
        SetWindowSubclass(
            this._hwnd,
            this._minimumSizeSubclassProc,
            new UIntPtr(MinimumSizeSubclassId),
            UIntPtr.Zero);
    }

    private IntPtr MinimumSizeWindowProc(
        IntPtr hwnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize.X = MinimumWindowWidth;
            info.MinTrackSize.Y = MinimumWindowHeight;
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (this._hwnd != IntPtr.Zero && this._minimumSizeSubclassProc is not null)
        {
            RemoveWindowSubclass(
                this._hwnd,
                this._minimumSizeSubclassProc,
                new UIntPtr(MinimumSizeSubclassId));
            this._minimumSizeSubclassProc = null;
            this._hwnd = IntPtr.Zero;
        }

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
            Logger.LogError("MainWindow", "StopAsync during close", ex);
        }
        finally
        {
            this._syncService.Dispose();
            this._syncService = null;
            // SupabaseClient + GitHubClient each own an HttpClient — dispose alongside
            // the daemon. After this returns the window has no live network resources.
            this._supabase?.Dispose();
            this._supabase = null;
            this._github?.Dispose();
            this._github = null;
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("No .git found above exe at " + AppContext.BaseDirectory);
    }

    private async void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        this.DiscoverButton.IsEnabled = false;
        this.SyncProgress.IsActive = true;
        this.SetStatus("Manual sync", "Discovering team members and branches.", InfoBarSeverity.Informational);

        try
        {
            if (this._syncService is null)
            {
                this.SetStatus("Daemon not running", "See the startup error above.", InfoBarSeverity.Error);
                return;
            }

            SyncCycleResult? result = await this._syncService.RunOnceAsync();
            if (result is null)
            {
                this.SetStatus("Sync busy", "Auto-cycle in progress. Try again in a moment.", InfoBarSeverity.Warning);
                return;
            }

            if (result.HadError)
            {
                this.SetStatus("Sync error", result.ErrorMessage ?? "Unknown sync failure.", InfoBarSeverity.Error);
                return;
            }

            string repoRoot = this._repoRoot ?? FindRepoRoot();
            string configPath = Path.Combine(repoRoot, "config.local.json");

            ConfigStore configStore = new ConfigStore(configPath);
            HandoffConfig merged = configStore.Read();

            // Re-discover via the existing Supabase client so we don't open a second
            // HttpClient or duplicate the daemon's network state.
            IReadOnlyList<TeamBranch> branches = Array.Empty<TeamBranch>();
            if (this._supabase is not null)
            {
                TeamMemberDiscovery discovery = new TeamMemberDiscovery(this._supabase);
                branches = await discovery.DiscoverContextsAsync();
            }

            this.RenderDiscoveryDetails(merged, branches);
            await this.RefreshTeamAsync();
            await this.RefreshActivityAsync(result.SupabaseReachable);
            this.SetStatus(
                "Discovery complete",
                "Found " + branches.Count + " rows across " + merged.TeamMembers.Count + " members.",
                result.SupabaseReachable ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            this.SetStatus("Discovery failed", ex.GetType().Name + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            this.SyncProgress.IsActive = false;
            this.DiscoverButton.IsEnabled = true;
        }
    }

    private void LoadWorkspaceConfig()
    {
        HandoffConfig? config = this._configStore?.Read();
        if (config is null)
        {
            return;
        }

        this.RenderMembers(config);
    }

    private void RenderDiscoveryDetails(HandoffConfig config, IReadOnlyList<TeamBranch> branches)
    {
        this.RenderMembers(config);
        this.RepoPathText.Text = this._repoRoot ?? FindRepoRoot();
        this.BranchCountText.Text = branches.Count.ToString();
        this.MemberCountText.Text = config.TeamMembers.Count.ToString();

        this._branches.Clear();
        foreach (TeamBranch branch in branches
            .OrderBy(b => b.Member, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Branch, StringComparer.OrdinalIgnoreCase))
        {
            // TeamBranch was redesigned around Supabase columns: branch is the raw
            // branch string already, and timestamp comes from the row's UpdatedAt.
            // Fall back to "(no commit)" when the producer hasn't pushed metadata yet.
            string sha = string.IsNullOrEmpty(branch.CommitSha) ? "(no sha)" : branch.CommitSha;
            string ts = branch.UpdatedAt.HasValue
                ? branch.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "(no timestamp)";
            this._branches.Add(new BranchListRow(
                branch.Member + "/" + branch.Branch,
                sha + " - " + ts));
        }
    }

    private void RenderMembers(HandoffConfig config)
    {
        this.SelfText.Text = string.IsNullOrWhiteSpace(config.Self) ? "Not set" : config.Self;

        this.RenderDashboardMembers(config);
        this.TeamRoot.RenderTeam(config, this._teamMetadata);
    }

    private void RenderDashboardMembers(HandoffConfig config)
    {
        this._members.Clear();
        foreach (TeamMemberEntry member in config.TeamMembers.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            string suffix = member.Subscribe ? " (subscribed)" : " (muted)";
            this._members.Add(member.Name + suffix);
        }
    }

    private void OnTeamSubscriptionChanged(object? sender, TeamSubscriptionChangedEventArgs e)
    {
        if (this._configStore is null)
        {
            return;
        }

        HandoffConfig config = this._configStore.Read();
        TeamMemberEntry? entry = config.TeamMembers.FirstOrDefault(m =>
            string.Equals(m.Name, e.Name, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            entry = new TeamMemberEntry
            {
                Name = e.Name,
            };
            config.TeamMembers.Add(entry);
        }

        entry.Subscribe = e.Subscribe;
        this._configStore.Write(config);
        this.RenderDashboardMembers(config);
    }

    private void ShellNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        this.ContentRoot.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        this.TeamRoot.Visibility = tag == "team" ? Visibility.Visible : Visibility.Collapsed;
        this.ActivityRoot.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        this.AboutRoot.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        this.StatusInfoBar.Title = title;
        this.StatusInfoBar.Message = message;
        this.StatusInfoBar.Severity = severity;
        this.StatusInfoBar.IsOpen = true;
        this.StatusText.Text = title + ": " + message;
    }
}

public sealed class BranchListRow
{
    public BranchListRow(string title, string subtitle)
    {
        this.Title = title;
        this.Subtitle = subtitle;
    }

    public string Title { get; }

    public string Subtitle { get; }
}

internal delegate IntPtr SubclassProc(
    IntPtr hwnd,
    uint message,
    UIntPtr wParam,
    IntPtr lParam,
    UIntPtr subclassId,
    UIntPtr refData);

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;

    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MinMaxInfo
{
    public Point Reserved;

    public Point MaxSize;

    public Point MaxPosition;

    public Point MinTrackSize;

    public Point MaxTrackSize;
}
