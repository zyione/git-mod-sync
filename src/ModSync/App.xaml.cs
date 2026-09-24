using System.Windows;
using ModSync.Themes;

namespace ModSync;

/// <summary>
/// Application entry and theme setup.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.InitializeTheme();
    }
}
