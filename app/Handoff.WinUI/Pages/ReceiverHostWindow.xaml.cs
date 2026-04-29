using System.Globalization;
using Handoff.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Handoff.WinUI.Pages;

public sealed partial class ReceiverHostWindow : Window
{
    private const int WindowWidth = 600;
    private const int WindowHeight = 480;

    public ReceiverHostWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        PopulateContent();
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

    private void PopulateContent()
    {
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

        HeadingText.Text = $"Update from {FormatName(change.Author)}";
        AttributionText.Text = $"on {change.Branch} • {FormatTime(change.Timestamp)}";
        CommitMessageText.Text = change.CommitMessage;
        SummaryText.Text = change.Summary;
        ChangedFilesText.Text = FormatChangedFiles(change.ChangedFiles);
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        EnterStoryboard.Begin();
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        // TODO: record acceptance for the current commit.
        Close();
    }

    private void OnDenyClick(object sender, RoutedEventArgs e)
    {
        // TODO: record denial for the current commit.
        Close();
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
