using System.Text.Json.Serialization;

namespace Handoff.WinUI.Models;

/// <summary>
/// One row in config.local.json's "team-members" array. Mutable so ConfigStore
/// can flip Subscribe in-place during a merge if needed.
/// </summary>
public sealed class TeamMemberEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("subscribe")]
    public bool Subscribe { get; set; }
}

/// <summary>
/// Supabase connection settings shared by daemon and hook. Both fields are
/// required for any DB call; missing/empty values cause the daemon to skip
/// network steps gracefully (logged but not thrown) so an unconfigured
/// install still boots.
/// </summary>
public sealed class SupabaseConfig
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";
}

/// <summary>
/// Top-level shape of config.local.json. Self is the user's own normalized
/// member name; TeamMembers is the discovered roster with per-entry subscribe
/// toggles. Supabase carries the REST URL + publishable key. Default-
/// constructed instance is the safe empty state used when the file is
/// missing or unreadable.
/// </summary>
public sealed class HandoffConfig
{
    [JsonPropertyName("self")]
    public string Self { get; set; } = "";

    [JsonPropertyName("team-members")]
    public List<TeamMemberEntry> TeamMembers { get; set; } = new List<TeamMemberEntry>();

    [JsonPropertyName("supabase")]
    public SupabaseConfig? Supabase { get; set; }
}
