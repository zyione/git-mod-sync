using System.Windows;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

public partial class MainWindow
{
    private LaunchSetupService LaunchSetup => new(PathUtils.GetAppDirectory());
    private void RefreshLaunchSetup()
    {
        try
        {
            var state = LaunchSetup.Inspect(_configService.MinecraftFolder);
            LaunchSetupHeading.Text = "Launch check · " + (state.Healthy ? "Enabled" : state.Installed ? "Needs repair" : "Off");
            LaunchSetupDescription.Text = state.Message;
            LaunchSetupButton.Content = state.Healthy ? "Manage" : state.Installed ? "Repair" : "Enable";
            LaunchSetupButton.ToolTip = state.Message;
        }
        catch (Exception ex) { LaunchSetupHeading.Text = "Launch check · Unavailable"; LaunchSetupDescription.Text = ex.Message; }
    }

    private async void LaunchSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_launchDecision != null) { ShowFeedback("Cancel this launch and close UltimMC before changing launch setup.", false); return; }
        if (!EnsureInstanceSelected()) return;
        try
        {
            string game = _configService.MinecraftFolder;
            var state = LaunchSetup.Inspect(game);
            if (!state.Supported) { ShowFeedback(state.Message, false); return; }
            int choice = await ChooseAsync(state.Healthy ? "Manage launch check" : "Check before launching Minecraft?",
                "Close UltimMC first. ModSync will update this instance’s launch settings and keep a backup in UltimMC\\ModSync. Existing launch commands are preserved; inherited commands are copied into this instance. Updates still need your approval.",
                state.Healthy ? "Disable launch check" : state.Installed ? "Repair launch check" : "Enable launch check", "Cancel");
            if (choice != 0) return;
            SetBusy(true);
            await Task.Run(() => { if (state.Healthy) LaunchSetup.Disable(game); else LaunchSetup.Enable(game); });
            ShowFeedback(state.Healthy ? "Launch check disabled. Previous commands restored." : "Launch check enabled. Reopen UltimMC, then launch this instance.", false);
        }
        catch (Exception ex) { ShowFeedback(ex.Message, true); }
        finally { SetBusy(false); RefreshLaunchSetup(); }
    }
}
