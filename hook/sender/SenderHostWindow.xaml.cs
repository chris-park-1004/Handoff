using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Graphics;
using WinRT.Interop;

namespace Handoff.Sender;

public sealed partial class SenderHostWindow : Window
{
    private const int WindowWidth = 560;
    private const int WindowHeight = 360;
    private static readonly string SenderDebugLog = Path.Combine(
        AppContext.BaseDirectory,
        "handoff-sender-debug.log");
    private readonly SenderPayload _payload;
    private string _generatedSummary = "";

    /// <summary>
    /// Sender process is launched by the producer hook with the commit blob
    /// piped over stdin. Read it once at construction time; the window keeps
    /// it as immutable state for the rest of its lifecycle.
    /// </summary>
    public SenderHostWindow()
    {
        _payload = SenderPayload.FromJson(ReadStdin());
        InitializeComponent();
        ConfigureWindow();
    }

    /// <summary>
    /// Apply the WinUI window chrome: fixed size, custom title bar, no
    /// minimize/maximize, centered on the active monitor. Called once at
    /// construction; the window isn't designed to be re-positioned later.
    /// </summary>
    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Title = "Handoff";

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragArea);
        }

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
        var work = displayArea.WorkArea;
        var x = work.X + (work.Width - WindowWidth) / 2;
        var y = work.Y + (work.Height - WindowHeight) / 2;
        appWindow.MoveAndResize(new RectInt32(x, y, WindowWidth, WindowHeight));
    }

    /// <summary>
    /// Kick off the entrance fade-in animation once XAML layout is ready.
    /// Wired from the root Grid's Loaded event in XAML.
    /// </summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        EnterStoryboard.Begin();
    }

    /// <summary>
    /// Send button handler: validate, push the row to Supabase, then close on
    /// success. All three buttons are disabled during the round-trip so the
    /// user can't double-submit while the request is in flight.
    /// </summary>
    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        var summary = SummaryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            await ShowErrorAsync("Send failed", "Write a summary before sending it to your team.");
            return;
        }

        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        AutoGenerateButton.IsEnabled = false;

        try
        {
            var result = await InsertSharedContextAsync(summary);
            if (result.Success)
            {
                Close();
                return;
            }

            await ShowErrorAsync("Send failed", result.ErrorMessage);
        }
        finally
        {
            SendButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            AutoGenerateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Cancel button handler: close without sending. Closing the window from
    /// the title bar X has the same effect.
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Auto-generate button handler. First click invokes the CLI to produce a
    /// summary; subsequent clicks just re-paste the cached result so the user
    /// doesn't accidentally burn another CLI call after editing the textbox.
    /// </summary>
    private async void OnAutoGenerateClick(object sender, RoutedEventArgs e)
    {
        // Cache hit — restore the last generated summary instead of
        // regenerating. The user may have edited the textbox manually since.
        if (!string.IsNullOrWhiteSpace(_generatedSummary))
        {
            SummaryTextBox.Text = _generatedSummary;
            return;
        }

        AutoGenerateButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        var previousText = SummaryTextBox.Text;
        SummaryTextBox.IsReadOnly = true;
        SummaryTextBox.Text = "Generating summary...";
        try
        {
            var result = await GenerateSummaryAsync();
            if (!result.Success)
            {
                SummaryTextBox.Text = previousText;
                await ShowErrorAsync("Auto-generate failed", result.ErrorMessage);
                return;
            }

            _generatedSummary = result.Summary;
            SummaryTextBox.Text = result.Summary;
        }
        finally
        {
            SummaryTextBox.IsReadOnly = false;
            AutoGenerateButton.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Dispatches summary generation to whichever CLI invoked the hook that
    /// opened this sender. Only the invoking CLI is reliably on PATH for the
    /// spawned sender process — calling `codex` from a Claude session (or vice
    /// versa) fails with "file not found" because the other CLI isn't there.
    /// </summary>
    private Task<SummaryResult> GenerateSummaryAsync()
    {
        if (string.IsNullOrWhiteSpace(_payload.RepoRoot))
        {
            return Task.FromResult(SummaryResult.Fail("Missing repo_root in the sender payload."));
        }

        string cli = string.IsNullOrWhiteSpace(_payload.Cli) ? "codex" : _payload.Cli.Trim().ToLowerInvariant();
        if (cli == "claude")
        {
            return GenerateSummaryWithClaudeAsync();
        }
        return GenerateSummaryWithCodexAsync();
    }

    /// <summary>
    /// Runs `codex exec --ephemeral` in read-only sandbox. -c codex_hooks=false
    /// silences Codex's hook chain for this child invocation, and the
    /// HANDOFF_SUMMARY_GENERATION env var is the JS-side guard the team-context
    /// hooks check so they exit early — second line of defense if the toml flag
    /// is ever ignored.
    /// </summary>
    private async Task<SummaryResult> GenerateSummaryWithCodexAsync()
    {
        string localDir = Path.Combine(_payload.RepoRoot, ".local");
        Directory.CreateDirectory(localDir);
        // PID-suffixed so two senders running concurrently don't trample each
        // other's response file. Codex writes its final answer here via -o.
        string outputPath = Path.Combine(localDir, "summary-response-" + Environment.ProcessId + ".txt");

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _payload.RepoRoot,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(_payload.RepoRoot);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("codex_hooks=false");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("-");
        startInfo.Environment["HANDOFF_SUMMARY_GENERATION"] = "1";

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return SummaryResult.Fail("Could not start the Codex CLI process.");
            }

            await process.StandardInput.WriteAsync(BuildSummaryPrompt());
            process.StandardInput.Close();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();
            Task exited = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMinutes(3)));
            if (exited != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogSender("codex summary generation timed out");
                return SummaryResult.Fail("Codex summary generation timed out after 3 minutes.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                LogSender("codex exec failed: exit=" + process.ExitCode + ", stderr=" + stderr);
                return SummaryResult.Fail("Codex exited with code " + process.ExitCode + ": " + Truncate(stderr, 700));
            }

            string summary = File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath)
                : stdout;

            string normalized = NormalizeSummary(summary);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                LogSender("codex returned empty summary. stdout=" + stdout + ", stderr=" + stderr);
                return SummaryResult.Fail("Codex completed but returned an empty summary.");
            }

            return SummaryResult.Ok(normalized);
        }
        catch (Exception ex)
        {
            LogSender("codex summary generation exception: " + ex);
            return SummaryResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

    /// <summary>
    /// Runs `claude -p` (Claude Code print mode). Plan permission mode forbids
    /// tool/file mutation, Haiku keeps latency down for this short prompt, and
    /// HANDOFF_SUMMARY_GENERATION makes the JS hooks fire-and-exit so we don't
    /// recurse into another sender popup. Output is on stdout — no temp file.
    /// </summary>
    private async Task<SummaryResult> GenerateSummaryWithClaudeAsync()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _payload.RepoRoot,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("--permission-mode");
        startInfo.ArgumentList.Add("plan");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("claude-haiku-4-5-20251001");
        startInfo.Environment["HANDOFF_SUMMARY_GENERATION"] = "1";

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return SummaryResult.Fail("Could not start the Claude CLI process.");
            }

            await process.StandardInput.WriteAsync(BuildSummaryPrompt());
            process.StandardInput.Close();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();
            Task exited = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMinutes(3)));
            if (exited != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogSender("claude summary generation timed out");
                return SummaryResult.Fail("Claude summary generation timed out after 3 minutes.");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                LogSender("claude -p failed: exit=" + process.ExitCode + ", stderr=" + stderr);
                return SummaryResult.Fail("Claude exited with code " + process.ExitCode + ": " + Truncate(stderr, 700));
            }

            string normalized = NormalizeSummary(stdout);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                LogSender("claude returned empty summary. stdout=" + stdout + ", stderr=" + stderr);
                return SummaryResult.Fail("Claude completed but returned an empty summary.");
            }

            return SummaryResult.Ok(normalized);
        }
        catch (Exception ex)
        {
            LogSender("claude summary generation exception: " + ex);
            return SummaryResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Append a line to the sender's local debug log. Best-effort; a failure
    /// here must not surface to the user since the sender UI has no console.
    /// </summary>
    private static void LogSender(string message)
    {
        try
        {
            File.AppendAllText(SenderDebugLog, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Build the prompt fed into the chosen CLI. The shape is intentionally
    /// minimal — we want a 1-2 sentence teammate-facing handoff, not a
    /// breakdown — so the prompt explicitly forbids markdown and bullets.
    /// </summary>
    private string BuildSummaryPrompt()
    {
        var files = _payload.ChangedFiles is { ValueKind: JsonValueKind.Array } changedFiles
            ? string.Join(", ", changedFiles.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : "";

        return string.Join('\n', new[]
        {
            "Create a concise teammate-facing handoff summary for this local commit.",
            "Return only the summary text. Use 1-2 sentences. No markdown headings, no bullets, no hype.",
            "",
            "Author: " + _payload.Author,
            "Branch: " + _payload.Branch,
            "Commit SHA: " + _payload.CommitSha,
            "Commit message: " + _payload.CommitMessage,
            "Commit timestamp: " + _payload.Timestamp,
            "Changed files: " + files,
            "",
            "This request is coming from the Handoff sender window after the user clicked Auto-generate.",
            "Do not edit files or run commands.",
        });
    }

    /// <summary>
    /// Flatten a multi-line CLI response into a single trimmed line.
    /// Both Codex and Claude sometimes wrap their output with leading/trailing
    /// blank lines or split a single sentence across hard line breaks; this
    /// keeps the textbox clean regardless.
    /// </summary>
    private static string NormalizeSummary(string summary)
    {
        return string.Join(
            " ",
            (summary ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0))
            .Trim();
    }

    /// <summary>
    /// POST the summary row to the Supabase shared_contexts table via REST.
    /// Returns Ok on 2xx; Fail carries the HTTP status + body so the user
    /// gets actionable feedback instead of a generic "send failed".
    /// </summary>
    private async Task<SendResult> InsertSharedContextAsync(string summary)
    {
        if (!_payload.HasSupabaseConfig || string.IsNullOrWhiteSpace(_payload.Author) || string.IsNullOrWhiteSpace(_payload.Branch))
        {
            return SendResult.Fail("Missing Supabase config, author, or branch in the sender payload.");
        }

        var row = new SharedContextPayload
        {
            MemberName = _payload.Author,
            Branch = _payload.Branch,
            CommitSha = _payload.CommitSha,
            CommitMessage = _payload.CommitMessage,
            Summary = summary,
            ChangedFiles = _payload.ChangedFiles,
        };

        var url = _payload.Supabase!.Url.TrimEnd('/') + "/rest/v1/shared_contexts";
        var json = JsonSerializer.Serialize(row, JsonOptions);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("apikey", _payload.Supabase.Key);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _payload.Supabase.Key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                return SendResult.Ok();
            }

            var body = await resp.Content.ReadAsStringAsync();
            var detail = string.IsNullOrWhiteSpace(body)
                ? resp.ReasonPhrase ?? "No response body."
                : body;
            return SendResult.Fail("Supabase returned " + (int)resp.StatusCode + ": " + Truncate(detail, 700));
        }
        catch (Exception ex)
        {
            return SendResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Cap a string at `max` characters with an ellipsis, used for error
    /// messages bubbled into ContentDialog so a multi-KB stderr or response
    /// body doesn't push the dialog off screen.
    /// </summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, max) + "...";
    }

    /// <summary>
    /// Show a modal error dialog over the sender window. Caller awaits the
    /// dismissal so the UI doesn't re-enable buttons before the user sees it.
    /// </summary>
    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Read the full stdin stream as a string, or empty if stdin isn't piped
    /// (e.g. when the .exe is launched directly for debugging without the
    /// producer hook). Failures are swallowed — the sender then runs with an
    /// empty payload and surfaces a "missing repo_root" error on Auto-generate.
    /// </summary>
    private static string ReadStdin()
    {
        try
        {
            if (Console.IsInputRedirected)
            {
                return Console.In.ReadToEnd();
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    /// <summary>
    /// Strongly-typed view over the JSON blob the producer hook pipes in.
    /// All getters are safe defaults so a missing field becomes an empty
    /// string instead of a NullReferenceException downstream.
    /// </summary>
    private sealed class SenderPayload
    {
        public string Author { get; private init; } = "";
        public string Branch { get; private init; } = "";
        public string CommitSha { get; private init; } = "";
        public string CommitMessage { get; private init; } = "";
        public string Timestamp { get; private init; } = "";
        public string RepoRoot { get; private init; } = "";
        public string Cli { get; private init; } = "";
        public JsonElement? ChangedFiles { get; private init; }
        public SupabaseConfig? Supabase { get; private init; }
        public bool HasSupabaseConfig =>
            Supabase is not null &&
            !string.IsNullOrWhiteSpace(Supabase.Url) &&
            !string.IsNullOrWhiteSpace(Supabase.Key);

        /// <summary>
        /// Parse the producer-hook payload. Returns an empty payload on any
        /// JSON error so the sender window still opens — the user can then
        /// type a manual summary even if Auto-generate is broken.
        /// </summary>
        public static SenderPayload FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new SenderPayload();
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new SenderPayload
                {
                    Author = GetString(root, "author"),
                    Branch = GetString(root, "branch"),
                    CommitSha = GetString(root, "commit_sha"),
                    CommitMessage = GetString(root, "commit_message"),
                    Timestamp = GetString(root, "timestamp"),
                    RepoRoot = GetString(root, "repo_root"),
                    Cli = GetString(root, "cli"),
                    ChangedFiles = CloneElement(root, "changed_files"),
                    Supabase = ReadSupabase(root),
                };
            }
            catch
            {
                return new SenderPayload();
            }
        }

        /// <summary>
        /// Read a string property from the payload, returning empty if the
        /// field is missing or a non-string (e.g. null or number).
        /// </summary>
        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// Clone a JSON element off the parent document so it stays valid
        /// after the source JsonDocument is disposed at the end of FromJson.
        /// </summary>
        private static JsonElement? CloneElement(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value)
                ? value.Clone()
                : null;
        }

        /// <summary>
        /// Pull the nested supabase config out of the payload. Null when the
        /// producer didn't include it (e.g. Supabase not yet configured).
        /// </summary>
        private static SupabaseConfig? ReadSupabase(JsonElement root)
        {
            if (!root.TryGetProperty("supabase", out var supabase) || supabase.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new SupabaseConfig
            {
                Url = GetString(supabase, "url"),
                Key = GetString(supabase, "key"),
            };
        }
    }

    /// <summary>
    /// Supabase REST endpoint + anon/service key copied out of the producer
    /// hook payload. Both fields must be non-empty for sends to work.
    /// </summary>
    private sealed class SupabaseConfig
    {
        public string Url { get; init; } = "";
        public string Key { get; init; } = "";
    }

    /// <summary>
    /// Result type for InsertSharedContextAsync. Carries an error message on
    /// failure so the dialog can show why the send failed instead of a
    /// generic message.
    /// </summary>
    private sealed class SendResult
    {
        private SendResult(bool success, string errorMessage)
        {
            Success = success;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; }

        public string ErrorMessage { get; }

        public static SendResult Ok()
        {
            return new SendResult(true, "");
        }

        public static SendResult Fail(string message)
        {
            return new SendResult(false, string.IsNullOrWhiteSpace(message)
                ? "The shared context could not be saved to Supabase."
                : message);
        }
    }

    /// <summary>
    /// Result type for the CLI summary generators. Carries either the cleaned
    /// summary text (Ok) or an error message (Fail). Generators never throw
    /// upstream — exceptions are caught and converted to Fail.
    /// </summary>
    private sealed class SummaryResult
    {
        private SummaryResult(bool success, string summary, string errorMessage)
        {
            Success = success;
            Summary = summary;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; }

        public string Summary { get; }

        public string ErrorMessage { get; }

        public static SummaryResult Ok(string summary)
        {
            return new SummaryResult(true, summary, "");
        }

        public static SummaryResult Fail(string message)
        {
            return new SummaryResult(false, "", string.IsNullOrWhiteSpace(message)
                ? "Codex did not return a summary for this commit."
                : message);
        }
    }

    /// <summary>
    /// Wire shape for a Supabase shared_contexts row. Property names match
    /// the table columns via JsonPropertyName so System.Text.Json serializes
    /// to the exact keys PostgREST expects.
    /// </summary>
    private sealed class SharedContextPayload
    {
        [JsonPropertyName("member_name")]
        public string MemberName { get; init; } = "";

        [JsonPropertyName("branch")]
        public string Branch { get; init; } = "";

        [JsonPropertyName("commit_sha")]
        public string CommitSha { get; init; } = "";

        [JsonPropertyName("commit_message")]
        public string CommitMessage { get; init; } = "";

        [JsonPropertyName("summary")]
        public string Summary { get; init; } = "";

        [JsonPropertyName("changed_files")]
        public JsonElement? ChangedFiles { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
