using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handoff.WinUI.Services;

/// <summary>
/// One row from public.shared_contexts. Mirrors the schema agreed in the
/// hackathon: identity (member, branch), latest commit fields for preview,
/// content_hash for client-side watermarking, updated_at for freshness.
/// JSON property names match the column names exactly so System.Text.Json
/// can deserialize the REST payload directly.
/// </summary>
public sealed class SharedContextRow
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("member")]
    public string Member { get; set; } = "";

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

public sealed class SupabaseClient : IDisposable
{
    // PostgREST conventions:
    //   - All paths under /rest/v1/
    //   - apikey + Authorization Bearer headers required (using same publishable key)
    //   - Prefer: return=representation echoes the inserted row(s) back, useful for diagnostics
    //   - Prefer: resolution=merge-duplicates makes the upsert use ON CONFLICT DO UPDATE
    //   - Content-Profile: public is implicit; left as default
    private const string RestRoot = "/rest/v1/";
    private const string SharedContextsTable = "shared_contexts";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private bool _disposed;

    /* ===========================================================================================
     * SupabaseClient (constructor)
     * Description: Wires up an HttpClient against the Supabase REST endpoint. The constructor
     *              does NOT validate the URL or key — by design, every call site already has
     *              to handle network failure, and a hard precheck would block the daemon from
     *              starting on a partially configured machine. Empty url/key just means every
     *              method returns "no data" results.
     * Parameters:
     *   url - Supabase project URL (e.g. https://abc.supabase.co); trailing slash optional
     *   key - publishable/anon API key used for both apikey and Authorization Bearer headers
     * Return Values: (constructor)
     * ===========================================================================================
     */
    public SupabaseClient(string url, string key)
    {
        this._baseUrl = (url ?? string.Empty).TrimEnd('/');
        this._apiKey = key ?? string.Empty;
        this._http = new HttpClient
        {
            // Hackathon-friendly default. Daemon polls slowly, hook is timeboxed
            // by Claude Code's per-hook limit, so 15s leaves room for a transient
            // network blip without ever hitting the harness ceiling.
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /* ===========================================================================================
     * IsConfigured
     * Description: Cheap precondition check. Callers can short-circuit network calls when the
     *              config is empty, avoiding a guaranteed-failing HTTP attempt and the noisy
     *              error log that comes with it.
     * Parameters: (none)
     * Return Values:
     *   true when both url and key are non-empty; false otherwise.
     * ===========================================================================================
     */
    public bool IsConfigured()
    {
        return !string.IsNullOrEmpty(this._baseUrl) && !string.IsNullOrEmpty(this._apiKey);
    }

    /* ===========================================================================================
     * SelectAllAsync
     * Description: Fetches every row from shared_contexts. Intended for the daemon's roster
     *              discovery (distinct member names) and the hook's filter logic. Row count is
     *              tiny (one entry per teammate-branch), so paging is unnecessary.
     *              Network or parse failures yield an empty list — the caller must treat
     *              "no data" as a non-error so the daemon can keep polling.
     * Parameters:
     *   ct - cancellation token; propagates into the underlying HttpClient call
     * Return Values:
     *   IReadOnlyList<SharedContextRow> with zero or more rows.
     * ===========================================================================================
     */
    public async Task<IReadOnlyList<SharedContextRow>> SelectAllAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured())
        {
            Logger.Log("Supabase", "SelectAll skipped: config not set");
            return Array.Empty<SharedContextRow>();
        }

        try
        {
            string path = RestRoot + SharedContextsTable + "?select=*";
            using HttpRequestMessage req = this.BuildRequest(HttpMethod.Get, path);
            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log("Supabase", "SelectAll non-2xx (" + (int)resp.StatusCode + "): " + Truncate(body, 300));
                return Array.Empty<SharedContextRow>();
            }

            List<SharedContextRow>? rows = JsonSerializer.Deserialize<List<SharedContextRow>>(body, JsonOptions);
            return (IReadOnlyList<SharedContextRow>?)rows ?? Array.Empty<SharedContextRow>();
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates — caller (SyncService) interprets it as "exit cleanly".
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("Supabase", "SelectAll", ex);
            return Array.Empty<SharedContextRow>();
        }
    }

    /* ===========================================================================================
     * UpsertAsync
     * Description: Inserts the given row, or updates the existing row when (member, branch)
     *              already exists (UNIQUE constraint in the schema). Uses PostgREST's
     *              "Prefer: resolution=merge-duplicates" header which compiles down to
     *              ON CONFLICT DO UPDATE on the database. This is what the producer-side
     *              commit watcher will call once it detects a new commit.
     * Parameters:
     *   row - row data; Id is ignored on insert and recomputed by the DB
     *   ct  - cancellation token; propagates into the HttpClient call
     * Return Values:
     *   true on 2xx response; false on any failure (logged, not thrown — the polling loop
     *   recovers on the next tick).
     * ===========================================================================================
     */
    public async Task<bool> UpsertAsync(SharedContextRow row, CancellationToken ct = default)
    {
        if (!this.IsConfigured())
        {
            Logger.Log("Supabase", "Upsert skipped: config not set");
            return false;
        }

        try
        {
            string path = RestRoot + SharedContextsTable;
            string json = JsonSerializer.Serialize(row, JsonOptions);

            using HttpRequestMessage req = this.BuildRequest(HttpMethod.Post, path);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            // merge-duplicates triggers ON CONFLICT DO UPDATE on the DB side; we need this
            // because the commit watcher will re-emit rows for the same (member, branch)
            // every time a new commit lands on that branch.
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger.Log("Supabase", "Upsert non-2xx (" + (int)resp.StatusCode + "): " + Truncate(body, 300));
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("Supabase", "Upsert", ex);
            return false;
        }
    }

    /* ===========================================================================================
     * BuildRequest
     * Description: Centralizes the apikey + Authorization header pair so every method does not
     *              re-set them. Keeps the absolute URL construction in one place too — useful
     *              when the project URL has or omits a trailing slash.
     * Parameters:
     *   method      - HTTP method
     *   relativePath - path under the base URL (e.g. "/rest/v1/shared_contexts?select=*")
     * Return Values:
     *   HttpRequestMessage with required Supabase headers attached.
     * ===========================================================================================
     */
    private HttpRequestMessage BuildRequest(HttpMethod method, string relativePath)
    {
        HttpRequestMessage req = new HttpRequestMessage(method, this._baseUrl + relativePath);
        req.Headers.TryAddWithoutValidation("apikey", this._apiKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this._apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /* ===========================================================================================
     * Truncate
     * Description: Defensive log helper — prevents a multi-kilobyte error body from being
     *              splatted into the daemon log when the server returns HTML or a stack trace
     *              instead of JSON. Caller is responsible for choosing a sensible cap.
     * Parameters:
     *   s   - string to truncate (may be null)
     *   max - maximum length to keep
     * Return Values:
     *   Original string if short enough, or first `max` chars suffixed with "...".
     * ===========================================================================================
     */
    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }
        if (s.Length <= max)
        {
            return s;
        }
        return s.Substring(0, max) + "...";
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }
        this._disposed = true;
        try
        {
            this._http.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError("Supabase", "Dispose", ex);
        }
    }
}
