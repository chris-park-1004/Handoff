using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Handoff.WinUI.Models;

namespace Handoff.WinUI.Services;

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
    private const string TeamMembersTable = "team_members";

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
     *   IReadOnlyList<SharedContext> with zero or more rows.
     * ===========================================================================================
     */
    public async Task<IReadOnlyList<SharedContext>> SelectAllAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured())
        {
            Logger.Log("Supabase", "SelectAll skipped: config not set");
            return Array.Empty<SharedContext>();
        }

        try
        {
            string path = RestRoot + SharedContextsTable + "?select=*&order=updated_at.desc";
            using HttpRequestMessage req = this.BuildRequest(HttpMethod.Get, path);
            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log("Supabase", "SelectAll non-2xx (" + (int)resp.StatusCode + "): " + Truncate(body, 300));
                return Array.Empty<SharedContext>();
            }

            List<SharedContext>? rows = JsonSerializer.Deserialize<List<SharedContext>>(body, JsonOptions);
            return (IReadOnlyList<SharedContext>?)rows ?? Array.Empty<SharedContext>();
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates — caller (SyncService) interprets it as "exit cleanly".
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("Supabase", "SelectAll", ex);
            return Array.Empty<SharedContext>();
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
    public async Task<bool> UpsertAsync(SharedContext row, CancellationToken ct = default)
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
     * SelectTeamMembersAsync
     * Description: Pulls every row from team_members. The roster table is the source of truth
     *              for "who exists on this team" — separate from shared_contexts so a teammate
     *              shows up in the UI even before they have pushed their first context. The
     *              caller (TeamMemberDiscovery) feeds these into ConfigStore.MergeDiscoveredMembers
     *              so subscription state in config.local.json stays in sync.
     * Parameters:
     *   ct - cancellation token; propagates into the HttpClient call
     * Return Values:
     *   IReadOnlyList<TeamMember> with zero or more rows. Empty list on any failure (logged).
     * ===========================================================================================
     */
    public async Task<IReadOnlyList<TeamMember>> SelectTeamMembersAsync(CancellationToken ct = default)
    {
        if (!this.IsConfigured())
        {
            Logger.Log("Supabase", "SelectTeamMembers skipped: config not set");
            return Array.Empty<TeamMember>();
        }

        try
        {
            string path = RestRoot + TeamMembersTable + "?select=*";
            using HttpRequestMessage req = this.BuildRequest(HttpMethod.Get, path);
            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log("Supabase", "SelectTeamMembers non-2xx (" + (int)resp.StatusCode + "): " + Truncate(body, 300));
                return Array.Empty<TeamMember>();
            }

            List<TeamMember>? rows = JsonSerializer.Deserialize<List<TeamMember>>(body, JsonOptions);
            return (IReadOnlyList<TeamMember>?)rows ?? Array.Empty<TeamMember>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("Supabase", "SelectTeamMembers", ex);
            return Array.Empty<TeamMember>();
        }
    }

    /* ===========================================================================================
     * UpsertTeamMemberAsync
     * Description: Inserts the given team_members row, or updates the existing row when the
     *              primary key (name) already exists. Producer side calls this once per
     *              cycle for the user's own identity, ensuring the FK target for any
     *              shared_contexts insert is present. CreatedAt/UpdatedAt are server-managed
     *              and ignored on the wire.
     * Parameters:
     *   member - row data; only Name is strictly required, the rest is optional metadata
     *   ct     - cancellation token; propagates into HttpClient call
     * Return Values:
     *   true on 2xx; false on any failure (logged).
     * ===========================================================================================
     */
    public async Task<bool> UpsertTeamMemberAsync(TeamMember member, CancellationToken ct = default)
    {
        if (!this.IsConfigured())
        {
            Logger.Log("Supabase", "UpsertTeamMember skipped: config not set");
            return false;
        }

        try
        {
            string path = RestRoot + TeamMembersTable;
            string json = JsonSerializer.Serialize(member, JsonOptions);

            using HttpRequestMessage req = this.BuildRequest(HttpMethod.Post, path);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger.Log("Supabase", "UpsertTeamMember non-2xx (" + (int)resp.StatusCode + "): " + Truncate(body, 300));
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
            Logger.LogError("Supabase", "UpsertTeamMember", ex);
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
