using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Handoff.WinUI.Services;

public static class NotificationService
{
    public static void ShowLatestTeamChange()
    {
        var teamDir = Path.Combine(Directory.GetCurrentDirectory(), "team-members");
        if (!Directory.Exists(teamDir))
        {
            return;
        }

        var latestFile = Directory
            .EnumerateFiles(teamDir, "shared-context.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latestFile is null)
        {
            return;
        }

        using var json = JsonDocument.Parse(File.ReadAllText(latestFile));
        var root = json.RootElement;
        var author = root.GetProperty("author").GetString();
        var branch = root.GetProperty("branch").GetString();
        var message = root.GetProperty("commit_message").GetString();
        var file = root.GetProperty("changed_files")[0].GetString();

        var notification = new AppNotificationBuilder()
            .AddText($"{author} changed {branch}")
            .AddText(message)
            .AddText(file)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
