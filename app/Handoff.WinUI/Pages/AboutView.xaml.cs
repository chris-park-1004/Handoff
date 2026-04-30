using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI.Pages;

public sealed partial class AboutView : UserControl
{
    public AboutView()
    {
        this.InitializeComponent();
        this.PopulateBuild();
        this.PopulateRuntime();
        this.PopulateWorkspace();
    }

    private void PopulateBuild()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        AssemblyName name = asm.GetName();

        string version = name.Version?.ToString() ?? "0.0.0.0";
        string commit = ReadGitCommit() ?? "unknown";
        string built = ReadBuildTimestamp(asm);
        string config = IsDebug() ? "Debug" : "Release";

        this.AssemblyText.Text = name.Name ?? "Handoff.WinUI";
        this.VersionText.Text = version;
        this.CommitText.Text = commit;
        this.BuiltText.Text = built;
        this.ConfigurationText.Text = config;

        this.VersionBadge.Text = "v" + (name.Version is { } v ? v.Major + "." + v.Minor + "." + v.Build : "0.0.0");
        this.CommitBadge.Text = commit.Length > 7 ? commit.Substring(0, 7) : commit;
        this.ConfigurationBadge.Text = config;
    }

    private void PopulateRuntime()
    {
        this.DotnetText.Text = RuntimeInformation.FrameworkDescription;
        this.OsText.Text = RuntimeInformation.OSDescription;
        this.ArchText.Text = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            + " / " + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        Process current = Process.GetCurrentProcess();
        this.ProcessText.Text = "pid " + current.Id + " · " + current.ProcessName;
        this.HostText.Text = Environment.MachineName + " · " + Environment.UserName;
    }

    private void PopulateWorkspace()
    {
        string? repoRoot = TryFindRepoRoot();
        if (repoRoot is null)
        {
            this.RepoText.Text = "(not in a git repository)";
            this.ConfigText.Text = "—";
            this.LogText.Text = "—";
            return;
        }

        string configPath = Path.Combine(repoRoot, "config.local.json");
        string logPath = Path.Combine(repoRoot, ".local", "daemon.log");

        this.RepoText.Text = repoRoot;
        this.ConfigText.Text = File.Exists(configPath) ? configPath : configPath + "  (missing)";
        this.LogText.Text = File.Exists(logPath) ? logPath : logPath + "  (missing)";
    }

    private static string? ReadGitCommit()
    {
        try
        {
            string? root = TryFindRepoRoot();
            if (root is null)
            {
                return null;
            }

            string headPath = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref:", StringComparison.Ordinal))
            {
                string refPath = Path.Combine(root, ".git", head.Substring(4).Trim());
                if (File.Exists(refPath))
                {
                    return File.ReadAllText(refPath).Trim();
                }
            }
            return head;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadBuildTimestamp(Assembly asm)
    {
        try
        {
            string? path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                DateTime utc = File.GetLastWriteTimeUtc(path);
                return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
        }
        return "unknown";
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static string? TryFindRepoRoot()
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
        return null;
    }
}
