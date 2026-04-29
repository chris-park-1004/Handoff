using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;

namespace Handoff.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
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
     * Description: Test-only handler — runs one discovery cycle end-to-end:
     *                1. resolves repo root from exe location
     *                2. ensures config.local.json exists, bootstrapping Self from
     *                   `git config user.name` if it has to create the file
     *                3. fetches origin
     *                4. parses remote refs into TeamBranch entries
     *                5. creates team-members/{member}/{branch}/ folders
     *                6. merges new member names into config
     *              The result is dumped into StatusText. The button is disabled while
     *              running so a user cannot trigger overlapping cycles.
     * Parameters:
     *   sender - the Discover button (unused)
     *   e      - routed event args (unused)
     * Return Values: (async void event handler — no return)
     * ======================================================================================
     */
    private async void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        this.DiscoverButton.IsEnabled = false;
        this.StatusText.Text = "Discovering...";

        try
        {
            string repoRoot = FindRepoRoot();
            string teamMembersPath = Path.Combine(repoRoot, "team-members");
            string configPath = Path.Combine(repoRoot, "config.local.json");

            GitProcess git = new GitProcess();
            ConfigStore configStore = new ConfigStore(configPath);

            // First-run bootstrap. Only fires when the file is missing — afterwards
            // the user (or UI) owns the Self value and we never overwrite it.
            if (!File.Exists(configPath))
            {
                string? gitUserName = await TeamMemberDiscovery.GetGitUserNameAsync(git, repoRoot);
                configStore.EnsureExists(gitUserName);
            }

            // Refresh remote refs before scanning. If fetch fails (offline, auth, etc.)
            // we still attempt discovery against the cached remote refs — partial data
            // is better than nothing for a test cycle.
            GitResult fetchResult = await git.RunAsync(repoRoot, new[] { "fetch", "origin", "--quiet" });
            if (!fetchResult.Success)
            {
                this.StatusText.Text = "fetch warning (continuing with cached refs): " + fetchResult.Stderr;
            }

            TeamMemberDiscovery discovery = new TeamMemberDiscovery(git, repoRoot, teamMembersPath);
            IReadOnlyList<TeamBranch> branches = await discovery.DiscoverAsync();
            discovery.EnsureFolders(branches);

            IEnumerable<string> uniqueMembers = branches.Select(b => b.Member).Distinct();
            HandoffConfig merged = configStore.MergeDiscoveredMembers(uniqueMembers);

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
