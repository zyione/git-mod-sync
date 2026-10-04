using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ModSync.Themes;

/// <summary>
/// Manages ModSync Light and Dark palettes according to Windows system theme.
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
        var icon = new BitmapImage(new Uri($"pack://application:,,,/ModSync;component/Assets/modsync-{(dark ? "dark" : "light")}.ico"));
        icon.Freeze();
        res["AppIcon"] = icon;

        if (dark)
        {
            // Neutral Dark Mode Palette
            res["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
            res["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x23));
            res["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x23));
            res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x3B));
            res["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB5));
            res["SecondaryButtonBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x3B));
            res["SecondaryButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));
            res["SecondaryButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x32));
            res["ModalBackdropBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
            res["InputBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
        }
        else
        {
            // Neutral Light Mode Palette
            res["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            res["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["PrimaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x69));
            res["SecondaryButtonBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            res["SecondaryButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDC));
            res["SecondaryButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(0xCE, 0xCE, 0xD2));
            res["ModalBackdropBrush"] = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
            res["InputBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
        }

        // Shared Accent Colors
        res["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x6B, 0xD9));
        res["AccentTextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x70, 0xB4, 0xFF) : Color.FromRgb(0x00, 0x5C, 0xBF));
        res["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xE3));
        res["AccentPressedBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x62, 0xC4));

        res["SuccessBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x69, 0xD7, 0x8C) : Color.FromRgb(0x19, 0x70, 0x3A));
        res["WarningBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xF0, 0xBC, 0x60) : Color.FromRgb(0x86, 0x55, 0x00));
        res["ErrorBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xFF, 0x80, 0x78) : Color.FromRgb(0xBC, 0x2B, 0x22));
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
