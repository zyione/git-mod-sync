using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using ModSync.Themes;

namespace ModSync;

/// <summary>
/// Application entry, admin elevation safeguard, and theme setup.
/// </summary>
public partial class App : Application
{
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsRunningAsAdministrator())
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = processPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    Shutdown(0);
                    return;
                }
            }
            catch
            {
                // Elevation was cancelled by the user
                Shutdown(1);
                return;
            }
        }

        ThemeManager.InitializeTheme();
    }
}

