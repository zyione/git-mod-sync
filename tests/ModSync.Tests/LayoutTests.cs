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
                ((TextBlock)window.FindName("PackStatusText")).Text = "Resource packs: 3 local / 3 repository\nShaders: 1 local / 1 repository • Active: Complementary.zip";
                foreach (bool dark in new[] { false, true })
                {
                    ThemeManager.ApplyTheme(dark);
                    root.Measure(new Size(620, 760));
                    root.Arrange(new Rect(0, 0, 620, 760));
                    root.UpdateLayout();
                    foreach (var name in new[] { "SyncModsButton", "SyncOnlyModsButton", "SyncResourcesButton", "SyncShadersButton", "PushModsButton" })
                    {
                        var button = (Button)window.FindName(name);
                        var point = button.TranslatePoint(new Point(0, 0), root);
                        Assert.IsTrue(button.ActualWidth > 0 && point.Y >= 0 && point.Y + button.ActualHeight <= 760, name + " must fit the window.");
                    }
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
