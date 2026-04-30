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
            HeadingText.Text = "Incoming team context";
            AttributionText.Text = "Pending injection into Claude Code";
            CommitMessageText.Text = string.Empty;
            SummaryText.Text = stdinPreview;
            ChangedFilesText.Text = string.Empty;
            return;
        }

        var change = TeamChange.FindLatest();
        if (change is null)
        {
            HeadingText.Text = "No team updates yet";
            AttributionText.Text = string.Empty;
            CommitMessageText.Text = string.Empty;
            SummaryText.Text = "Once a teammate shares an update, you'll see it here.";
            ChangedFilesText.Text = string.Empty;
            AcceptButton.IsEnabled = false;
            DenyButton.IsEnabled = false;
            return;
        }

        var name = FormatName(change.Author);
        HeadingText.Text = name;
        AttributionText.Text = $"{change.Branch} • {FormatTime(change.Timestamp)}";
        CommitMessageText.Text = change.CommitMessage;
        SummaryText.Text = change.Summary;
        ChangedFilesText.Text = FormatChangedFiles(change.ChangedFiles);
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

    private static string FormatTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
    }

    private static string FormatChangedFiles(IReadOnlyList<string> files)
    {
        return files.Count switch
        {
            0 => "No files listed.",
            1 => $"1 file changed: {files[0]}",
            _ => $"{files.Count} files changed, including {files[0]}",
        };
    }
}
