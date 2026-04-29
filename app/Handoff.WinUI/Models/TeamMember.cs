using System.Text.Json.Serialization;

namespace Handoff.WinUI.Models;

/// <summary>
/// Mirrors public.team_members: the canonical roster of teammates. The Name
/// field is both the primary key and the foreign-key target for
/// shared_contexts.member, so it must always carry the normalized folder-
/// style identifier (e.g. "chris-park-1004").
/// </summary>
public sealed class TeamMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
