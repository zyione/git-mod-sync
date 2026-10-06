using System.Security.Principal;
using System.Windows;
using ModSync.Themes;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

/// <summary>
/// Application entry and theme setup.
/// </summary>
public partial class App : Application
{
    private Mutex? _dashboardMutex;
    private readonly CancellationTokenSource _bridgeCancellation = new();
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (AppRelocation.Detect(Environment.ProcessPath!) is { } relocation)
            {
                if (e.Args.Length > 0)
                    throw new IOException("Open ModSync directly to move it outside the instance, then review Launch check setup. This launch was cancelled.");
                ThemeManager.InitializeTheme();
                new RelocationWindow(relocation).ShowDialog();
                Shutdown();
                return;
            }
            bool relocated = e.Args.Length == 2 && e.Args[0] == "--relocated" &&
                System.Text.RegularExpressions.Regex.IsMatch(e.Args[1], "^[a-f0-9]{32}$");
            if (e.Args.Length > 0 && !relocated)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--pre-launch" || !Directory.Exists(e.Args[1]))
                    throw new IOException("Expected --pre-launch followed by the Minecraft game folder.");
                Shutdown(await LaunchBridge.WaitForDecisionAsync(e.Args[1]));
                return;
            }
            _dashboardMutex = new Mutex(true, "Local\\ModSync-dashboard-" + PathUtils.InstanceKey(PathUtils.GetAppDirectory()), out bool owns);
            if (!owns) { _dashboardMutex.Dispose(); _dashboardMutex = null; Shutdown(); return; }
            AppStorage.Initialize();
            ThemeManager.InitializeTheme();
            var window = new MainWindow();
            MainWindow = window;
            window.Closed += (_, _) => { _bridgeCancellation.Cancel(); Shutdown(); };
            window.Show();
            if (relocated) File.WriteAllText(Path.Combine(PathUtils.GetAppDirectory(), "relocation-ready-" + e.Args[1]), "Ready");
            await LaunchBridge.ListenAsync((instance, cancellation) => Dispatcher.InvokeAsync(() => window.RequestLaunchAsync(instance, cancellation)).Task.Unwrap(),
                play => Dispatcher.InvokeAsync(() => window.LaunchReplySent(play)), _bridgeCancellation.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show("ModSync could not start the launch check. Minecraft has not been started.\n\n" + ex.Message,
                "ModSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bridgeCancellation.Cancel();
        if (_dashboardMutex != null) { _dashboardMutex.ReleaseMutex(); _dashboardMutex.Dispose(); }
        base.OnExit(e);
    }
}
