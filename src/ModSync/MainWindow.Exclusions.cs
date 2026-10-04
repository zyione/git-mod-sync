using System.Windows;
using Microsoft.Win32;

namespace ModSync;

public partial class MainWindow
{
    private void ExclusionDropZone_DragOver(object sender, DragEventArgs e)
    {
        bool accepts = e.Data.GetDataPresent(DataFormats.FileDrop)
            && (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        ExclusionDropZone.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
            accepts ? "AccentTextBrush" : "CardBorderBrush");
        ExclusionDropHint.Text = accepts ? "Release to exclude these .jar files" : "Drop .jar files here to exclude them";
        e.Handled = true;
    }

    private void ResetExclusionDropZone()
    {
        ExclusionDropZone.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "CardBorderBrush");
        ExclusionDropHint.Text = "Drop .jar files here to exclude them";
    }

    private void ExclusionDropZone_DragLeave(object sender, DragEventArgs e) => ResetExclusionDropZone();

    private void ExclusionDropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ResetExclusionDropZone();
        if ((e.AllowedEffects & DragDropEffects.Copy) != 0
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (ExcludeDroppedFiles(paths)) e.Effects = DragDropEffects.Copy;
        }
    }

    private void ChooseExcludedFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose mods to exclude",
            Filter = "Minecraft mods (*.jar)|*.jar",
            Multiselect = true,
            CheckFileExists = true
        };
        if (Directory.Exists(_configService.ResolvedModsFolder))
            dialog.InitialDirectory = _configService.ResolvedModsFolder;
        if (dialog.ShowDialog(this) == true) ExcludeDroppedFiles(dialog.FileNames);
    }

    private bool ExcludeDroppedFiles(string[] paths)
    {
        ExclusionDropFeedback.Visibility = Visibility.Visible;
        try
        {
            var result = _ignoreService.ExcludeFiles(paths);
            if (!result.Success)
            {
                ExclusionDropFeedback.Text = "Couldn't save exclusions. Check that the configuration folder is writable and try again.";
                return false;
            }
            RefreshIgnoredModsList();
            PopulateDetectedMods();
            UpdateIgnoredModsUI();
            ExclusionDropFeedback.Text = result.Added > 0
                ? $"Excluded {result.Added} mod{(result.Added == 1 ? "" : "s")}."
                : "No new exclusions added.";
            if (result.Skipped > 0)
                ExclusionDropFeedback.Text += $" Skipped {result.Skipped} already excluded or unsupported item{(result.Skipped == 1 ? "" : "s")}.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Could not exclude dropped mods", ex);
            ExclusionDropFeedback.Text = "Couldn't add exclusions. Choose existing .jar files and try again.";
            return false;
        }
    }
}
