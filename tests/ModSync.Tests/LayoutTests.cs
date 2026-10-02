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
                ((TextBlock)window.FindName("PackStatusText")).Text = "3 local / 3 repository";
                ((TextBlock)window.FindName("ShaderStatusText")).Text = "1 local / 1 repository • Active: Complementary.zip";
                foreach (bool dark in new[] { false, true })
                {
                    ThemeManager.ApplyTheme(dark);
                    foreach (int width in new[] { 580, 620 })
                    {
                        root.Measure(new Size(width, 760));
                        root.Arrange(new Rect(0, 0, width, 760));
                        root.UpdateLayout();
                        foreach (var name in new[] { "SyncModsButton", "SyncOnlyModsButton", "SyncResourcesButton", "SyncShadersButton", "FabricUpdateButton", "PushModsButton", "PushResourcesButton", "PushShadersButton" })
                        {
                            var button = (Button)window.FindName(name);
                            var point = button.TranslatePoint(new Point(0, 0), root);
                            Assert.IsTrue(button.ActualWidth > 0 && point.X >= 0 && point.X + button.ActualWidth <= width && point.Y >= 0 && point.Y + button.ActualHeight <= 760, name + " must fit the window.");
                        }
                    }
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
                    var bitmap = new RenderTargetBitmap(620, 760, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(AppContext.BaseDirectory, dark ? "ui-preview-dark.png" : "ui-preview-light.png"));
                    encoder.Save(output);
                }
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
