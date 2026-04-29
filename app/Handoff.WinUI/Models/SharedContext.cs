using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handoff.WinUI.Models;

/// <summary>
/// Mirrors public.shared_contexts: the per-(member, branch) row that the
/// producer-side daemon writes on every commit and the consumer-side hook
/// reads to build the injection preview.
///
/// JSON property names match the column names exactly so System.Text.Json
/// can deserialize the PostgREST payload directly. Nullable fields reflect
/// columns whose data is optional (commit metadata may be absent on the
/// very first push for a branch, for example).
/// </summary>
public sealed class SharedContext
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("member_name")]
    public string MemberName { get; set; } = "";

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "";

    [JsonPropertyName("commit_sha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("commit_message")]
    public string? CommitMessage { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("changed_files")]
    public JsonElement? ChangedFiles { get; set; }

    [JsonPropertyName("tags")]
    public JsonElement? Tags { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
