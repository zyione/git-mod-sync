using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Themes;

namespace ModSync.Tests;

[TestClass]
public class LayoutTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SavePreview(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(output);
    }

    [TestMethod]
    public void DashboardActionsFitMinimumWindowAndRenderInBothThemes()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                var root = (FrameworkElement)window.Content;
                var config = (ModSync.Services.ConfigService)typeof(MainWindow).GetField("_configService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                config.Config.ModsFolder = Path.Combine(AppContext.BaseDirectory, "4Stoogies", ".minecraft", "mods");
                config.Config.InstanceSelectionCompleted = true;
                Directory.CreateDirectory(config.MinecraftFolder);
                config.Config.SyncResourcePacks = true; config.Config.SyncShaderPacks = true;
                typeof(MainWindow).GetMethod("RenderCategoryStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                ((TextBlock)window.FindName("PackStatusText")).Text = "3 local · 3 repository";
                ((TextBlock)window.FindName("ShaderStatusText")).Text = "1 local · 1 repository\nActive: Complementary.zip";
                typeof(MainWindow).GetMethod("UpdateStatusCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("ApplyFabricStatusToUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object?[] { null });
                foreach (bool dark in new[] { false, true })
                {
                    ThemeManager.ApplyTheme(dark);
                    var appIcon = (BitmapFrame)app.Resources["AppIcon"];
                    Assert.AreEqual(9, appIcon.Decoder!.Frames.Count, "Keep all ICO resolutions available for native taskbar icon selection.");
                    Assert.AreEqual(256, ((BitmapSource)app.Resources["HeaderIcon"]).PixelWidth, "Render the header from high-resolution artwork.");
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                    var nativeIcon = SendMessage(handle, 0x007F, new IntPtr(1), IntPtr.Zero); // WM_GETICON / ICON_BIG
                    Assert.AreNotEqual(IntPtr.Zero, nativeIcon, "The running window must provide a taskbar icon.");
                    var nativeBitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(nativeIcon,
                        Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    Assert.IsTrue(nativeBitmap.PixelWidth >= 32, "The native taskbar icon must not use a padded 16px image.");
                    foreach (int width in new[] { 580, 620 })
                    {
                        root.Measure(new Size(width, 760));
                        root.Arrange(new Rect(0, 0, width, 760));
                        root.UpdateLayout();
                        foreach (var name in new[] { "MainInstanceButton", "LaunchSetupButton", "ChooseInstanceButton", "DashboardCheckButton", "SyncOnlyModsButton", "SyncResourcesButton", "SyncShadersButton", "FabricUpdateButton" })
                        {
                            var button = (Button)window.FindName(name);
                            if (button.Visibility == Visibility.Collapsed) continue;
                            var point = button.TranslatePoint(new Point(0, 0), root);
                            Assert.IsTrue(button.ActualWidth > 0 && point.X >= 0 && point.X + button.ActualWidth <= width, name + " must fit the window.");
                        }
                    }
                    var menu = (ContextMenu)((MenuItem)window.FindName("PushOnlyModsButton")).Parent;
                    typeof(MainWindow).GetMethod("PrepareActionsMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { menu });
                    menu.Measure(new Size(300, 400)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
                    SavePreview(menu, dark ? "ui-menu-dark.png" : "ui-menu-light.png");
                    foreach (var name in new[] { "PushOnlyModsButton", "ReinstallModsButton", "PushResourcesButton", "PushShadersButton", "SyncOrderButton", "PushOrderButton" })
                        Assert.IsInstanceOfType(window.FindName(name), typeof(MenuItem), "Advanced actions belong in menus.");
                    var tooltip = new ToolTip { Content = "Sync shaderpacks and the active shader" };
                    tooltip.Measure(new Size(320, 100));
                    tooltip.Arrange(new Rect(tooltip.DesiredSize));
                    tooltip.UpdateLayout();
                    var tooltipText = Descendants(tooltip).OfType<TextBlock>().First();
                    var foreground = ((SolidColorBrush)tooltipText.Foreground).Color;
                    var background = ((SolidColorBrush)tooltip.Background).Color;
                    Assert.AreEqual(((SolidColorBrush)app.Resources["PrimaryTextBrush"]).Color, foreground);
                    Assert.IsTrue(Math.Abs(foreground.R - background.R) > 150, "Tooltip text must contrast with its background.");
                    SavePreview(tooltip, dark ? "tooltip-preview-dark.png" : "tooltip-preview-light.png");
                    SavePreview(root, dark ? "ui-preview-dark.png" : "ui-preview-light.png");
                    foreach (bool healthy in new[] { true, false })
                    {
                        ((TextBlock)window.FindName("LaunchSetupHeading")).Text = healthy ? "Launch check · Enabled" : "Launch check · Needs repair";
                        ((TextBlock)window.FindName("LaunchSetupDescription")).Text = healthy ? "Checks this instance whenever you press Launch." : "Launch check needs repair.";
                        ((Button)window.FindName("LaunchSetupButton")).Content = healthy ? "Manage" : "Repair";
                        root.Measure(new Size(580, 640)); root.Arrange(new Rect(0, 0, 580, 640)); root.UpdateLayout();
                        var setupButton = (Button)window.FindName("LaunchSetupButton");
                        var position = setupButton.TranslatePoint(new Point(0, 0), root);
                        Assert.IsTrue(position.Y + setupButton.ActualHeight < 640 && position.X + setupButton.ActualWidth <= 580);
                        SavePreview(root, $"ui-launch-setup-{(healthy ? "enabled" : "repair")}-{(dark ? "dark" : "light")}.png");
                    }
                    typeof(MainWindow).GetMethod("RefreshLaunchSetup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    typeof(MainWindow).GetMethod("ChooseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, new object?[] { "Have you excluded your personal mods?", "Excluded mods stay on your computer and won’t be uploaded. If you haven’t added personal mods, choose Skip. We’ll ask again when new local mods appear.", "Review exclusions", "Already excluded", "Skip — no personal mods" });
                    root.Measure(new Size(580, 640)); root.Arrange(new Rect(0, 0, 580, 640)); root.UpdateLayout();
                    var choiceSheet = (FrameworkElement)window.FindName("ChoiceSheet");
                    Assert.AreEqual(Visibility.Visible, choiceSheet.Visibility);
                    Assert.IsTrue(((FrameworkElement)choiceSheet.Parent).ActualHeight >= choiceSheet.ActualHeight && choiceSheet.ActualHeight > 150,
                        "The visible prompt must have space in its parent, not merely previously measured buttons.");
                    foreach (var name in new[] { "ChoiceFirst", "ChoiceSecond", "ChoiceThird" })
                    {
                        var button = (Button)window.FindName(name);
                        var location = button.TranslatePoint(new Point(), root);
                        Assert.IsTrue(button.ActualHeight >= 36 && location.Y >= 0 && location.Y + button.ActualHeight <= 640);
                        Assert.IsTrue(location.X >= 0 && location.X + button.ActualWidth <= 580);
                    }
                    SavePreview(root, dark ? "ui-personal-mods-dark.png" : "ui-personal-mods-light.png");
                    var choiceCard = (FrameworkElement)VisualTreeHelper.GetParent(choiceSheet.Parent);
                    Assert.IsTrue(choiceCard.ActualHeight >= choiceSheet.ActualHeight, "Reopened dialog content must not be clipped by a stale card height.");
                    foreach (var (title, body, first, second, third, name) in new[] {
                        ("Everything is synchronized", "Enabled content matches the checked repository versions. Excluded mods were preserved. Continue to Minecraft and close ModSync?", "Play & close ModSync", "Play & keep ModSync open", (string?)null, "ready"),
                        ("Minecraft is waiting", "Could not verify repository updates. Check your connection and try again, or play with your current files.", "Check again", "Play anyway", (string?)"Cancel launch", "offline") })
                    {
                        typeof(MainWindow).GetMethod("ChooseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(window, new object?[] { title, body, first, second, third });
                        root.Measure(new Size(580, 640)); root.Arrange(new Rect(0, 0, 580, 640)); root.UpdateLayout();
                        Assert.IsTrue(choiceCard.ActualHeight >= choiceSheet.ActualHeight);
                        SavePreview(root, $"ui-launch-{name}-{(dark ? "dark" : "light")}.png");
                    }
                    typeof(MainWindow).GetMethod("CloseModal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    ((FrameworkElement)window.FindName("ProgressArea")).Visibility = Visibility.Visible;
                    ((FrameworkElement)window.FindName("FeedbackCard")).Visibility = Visibility.Visible;
                    ((TextBlock)window.FindName("FeedbackMessageText")).Text = "Downloading repository updates…";
                    root.Measure(new Size(580, 760)); root.Arrange(new Rect(0, 0, 580, 760)); root.UpdateLayout();
                    var actions = (ScrollViewer)window.FindName("ActionsScrollViewer");
                    var fabric = (FrameworkElement)window.FindName("FabricCard");
                    var feedback = (FrameworkElement)window.FindName("FeedbackCard");
                    Assert.IsTrue(actions.ScrollableHeight >= 0, "Content must scroll rather than overlap navigation.");
                    Assert.IsTrue(actions.TranslatePoint(new Point(), root).Y + actions.ActualHeight <= feedback.TranslatePoint(new Point(), root).Y);
                    typeof(MainWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object?[] { true, "Checking repositories…" });
                    Assert.IsFalse(((Button)window.FindName("DashboardCheckButton")).IsEnabled);
                    Assert.IsFalse(((MenuItem)window.FindName("PushOnlyModsButton")).IsEnabled);
                    root.UpdateLayout();
                    var progress = (FrameworkElement)window.FindName("ProgressArea");
                    Assert.IsTrue(progress.ActualHeight > 0 && progress.TranslatePoint(new Point(), root).Y + progress.ActualHeight <= 760, "Progress must remain visible without scrolling.");
                    root.UpdateLayout();
                    SavePreview(root, dark ? "ui-busy-dark.png" : "ui-busy-light.png");
                    typeof(MainWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object?[] { false, null });
                    root.Measure(new Size(580, 640)); root.Arrange(new Rect(0, 0, 580, 640)); root.UpdateLayout();
                    var navigation = (Button)window.FindName("ChooseInstanceButton");
                    Assert.IsTrue(navigation.TranslatePoint(new Point(), root).Y + navigation.ActualHeight <= 640);
                    actions.ScrollToEnd(); root.UpdateLayout();
                    SavePreview(root, dark ? "ui-small-dark.png" : "ui-small-light.png");
                    actions.ScrollToTop();
                    ((FrameworkElement)window.FindName("ProgressArea")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)window.FindName("FeedbackCard")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)window.FindName("ModalBackdrop")).Visibility = Visibility.Visible;
                    ((FrameworkElement)window.FindName("SwitchRepoSheet")).Visibility = Visibility.Visible;
                    ((TextBox)window.FindName("RepoUrlInputBox")).Text = ModSync.Models.AppConfig.DefaultRepositoryUrl;
                    ((TextBox)window.FindName("ResourceRepoUrlInput")).Text = ModSync.Models.AppConfig.DefaultResourcePackRepositoryUrl;
                    ((TextBox)window.FindName("ShaderRepoUrlInput")).Text = ModSync.Models.AppConfig.DefaultShaderPackRepositoryUrl;
                    foreach (var name in new[] { "ModsBranchInput", "ResourceBranchInput", "ShaderBranchInput" }) ((TextBox)window.FindName(name)).Text = "main";
                    root.Measure(new Size(620, 760)); root.Arrange(new Rect(0, 0, 620, 760)); root.UpdateLayout();
                    SavePreview(root, dark ? "ui-repositories-dark.png" : "ui-repositories-light.png");
                    ((FrameworkElement)window.FindName("ModalBackdrop")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)window.FindName("SwitchRepoSheet")).Visibility = Visibility.Collapsed;
                    typeof(MainWindow).GetMethod("Settings_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                    root.Measure(new Size(580, 640)); root.Arrange(new Rect(0, 0, 580, 640)); root.UpdateLayout();
                    SavePreview(root, dark ? "ui-settings-dark.png" : "ui-settings-light.png");
                    ((FrameworkElement)window.FindName("SettingsSheet")).Visibility = Visibility.Collapsed;
                    typeof(MainWindow).GetMethod("ShowInstancePicker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    ((ListBox)window.FindName("InstanceList")).ItemsSource = new[] { new ModSync.Services.MinecraftInstance("My Minecraft instance", "C:/Games/PrismLauncher/instances/My instance/.minecraft") };
                    ((ListBox)window.FindName("InstanceList")).SelectedIndex = 0;
                    root.Measure(new Size(580, 760)); root.Arrange(new Rect(0, 0, 580, 760)); root.UpdateLayout();
                    SavePreview(root, dark ? "ui-instance-dark.png" : "ui-instance-light.png");
                    var instanceSheet = (FrameworkElement)window.FindName("InstanceSheet");
                    Assert.IsTrue(instanceSheet.ActualHeight > 0 && instanceSheet.TranslatePoint(new Point(), root).Y >= 0);
                    Assert.IsTrue(instanceSheet.TranslatePoint(new Point(), root).Y + instanceSheet.ActualHeight <= 760);
                    var item = (ListBoxItem)((ListBox)window.FindName("InstanceList")).ItemContainerGenerator.ContainerFromIndex(0);
                    var selectedText = Descendants(item).OfType<TextBlock>().First();
                    Assert.AreEqual(Colors.White, ((SolidColorBrush)selectedText.Foreground).Color, "Selected instance must remain readable.");

                    ((FrameworkElement)window.FindName("InstanceSheet")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)window.FindName("ModalBackdrop")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)window.FindName("MainDashboard")).IsEnabled = true;
                    typeof(MainWindow).GetMethod("UpdateModCountBadge", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object?[] { 10, 10 });
                    typeof(MainWindow).GetMethod("RenderCategoryStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    StringAssert.Contains(((TextBlock)window.FindName("StatusHeadingText")).Text, "Not checked");
                    var changes = new ModSync.Models.SyncSummary();
                    changes.Changes.Add(new ModSync.Models.ModChange { Type = ModSync.Models.ChangeType.Updated, RelativePath = "resourcepacks/SameName.zip" });
                    typeof(MainWindow).GetMethod("SetCheckedStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { ModSync.Models.SyncScope.All, changes });
                    Assert.AreEqual("1 change to review", ((TextBlock)window.FindName("ResourceStateText")).Text);
                    Assert.AreEqual("Review changes", ((Button)window.FindName("SyncResourcesButton")).Content);
                    Assert.AreEqual(Visibility.Visible, ((Button)window.FindName("SyncModsButton")).Visibility);
                    root.UpdateLayout();
                    SavePreview(root, dark ? "ui-changes-dark.png" : "ui-changes-light.png");
                    typeof(MainWindow).GetMethod("SetCheckedStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { ModSync.Models.SyncScope.All, new ModSync.Models.SyncSummary() });
                    Assert.AreEqual(Visibility.Collapsed, ((Button)window.FindName("SyncOnlyModsButton")).Visibility);
                    Assert.AreEqual(Visibility.Collapsed, ((Button)window.FindName("SyncResourcesButton")).Visibility);
                    Assert.AreEqual(Visibility.Collapsed, ((Button)window.FindName("SyncModsButton")).Visibility);
                    var fabricCurrent = new ModSync.Models.FabricStatusInfo { IsConfigured = true, IsMinecraftFound = true, IsUpToDate = true, MinecraftVersion = "1.20.1", InstalledLoaderVersion = "0.19.5", TargetLoaderVersion = "0.19.5" };
                    typeof(MainWindow).GetField("_currentFabricStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, fabricCurrent);
                    typeof(MainWindow).GetMethod("ApplyFabricStatusToUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { fabricCurrent });
                    Assert.AreEqual("Everything is up to date", ((TextBlock)window.FindName("OverallStatusText")).Text);
                    Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)window.FindName("FabricVersionComparison")).Visibility);
                    root.UpdateLayout();
                    SavePreview(root, dark ? "ui-synced-dark.png" : "ui-synced-light.png");
                    var metadata = new ModSync.Models.SyncSummary();
                    metadata.Changes.Add(new ModSync.Models.ModChange { Type = ModSync.Models.ChangeType.Updated, IsInternal = true, RelativePath = ".modsync-managed-packs.json" });
                    typeof(MainWindow).GetMethod("SetCheckedStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { ModSync.Models.SyncScope.ResourcePacks, metadata });
                    Assert.AreEqual("Review metadata updates", ((TextBlock)window.FindName("ResourceStateText")).Text);
                    Assert.AreEqual(Visibility.Visible, ((Button)window.FindName("SyncModsButton")).Visibility);
                    config.Config.SyncResourcePacks = false;
                    typeof(MainWindow).GetMethod("RenderCategoryStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    Assert.IsFalse(((MenuItem)window.FindName("PushResourcesButton")).IsEnabled);
                    Assert.AreEqual("Enable in Settings", ((Button)window.FindName("SyncResourcesButton")).Content);
                    config.Config.SyncResourcePacks = true;
                    typeof(MainWindow).GetMethod("MarkSyncComplete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { ModSync.Models.SyncScope.ResourcePackOrder });
                    StringAssert.Contains(((TextBlock)window.FindName("ResourceStateText")).Text, "files not checked");
                    ((Dictionary<ModSync.Models.SyncScope, string>)typeof(MainWindow).GetField("_categoryStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).Clear();
                    typeof(MainWindow).GetMethod("RenderCategoryStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);


                    foreach (var scope in new[] { ModSync.Models.SyncScope.Mods, ModSync.Models.SyncScope.ResourcePacks, ModSync.Models.SyncScope.Shaders })
                    {
                        typeof(MainWindow).GetMethod("ShowReinstallConfirmation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { scope, false });
                        root.Measure(new Size(580, 760)); root.Arrange(new Rect(0, 0, 580, 760)); root.UpdateLayout();
                        var sheet = (FrameworkElement)window.FindName("CleanReinstallConfirmSheet");
                        var point = sheet.TranslatePoint(new Point(), root);
                        Assert.IsTrue(point.Y >= 0 && point.Y + sheet.ActualHeight <= 760, "Scoped reinstall confirmation must fit.");
                        var title = ((TextBlock)window.FindName("ReinstallTitleText")).Text;
                        StringAssert.Contains(title, scope == ModSync.Models.SyncScope.Mods ? "Mods" : scope == ModSync.Models.SyncScope.ResourcePacks ? "Resource Packs" : "Shaders");
                        SavePreview(root, $"ui-reinstall-{scope}-{(dark ? "dark" : "light")}.png");
                        ((FrameworkElement)window.FindName("CleanReinstallConfirmSheet")).Visibility = Visibility.Collapsed;
                        ((FrameworkElement)window.FindName("ModalBackdrop")).Visibility = Visibility.Collapsed;
                        ((FrameworkElement)window.FindName("MainDashboard")).IsEnabled = true;
                    }

                }
                Directory.CreateDirectory(config.ResolvedModsFolder);
                var exampleMods = new[] { "zoom-personal-1.0.jar", "map-personal-2.0.jar", new string('a', 110) + ".jar" };
                foreach (var name in exampleMods) File.WriteAllText(Path.Combine(config.ResolvedModsFolder, name), "UI fixture");
                config.Config.IgnoredMods.Clear();
                foreach (bool dark in new[] { false, true })
                {
                    ThemeManager.ApplyTheme(dark);
                    typeof(MainWindow).GetMethod("OpenIgnoredModsModal_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, new object[] { window, new RoutedEventArgs() });
                    var search = (TextBox)window.FindName("ExclusionSearchInput");
                    search.Text = "zoom-personal";
                    var list = (StackPanel)window.FindName("DetectedModsList");
                    Assert.AreEqual(1, list.Children.Count);
                    ((CheckBox)list.Children[0]).IsChecked = true;
                    search.Text = "map-personal";
                    ((CheckBox)list.Children[0]).IsChecked = true;
                    search.Text = "";
                    var selected = (Button)window.FindName("ExcludeSelectedModsButton");
                    Assert.IsTrue(selected.IsEnabled);
                    StringAssert.Contains(selected.Content.ToString()!, "(2)");
                    root.Measure(new Size(580, 760)); root.Arrange(new Rect(0, 0, 580, 760)); root.UpdateLayout();
                    var zone = (Border)window.FindName("ExclusionDropZone");
                    var point = zone.TranslatePoint(new Point(), root);
                    Assert.IsTrue(zone.AllowDrop);
                    Assert.IsTrue(zone.ActualWidth > 0 && point.X >= 0 && point.X + zone.ActualWidth <= 580);
                    SavePreview(root, dark ? "ui-exclusions-dark.png" : "ui-exclusions-light.png");
                }
                foreach (var name in exampleMods) File.Delete(Path.Combine(config.ResolvedModsFolder, name));
                ((FrameworkElement)window.FindName("IgnoredModsSheet")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("ModalBackdrop")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("MainDashboard")).IsEnabled = true;
                var completed = new ModSync.Models.SyncSummary();
                completed.Changes.Add(new ModSync.Models.ModChange { RelativePath = "resourcepacks/A.zip", Type = ModSync.Models.ChangeType.Added });
                completed.Changes.Add(new ModSync.Models.ModChange { RelativePath = "options.txt (resource pack order)", Type = ModSync.Models.ChangeType.Updated, NewContent = "order" });
                typeof(MainWindow).GetField("_syncScope", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, ModSync.Models.SyncScope.ResourcePacks);
                typeof(MainWindow).GetMethod("ShowSyncCompletion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object?[] { completed, null, false });
                root.Measure(new Size(580, 760)); root.Arrange(new Rect(0, 0, 580, 760)); root.UpdateLayout();
                SavePreview(root, "ui-completion-dark.png");
                window.Close();
                app.Shutdown();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "UI rendering timed out.");
        if (error != null) throw error;
    }
}
