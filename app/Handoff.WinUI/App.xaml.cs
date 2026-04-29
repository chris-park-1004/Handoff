using Handoff.WinUI.Pages;
using Microsoft.UI.Xaml;

namespace Handoff.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var isReceiver = commandLine.Any(arg =>
            string.Equals(arg, "--receive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--receiver", StringComparison.OrdinalIgnoreCase));

        _window = isReceiver
            ? new ReceiverHostWindow()
            : new SenderHostWindow();

        _window.Activate();
    }
}
