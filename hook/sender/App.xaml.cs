using Microsoft.UI.Xaml;

namespace Handoff.Sender;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new SenderHostWindow();
        _window.Closed += (_, _) => Environment.Exit(0);
        _window.Activate();
    }
}
