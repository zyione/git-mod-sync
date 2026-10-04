using System.Security.Principal;
using System.Windows;
using ModSync.Themes;

namespace ModSync;

/// <summary>
/// Application entry and theme setup.
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

        ThemeManager.InitializeTheme();
    }
}
