using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
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
    private ConfigStore? _configStore;
    private string? _repoRoot;
    private IntPtr _hwnd;
    private SubclassProc? _minimumSizeSubclassProc;

    private readonly ObservableCollection<string> _members = new ObservableCollection<string>();
    private readonly ObservableCollection<BranchListRow> _branches = new ObservableCollection<BranchListRow>();

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
        this.Closed += this.OnWindowClosed;
        this.StartSyncService();
    }

    private void StartSyncService()
    {
        try
        {
            string repoRoot = FindRepoRoot();
            string teamMembersPath = Path.Combine(repoRoot, "team-members");
            string configPath = Path.Combine(repoRoot, "config.local.json");
            string logPath = Path.Combine(repoRoot, ".local", "daemon.log");

            this._repoRoot = repoRoot;
            this.RepoPathText.Text = repoRoot;

            Logger.Configure(logPath);

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);
            this._configStore = configStore;

            this._syncService = new SyncService(git, configStore, repoRoot, teamMembersPath);
            this._syncService.CycleCompleted += this.OnSyncCycleCompleted;
            this._syncService.Start();

            this.LoadWorkspaceConfig();
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
        this.DispatcherQueue.TryEnqueue(() =>
        {
            string timestamp = result.CompletedAt.ToString("HH:mm:ss");
            this.LastSyncText.Text = timestamp;
            this.BranchCountText.Text = result.BranchesDiscovered.ToString();
            this.MemberCountText.Text = result.MembersTotal.ToString();
            this.FetchStatusText.Text = result.FetchSucceeded ? "Current" : "Cached refs";
            this.SyncProgress.IsActive = false;
            this.LoadWorkspaceConfig();

            if (result.HadError)
            {
                this.SetStatus(
                    "Sync error at " + timestamp,
                    result.ErrorMessage ?? "Unknown sync failure.",
                    InfoBarSeverity.Error);
                return;
            }

            string fetchSuffix = result.FetchSucceeded
                ? string.Empty
                : " Fetch failed, so cached refs were used.";
            this.SetStatus(
                "Sync complete",
                result.BranchesDiscovered + " branches, "
                + result.MembersTotal + " members."
                + fetchSuffix,
                result.FetchSucceeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        });
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
            string teamMembersPath = Path.Combine(repoRoot, "team-members");
            string configPath = Path.Combine(repoRoot, "config.local.json");

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);
            TeamMemberDiscovery discovery = new TeamMemberDiscovery(git, repoRoot, teamMembersPath);

            IReadOnlyList<TeamBranch> branches = await discovery.DiscoverAsync();
            HandoffConfig merged = configStore.Read();

            this.RenderDiscoveryDetails(merged, branches);
            this.SetStatus(
                "Discovery complete",
                "Found " + branches.Count + " branches across " + merged.TeamMembers.Count + " members.",
                result.FetchSucceeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
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
            this._branches.Add(new BranchListRow(
                branch.Member + "/" + branch.Branch,
                branch.OriginalBranch + " - " + branch.LastCommitSha + " - " + branch.LastCommitDate));
        }
    }

    private void RenderMembers(HandoffConfig config)
    {
        this.SelfText.Text = string.IsNullOrWhiteSpace(config.Self) ? "Not set" : config.Self;

        this._members.Clear();
        foreach (TeamMemberEntry member in config.TeamMembers.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            string suffix = member.Subscribe ? " (subscribed)" : " (muted)";
            this._members.Add(member.Name + suffix);
        }
    }

    private void ShellNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        this.ContentRoot.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        this.TeamRoot.Visibility = tag == "team" ? Visibility.Visible : Visibility.Collapsed;
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
