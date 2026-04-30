using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Handoff.Sender;

public sealed partial class SenderHostWindow : Window
{
    private const int WindowWidth = 560;
    private const int WindowHeight = 360;

    public SenderHostWindow()
    {
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

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        var summary = SummaryTextBox.Text.Trim();
        // TODO: persist or send `summary` to the team channel.
        _ = summary;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnAutoGenerateClick(object sender, RoutedEventArgs e)
    {
        // TODO: replace with real summary generation (git diff / LLM / etc.).
        SummaryTextBox.Text = "Auto-generated summary placeholder.";
    }
}
