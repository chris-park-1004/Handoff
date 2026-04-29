using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Handoff.WinUI.Services;

public static class NotificationService
{
    public static void ShowTeamChangeTest()
    {
        var notification = new AppNotificationBuilder()
            .AddText("Chris changed something")
            .AddText("JWT middleware was updated.")
            .AddText("feature/auth - src/auth/middleware.ts")
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
