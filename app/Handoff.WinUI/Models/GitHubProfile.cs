using System.Text.Json.Serialization;

namespace Handoff.WinUI.Models;

/// <summary>
/// Subset of GET https://api.github.com/users/{login}. Email is frequently
/// null — GitHub hides it unless the user has set it public, so callers
/// must treat null as "no email available", not as a network failure.
/// </summary>
public sealed class GitHubProfile
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}
