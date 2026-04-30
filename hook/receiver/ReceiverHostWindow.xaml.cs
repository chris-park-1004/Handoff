using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Handoff.Receiver;

public sealed partial class ReceiverHostWindow : Window
{
    private const int WindowWidth = 600;
    private const int WindowHeight = 480;
    private const int EdgeMargin = 16;
    private const int SlideDurationMs = 650;
    private const int FrameIntervalMs = 8;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private nint _hwnd;
    private int _restingX;
    private int _restingY;
    private int _startX;
    private DispatcherQueueTimer? _slideTimer;
    private DateTime _slideStart;

    public static int ExitCode { get; private set; } = 1;

    public ReceiverHostWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        PopulateContent();
    }

    private void ConfigureWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Title = "Handoff";
        appWindow.SetIcon("Assets/Logo.ico");

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
        _restingX = work.X + work.Width - WindowWidth - EdgeMargin;
        _restingY = work.Y + work.Height - WindowHeight - EdgeMargin;
        _startX = work.X + work.Width;

        appWindow.MoveAndResize(new RectInt32(_startX, _restingY, WindowWidth, WindowHeight));
    }

    private void PopulateContent()
    {
        var stdinPreview = ReadStdinPreview();
        if (!string.IsNullOrWhiteSpace(stdinPreview))
        {
            var parsed = ParseStdinPreview(stdinPreview);
            ApplyContent(parsed.Author, parsed.Branch, parsed.Summary, parsed.CommitMessage, parsed.CommitSha);
            return;
        }

        var change = TeamChange.FindLatest();
        if (change is null)
        {
            HeadingText.Text = "No team updates yet";
            AttributionText.Text = string.Empty;
            SummaryText.Text = "Once a teammate shares an update, you'll see it here.";
            CommitDetailText.Text = string.Empty;
            AcceptButton.IsEnabled = false;
            DenyButton.IsEnabled = false;
            return;
        }

        ApplyContent(change.Author, change.Branch, change.Summary, change.CommitMessage, change.CommitSha);
    }

    private void ApplyContent(string author, string branch, string summary, string commitMessage, string commitSha)
    {
        var name = string.IsNullOrWhiteSpace(author) ? string.Empty : FormatName(author);
        HeadingText.Text = string.IsNullOrEmpty(name)
            ? "Incoming team context"
            : "Incoming team context · " + name;
        AttributionText.Text = branch ?? string.Empty;
        SummaryText.Text = string.IsNullOrWhiteSpace(summary) ? "(no summary)" : summary;
        CommitDetailText.Text = FormatCommit(commitMessage, commitSha);
    }

    private static string FormatCommit(string message, string sha)
    {
        var shortSha = string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length > 7 ? sha[..7] : sha);
        if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(shortSha))
        {
            return string.Empty;
        }
        if (string.IsNullOrEmpty(shortSha)) return message;
        if (string.IsNullOrEmpty(message)) return shortSha;
        return message + " (" + shortSha + ")";
    }

    private record ParsedPreview(string Author, string Branch, string Summary, string CommitMessage, string CommitSha);

    private static ParsedPreview ParseStdinPreview(string text)
    {
        string author = "", branch = "", summary = "", commitMessage = "", commitSha = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## From ", StringComparison.Ordinal))
            {
                var rest = line["## From ".Length..].Trim();
                var slash = rest.IndexOf('/');
                if (slash > 0)
                {
                    author = rest[..slash];
                    branch = rest[(slash + 1)..];
                }
                else
                {
                    author = rest;
                }
            }
            else if (line.StartsWith("**Summary**:", StringComparison.Ordinal))
            {
                summary = line["**Summary**:".Length..].Trim();
            }
            else if (line.StartsWith("**Commit**:", StringComparison.Ordinal))
            {
                var rest = line["**Commit**:".Length..].Trim();
                var openParen = rest.LastIndexOf('(');
                var closeParen = rest.LastIndexOf(')');
                if (openParen > 0 && closeParen > openParen)
                {
                    commitMessage = rest[..openParen].Trim();
                    commitSha = rest.Substring(openParen + 1, closeParen - openParen - 1).Trim();
                }
                else
                {
                    commitMessage = rest;
                }
            }
        }

        return new ParsedPreview(author, branch, summary, commitMessage, commitSha);
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _slideStart = DateTime.UtcNow;
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.Interval = TimeSpan.FromMilliseconds(FrameIntervalMs);
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(DispatcherQueueTimer timer, object args)
    {
        var elapsed = (DateTime.UtcNow - _slideStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / SlideDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);

        var x = (int)(_startX + (_restingX - _startX) * eased);
        SetWindowPos(_hwnd, 0, x, _restingY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        if (t >= 1)
        {
            timer.Stop();
        }
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        ExitCode = 0;
        Close();
    }

    private void OnDenyClick(object sender, RoutedEventArgs e)
    {
        ExitCode = 1;
        Close();
    }

    private static string? ReadStdinPreview()
    {
        try
        {
            if (Console.IsInputRedirected)
            {
                var text = Console.In.ReadToEnd();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch
        {
        }
        return null;
    }

    private static string FormatName(string name)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
    }
}
