using System.Diagnostics;
using System.Windows;
using ModSync.Services;

namespace ModSync;

public partial class RelocationWindow : Window
{
    private RelocationLocation _location;
    public RelocationWindow(RelocationLocation location)
    {
        InitializeComponent(); _location = location; RefreshDestination();
    }
    private void RefreshDestination()
    {
        DestinationText.Text = Path.GetDirectoryName(_location.Destination);
        bool exists = File.Exists(_location.Destination);
        ConfirmButton.Content = exists ? "Open existing copy" : "Confirm & move";
        ExplanationText.Text = exists
            ? "This folder already contains ModSync. Open that copy without replacing it. The copy inside your instance will be kept."
            : "ModSync will reopen from this folder, then remove only its old executable after startup. Existing settings can be imported during instance selection; other old files are kept.";
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder containing UltimMC.exe", InitialDirectory = _location.Launcher };
        if (dialog.ShowDialog(this) != true) return;
        try { AppRelocation.ValidateDestination(dialog.FolderName); _location = _location with { Launcher = dialog.FolderName }; ErrorText.Text = ""; RefreshDestination(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppRelocation.ValidateDestination(_location.Launcher);
            if (File.Exists(_location.Destination))
                Process.Start(new ProcessStartInfo(_location.Destination) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_location.Destination) });
            else AppRelocation.Start(AppRelocation.Prepare(_location, Environment.ProcessId));
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; RefreshDestination(); }
    }
}
