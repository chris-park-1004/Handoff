using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace Handoff.WinUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppNotificationManager.Default.Register();
        NotificationService.ShowTeamChangeTest();
        Exit();
    }
}
