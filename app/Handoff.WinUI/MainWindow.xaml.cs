using System.Collections.ObjectModel;
using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI;

public sealed partial class MainWindow : Window
{
    // Owned by the window for now. Will move to App.xaml.cs once tray /
    // background-survives-close semantics are added.
    private SyncService? _syncService;
    private ConfigStore? _configStore;
    private string? _repoRoot;

    private readonly ObservableCollection<string> _members = new ObservableCollection<string>();
    private readonly ObservableCollection<BranchListRow> _branches = new ObservableCollection<BranchListRow>();

    public MainWindow()
    {
        this.InitializeComponent();
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
