using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using ModSync.Services;

namespace ModSync;

public partial class MainWindow
{
    private readonly List<(string Path, string Label)> _detectedExclusionMods = new();
    private readonly HashSet<string> _selectedExclusionMods = new(StringComparer.OrdinalIgnoreCase);

    private void PopulateDetectedMods()
    {
        _detectedExclusionMods.Clear();
        var failures = new List<string>();
        foreach (var (folder, source) in new[] { (_configService.ResolvedModsFolder, "Local"), (PackSyncService.RepositoryModsFolder(_configService.ResolvedRepositoryFolder), "Repository") })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in PackSyncService.SafeFiles(folder, _configService.Config.SyncSubdirectories)
                    .Where(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)))
                {
                    string relative = Path.GetRelativePath(folder, path);
                    if (!_ignoreService.IsIgnored(relative, path))
                        _detectedExclusionMods.Add((path, $"{relative} · {source}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(source);
                _logger.Warning($"Could not list {source} mods: {ex.Message}");
            }
        }
        _selectedExclusionMods.IntersectWith(_detectedExclusionMods.Select(m => m.Path));
        RenderDetectedExclusions();
        if (failures.Count > 0)
        {
            ExclusionDropFeedback.Text = $"Couldn't read {string.Join(" and ", failures)} mods. Check folder access and reopen this screen.";
            ExclusionDropFeedback.Visibility = Visibility.Visible;
        }
    }

    private IEnumerable<(string Path, string Label)> FilteredExclusionMods() => _detectedExclusionMods
        .Where(m => m.Label.Contains(ExclusionSearchInput.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase);

    private void RenderDetectedExclusions()
    {
        if (DetectedModsList == null || ExcludeSelectedModsButton == null) return;
        DetectedModsList.Children.Clear();
        foreach (var mod in FilteredExclusionMods())
        {
            var checkbox = new CheckBox
            {
                Content = new TextBlock { Text = mod.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = mod.Label,
                IsChecked = _selectedExclusionMods.Contains(mod.Path),
                MinHeight = 32,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            System.Windows.Automation.AutomationProperties.SetName(checkbox, mod.Label);
            checkbox.Checked += (_, _) => { _selectedExclusionMods.Add(mod.Path); UpdateExclusionSelection(); };
            checkbox.Unchecked += (_, _) => { _selectedExclusionMods.Remove(mod.Path); UpdateExclusionSelection(); };
            DetectedModsList.Children.Add(checkbox);
        }
        if (DetectedModsList.Children.Count == 0)
            DetectedModsList.Children.Add(new TextBlock { Text = _detectedExclusionMods.Count == 0
                ? "No unexcluded mods found." : "No mods match your search.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        UpdateExclusionSelection();
    }

    private void UpdateExclusionSelection()
    {
        ExcludeSelectedModsButton.IsEnabled = _selectedExclusionMods.Count > 0;
        ExcludeSelectedModsButton.Content = _selectedExclusionMods.Count > 0 ? $"Exclude Selected ({_selectedExclusionMods.Count})" : "Exclude Selected";
    }

    private void ExclusionSearch_Changed(object sender, TextChangedEventArgs e) => RenderDetectedExclusions();
    private void SelectShownMods_Click(object sender, RoutedEventArgs e)
    {
        _selectedExclusionMods.UnionWith(FilteredExclusionMods().Select(m => m.Path));
        RenderDetectedExclusions();
    }
    private void ClearSelectedMods_Click(object sender, RoutedEventArgs e)
    {
        _selectedExclusionMods.Clear();
        RenderDetectedExclusions();
    }
    private void ExcludeSelectedMods_Click(object sender, RoutedEventArgs e) => ExcludeDroppedFiles(_selectedExclusionMods.ToArray());

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
            var result = _ignoreService.ExcludeFiles(paths, RememberModIdentity.IsChecked == true);
            if (!result.Success)
            {
                ExclusionDropFeedback.Text = "Couldn't save exclusions. Check that the configuration folder is writable and try again.";
                return false;
            }
            RefreshIgnoredModsList();
            PopulateDetectedMods();
            UpdateIgnoredModsUI();
            ExclusionDropFeedback.Text = result.Added > 0
                ? $"Added {result.Added} exclusion{(result.Added == 1 ? "" : "s")}."
                : "No new exclusions added.";
            if (result.Skipped > 0)
                ExclusionDropFeedback.Text += $" Skipped {result.Skipped} already excluded or unsupported item{(result.Skipped == 1 ? "" : "s")}.";
            if (result.FilenameOnly > 0)
                ExclusionDropFeedback.Text += $" {result.FilenameOnly} used filenames because no readable Fabric ID was found; new filenames will need a new exclusion.";
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
