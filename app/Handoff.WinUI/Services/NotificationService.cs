using System.Globalization;
using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Handoff.WinUI.Services;

public static class NotificationService
{
    public static void ShowLatestTeamChange()
    {
        var teamDir = FindTeamDirectory();
        if (teamDir is null)
        {
            return;
        }

        var latestChange = Directory
            .EnumerateFiles(teamDir, "shared-context.json", SearchOption.AllDirectories)
            .Select(ReadTeamChange)
            .Where(change => change is not null)
            .OrderByDescending(change => change!.CommitTime ?? change.FileLastWriteTime)
            .FirstOrDefault();

        if (latestChange is null)
        {
            return;
        }

        var notification = BuildNotification(latestChange);

        AppNotificationManager.Default.Show(notification);
    }

    private static TeamChange? ReadTeamChange(string filePath)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = json.RootElement;

            return new TeamChange(
                Author: GetString(root, "author", "Someone"),
                Branch: GetString(root, "branch", "unknown branch"),
                CommitSha: GetString(root, "commit_sha", string.Empty),
                CommitMessage: GetString(root, "commit_message", "Updated shared context"),
                Summary: GetString(root, "summary", "No summary provided."),
                ChangedFiles: GetChangedFiles(root),
                CommitTime: GetCommitTime(root),
                FileLastWriteTime: new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindTeamDirectory()
    {
        foreach (var startDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var currentDirectory = new DirectoryInfo(startDirectory);
            while (currentDirectory is not null)
            {
                var candidate = Path.Combine(currentDirectory.FullName, "team-members");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                currentDirectory = currentDirectory.Parent;
            }
        }

        return null;
    }

    private static AppNotification BuildNotification(TeamChange change)
    {
        var displayTime = FormatCommitTime(change.CommitTime ?? change.FileLastWriteTime);
        var notification = new AppNotificationBuilder()
            .SetDuration(AppNotificationDuration.Long)
            .AddText($"{FormatName(change.Author)} changed {change.Branch}")
            .AddText($"Time: {displayTime}{Environment.NewLine}Message: {change.CommitMessage}")
            .AddText($"Summary: {change.Summary}{Environment.NewLine}Files: {FormatChangedFiles(change.ChangedFiles)}")
            .SetAttributionText(FormatAttribution(change))
            .SetTimeStamp(change.CommitTime ?? change.FileLastWriteTime)
            .BuildNotification();

        notification.Group = "team-changes";
        notification.Tag = "latest-team-change";

        return notification;
    }

    private static string GetString(JsonElement root, string propertyName, string fallback)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? fallback;
        }

        return fallback;
    }

    private static string[] GetChangedFiles(JsonElement root)
    {
        if (!root.TryGetProperty("changed_files", out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.String)
            .Select(file => file.GetString())
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file!)
            .ToArray();
    }

    private static DateTimeOffset? GetCommitTime(JsonElement root)
    {
        var timestamp = GetString(root, "timestamp", string.Empty);
        if (DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var commitTime))
        {
            return commitTime;
        }

        return null;
    }

    private static string FormatName(string name)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
    }

    private static string FormatCommitTime(DateTimeOffset commitTime)
    {
        return commitTime.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
    }

    private static string FormatChangedFiles(IReadOnlyList<string> changedFiles)
    {
        return changedFiles.Count switch
        {
            0 => "No files listed",
            1 => changedFiles[0],
            _ => $"{changedFiles.Count} files, including {changedFiles[0]}",
        };
    }

    private static string FormatAttribution(TeamChange change)
    {
        return string.IsNullOrWhiteSpace(change.CommitSha)
            ? "Handoff team update"
            : $"Commit: {change.CommitSha}";
    }

    private sealed record TeamChange(
        string Author,
        string Branch,
        string CommitSha,
        string CommitMessage,
        string Summary,
        IReadOnlyList<string> ChangedFiles,
        DateTimeOffset? CommitTime,
        DateTimeOffset FileLastWriteTime);
}
