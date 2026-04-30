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
    private readonly SenderPayload _payload;
    private string _generatedSummary = "";

    public SenderHostWindow()
    {
        _payload = SenderPayload.FromJson(ReadStdin());
        InitializeComponent();
        ConfigureWindow();
    }

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

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        EnterStoryboard.Begin();
    }

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

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnAutoGenerateClick(object sender, RoutedEventArgs e)
    {
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
            var summary = await GenerateSummaryAsync();
            if (string.IsNullOrWhiteSpace(summary))
            {
                SummaryTextBox.Text = previousText;
                await ShowErrorAsync("Auto-generate failed", "Codex did not return a summary for this commit.");
                return;
            }

            _generatedSummary = summary;
            SummaryTextBox.Text = summary;
        }
        finally
        {
            SummaryTextBox.IsReadOnly = false;
            AutoGenerateButton.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    private async Task<string> GenerateSummaryAsync()
    {
        if (string.IsNullOrWhiteSpace(_payload.RepoRoot))
        {
            return string.Empty;
        }

        var localDir = Path.Combine(_payload.RepoRoot, ".local");
        Directory.CreateDirectory(localDir);
        var outputPath = Path.Combine(localDir, "summary-response-" + Environment.ProcessId + ".txt");

        var startInfo = new ProcessStartInfo
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
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            await process.StandardInput.WriteAsync(BuildSummaryPrompt());
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var exited = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMinutes(3)));
            if (exited != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }

            var stdout = await stdoutTask;
            _ = await stderrTask;

            var summary = File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath)
                : stdout;

            return NormalizeSummary(summary);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

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

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, max) + "...";
    }

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

    private sealed class SenderPayload
    {
        public string Author { get; private init; } = "";
        public string Branch { get; private init; } = "";
        public string CommitSha { get; private init; } = "";
        public string CommitMessage { get; private init; } = "";
        public string Timestamp { get; private init; } = "";
        public string RepoRoot { get; private init; } = "";
        public JsonElement? ChangedFiles { get; private init; }
        public SupabaseConfig? Supabase { get; private init; }
        public bool HasSupabaseConfig =>
            Supabase is not null &&
            !string.IsNullOrWhiteSpace(Supabase.Url) &&
            !string.IsNullOrWhiteSpace(Supabase.Key);

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
                    ChangedFiles = CloneElement(root, "changed_files"),
                    Supabase = ReadSupabase(root),
                };
            }
            catch
            {
                return new SenderPayload();
            }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static JsonElement? CloneElement(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value)
                ? value.Clone()
                : null;
        }

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

    private sealed class SupabaseConfig
    {
        public string Url { get; init; } = "";
        public string Key { get; init; } = "";
    }

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
