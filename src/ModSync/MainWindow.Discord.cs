using ModSync.Services;

namespace ModSync;

public partial class MainWindow
{
    private void RefreshDiscordSettings()
    {
        DiscordWebhookStatus.Text = DiscordNotificationService.IsConfigured
            ? "Enabled. Anyone who pushes through this ModSync build sends one grouped announcement to the shared Discord channel. No setup is needed."
            : "Not configured in this build. Ask the pack owner for a build with Discord announcements enabled.";
    }
}
