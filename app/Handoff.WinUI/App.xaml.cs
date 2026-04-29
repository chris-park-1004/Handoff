using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;

namespace Handoff.WinUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        NotificationService.ShowLatestTeamChange();
        Exit();
    }
}
