using System.Globalization;
using System.Text.Json;

namespace Handoff.Receiver;

public sealed record TeamChange(
    string Author,
    string Branch,
    string CommitSha,
    string CommitMessage,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset? CommitTime,
    DateTimeOffset FileLastWriteTime)
{
    public DateTimeOffset Timestamp => CommitTime ?? FileLastWriteTime;

    public static TeamChange? FindLatest()
    {
        var teamDir = FindTeamDirectory();
        if (teamDir is null)
        {
            return null;
        }

        return Directory
            .EnumerateFiles(teamDir, "shared-context.json", SearchOption.AllDirectories)
            .Select(Read)
            .Where(change => change is not null)
            .OrderByDescending(change => change!.Timestamp)
            .FirstOrDefault();
    }

    private static TeamChange? Read(string filePath)
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
}
