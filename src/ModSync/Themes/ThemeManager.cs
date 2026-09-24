using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ModSync.Themes;

/// <summary>
/// Manages Apple-inspired Light and Dark palettes according to Windows system theme.
/// </summary>
public static class ThemeManager
{
    public static bool IsDarkTheme { get; private set; }

    public static void InitializeTheme()
    {
        IsDarkTheme = CheckIfWindowsIsDark();
        ApplyTheme(IsDarkTheme);
    }

    public static void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ApplyTheme(IsDarkTheme);
    }

    public static void ApplyTheme(bool dark)
    {
        IsDarkTheme = dark;
        var res = Application.Current.Resources;

        if (dark)
        {
            // Apple Dark Mode Palette
            res["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
            res["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            res["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            res["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D));
            res["SecondaryButtonBrush"] = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            res["SecondaryButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));
            res["SecondaryButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x32));
            res["ModalBackdropBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
            res["InputBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        }
        else
        {
            // Apple Light Mode Palette
            res["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            res["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B));
            res["SecondaryButtonBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["SecondaryButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDC));
            res["SecondaryButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(0xCE, 0xCE, 0xD2));
            res["ModalBackdropBrush"] = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
            res["InputBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
        }

        // Shared Accent Colors
        res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
        res["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xE3));
        res["AccentPressedBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x62, 0xC4));

        res["SuccessBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        res["WarningBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00));
        res["ErrorBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
    }

    private static bool CheckIfWindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int lightThemeValue)
            {
                return lightThemeValue == 0;
            }
        }
        catch { }

        return false;
    }
}
