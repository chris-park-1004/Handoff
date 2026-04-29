using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Handoff.WinUI.Models;

namespace Handoff.WinUI.Services;

public sealed class GitHubClient : IDisposable
{
    private const string UserAgent = "Handoff-Daemon";
    private const string ApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = null,
    };

    private readonly HttpClient _http;
    private bool _disposed;

    /* ===========================================================================================
     * GitHubClient (constructor)
     * Description: Initializes an HttpClient configured for the GitHub REST API. We do NOT
     *              authenticate, accepting the 60 req/hr/IP rate limit — keeping zero-credential
     *              is worth the tighter limit for the hackathon. If we exceed the limit the
     *              fetch returns null and the daemon retries on the next cycle.
     * Parameters: (none)
     * Return Values: (constructor)
     * ===========================================================================================
     */
    public GitHubClient()
    {
        this._http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // GitHub rejects requests without a User-Agent header — must be set up front.
        this._http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        this._http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /* ===========================================================================================
     * FetchProfileAsync
     * Description: Looks up a public GitHub user profile by login. Returns null on any of:
     *                - empty login
     *                - 404 (user does not exist)
     *                - 403 (rate-limited)
     *                - network/parse errors
     *              All failures are logged but never thrown so a single bad lookup does not
     *              kill the daemon's cycle.
     * Parameters:
     *   login - the GitHub login to look up
     *   ct    - cancellation token; propagates into HttpClient call
     * Return Values:
     *   GitHubProfile when fetched successfully; null otherwise.
     * ===========================================================================================
     */
    public async Task<GitHubProfile?> FetchProfileAsync(string login, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        try
        {
            string url = ApiBase + "/users/" + Uri.EscapeDataString(login);
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage resp = await this._http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.Log("GitHub", "Profile not found: " + login);
                return null;
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                // 403 from api.github.com is almost always rate-limit (X-RateLimit-Remaining: 0).
                Logger.Log("GitHub", "403 (likely rate-limited) for login=" + login);
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log("GitHub", "Non-2xx (" + (int)resp.StatusCode + ") for login=" + login);
                return null;
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GitHubProfile>(body, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError("GitHub", "FetchProfile " + login, ex);
            return null;
        }
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
            Logger.LogError("GitHub", "Dispose", ex);
        }
    }
}
