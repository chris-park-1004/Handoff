using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handoff.WinUI.Services;

/// <summary>
/// One row in config.local.json's "team-members" array.
/// Mutable so ConfigStore can flip Subscribe in-place during a merge if needed.
/// </summary>
public sealed class TeamMemberEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("subscribe")]
    public bool Subscribe { get; set; }
}

/// <summary>
/// Top-level shape of config.local.json. Self is the user's own normalized
/// member name; TeamMembers is the discovered roster with per-entry subscribe
/// toggles. Default-constructed instance ("", []) is the safe empty state used
/// when the file is missing or unreadable.
/// </summary>
public sealed class HandoffConfig
{
    [JsonPropertyName("self")]
    public string Self { get; set; } = "";

    [JsonPropertyName("team-members")]
    public List<TeamMemberEntry> TeamMembers { get; set; } = new List<TeamMemberEntry>();
}

public sealed class ConfigStore
{
    // Shared options for both Read and Write. We do NOT use a global
    // PropertyNamingPolicy because "team-members" needs the hyphen — relying on
    // explicit JsonPropertyName attributes keeps the JSON shape unambiguous.
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    // Default Subscribe value for newly discovered members. Project agreement:
    // opt-out, not opt-in — the team appears in your context until you toggle off.
    private const bool DefaultSubscribe = true;

    private readonly string _configPath;

    /* ====================================================================
     * ConfigStore (constructor)
     * Description: Binds the store to a single config.local.json path. The
     *              file does not need to exist yet — Read returns a default
     *              HandoffConfig if missing, and Write creates the file.
     * Parameters:
     *   configPath - absolute path to config.local.json
     * Return Values: (constructor)
     *=====================================================================
     */
    public ConfigStore(string configPath)
    {
        this._configPath = configPath;
    }

    /* ======================================================================================
     * Exists
     * Description: Returns whether the config file currently exists on disk. Cheap check
     *              used by SyncService to decide whether to run the first-run bootstrap.
     *              Does not open the file or parse JSON.
     * Parameters: (none)
     * Return Values:
     *   true if the config file exists at the bound path; false otherwise.
     * ======================================================================================
     */
    public bool Exists()
    {
        return File.Exists(this._configPath);
    }

    /* ============================================================================
     * EnsureExists
     * Description: Creates the config file with default values when missing.
     *              No-op when the file is already there, so it is safe to call
     *              on every daemon startup. defaultSelf seeds the user's own
     *              normalized name (e.g., from `git config user.name` after
     *              normalization). When null, Self is created empty and the
     *              hook will not filter out the user's own folder until Self
     *              is later set via the UI or a manual edit.
     * Parameters:
     *   defaultSelf - optional Self value, used ONLY when the file is being
     *                 created. Ignored if the file already exists.
     * Return Values: (none)
     *=============================================================================
     */
    public void EnsureExists(string? defaultSelf = null)
    {
        if (File.Exists(this._configPath))
        {
            return;
        }
        HandoffConfig config = new HandoffConfig
        {
            Self = defaultSelf ?? string.Empty,
        };
        this.Write(config);
        Logger.Log("ConfigStore", "Created config at " + this._configPath
            + " with self=" + (string.IsNullOrEmpty(config.Self) ? "(empty)" : config.Self));
    }

    /* ==========================================================================
     * Read
     * Description: Loads HandoffConfig from disk. Returns a fresh empty config
     *              (Self="", TeamMembers=[]) when the file is missing, malformed,
     *              or otherwise unreadable. Never throws — failures are logged
     *              so the daemon's polling cycle can keep running.
     * Parameters: (none)
     * Return Values:
     *   HandoffConfig — never null; always usable for further merging or display.
     *===========================================================================
     */
    public HandoffConfig Read()
    {
        try
        {
            if (!File.Exists(this._configPath))
            {
                Logger.Log("ConfigStore", "Not found, returning defaults: " + this._configPath);
                return new HandoffConfig();
            }

            string json = File.ReadAllText(this._configPath);
            HandoffConfig? config = JsonSerializer.Deserialize<HandoffConfig>(json, JsonOptions);
            // Deserialize returns null for the literal "null" — fall back to defaults.
            return config ?? new HandoffConfig();
        }
        catch (Exception ex)
        {
            // Malformed JSON, IO error, etc. Logged but never propagated, since the
            // daemon's other steps must continue this tick (and a self-healing read
            // happens on the next cycle).
            Logger.LogError("ConfigStore", "Read " + this._configPath, ex);
            return new HandoffConfig();
        }
    }

    /* =====================================================================
     * Write
     * Description: Serializes the given config and writes it to disk. Never
     *              throws — failures are logged and the next polling cycle
     *              gets another chance.
     * Parameters:
     *   config - the config to persist
     * Return Values: (none)
     *======================================================================
     */
    public void Write(HandoffConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(this._configPath, json);
        }
        catch (Exception ex)
        {
            Logger.LogError("ConfigStore", "Write " + this._configPath, ex);
        }
    }

    /* ==========================================================================
     * MergeDiscoveredMembers
     * Description: Reads the current config, appends any names not yet present
     *              with Subscribe=DefaultSubscribe, and writes back. Existing
     *              entries keep their Subscribe value untouched — the UI is the
     *              source of truth for that flag, the daemon only adds names.
     *              The user's own Self value is filtered out so it never enters
     *              the team-members roster (the user's own folder must not be
     *              re-injected back to themselves).
     * Parameters:
     *   memberNames - normalized member folder names from TeamMemberDiscovery
     * Return Values:
     *   HandoffConfig — the merged config that was just written to disk.
     *===========================================================================
     */
    public HandoffConfig MergeDiscoveredMembers(IEnumerable<string> memberNames)
    {
        HandoffConfig config = this.Read();

        // Case-insensitive index keeps the merge O(N) and survives mismatched casing
        // between disk roster and discovery output (defense in depth — the normalizer
        // already lowercases, but cheap to be safe).
        HashSet<string> existing = new HashSet<string>(
            config.TeamMembers.Select(m => m.Name),
            StringComparer.OrdinalIgnoreCase);

        // Track names that were actually added this call so the log line is informative
        // ("nothing changed" vs "added: alex-kim, bob-lee") instead of just a final count.
        List<string> addedNames = new List<string>();

        foreach (string rawName in memberNames)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                continue;
            }
            // Filter out self — the user's own folder must never be in the roster.
            if (!string.IsNullOrEmpty(config.Self) &&
                string.Equals(rawName, config.Self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (existing.Contains(rawName))
            {
                continue;
            }

            config.TeamMembers.Add(new TeamMemberEntry
            {
                Name = rawName,
                Subscribe = DefaultSubscribe,
            });
            existing.Add(rawName);
            addedNames.Add(rawName);
        }

        this.Write(config);

        if (addedNames.Count == 0)
        {
            Logger.Log("ConfigStore", "Merge: no new members (roster size=" + config.TeamMembers.Count + ")");
        }
        else
        {
            Logger.Log("ConfigStore", "Merge: added " + addedNames.Count + " (" + string.Join(", ", addedNames)
                + "); roster size=" + config.TeamMembers.Count);
        }

        return config;
    }
}
