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
        _window = new SenderHostWindow();
        _window.Activate();
    }
}
