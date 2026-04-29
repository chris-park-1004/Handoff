using Microsoft.UI.Xaml;

namespace Handoff.Receiver;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new ReceiverHostWindow();
        _window.Closed += (_, _) => Environment.Exit(ReceiverHostWindow.ExitCode);
        _window.Activate();
    }
}
