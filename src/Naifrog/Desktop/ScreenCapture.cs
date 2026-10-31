using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Naifrog.Core;
using Naifrog.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Naifrog.Desktop;

internal static class ScreenCapture
{
    public static string[] MonitorNames()
    {
        var screens = Forms.Screen.AllScreens;
        var names = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++) names[i] = $"显示器 {i+1} · {screens[i].Bounds.Width} × {screens[i].Bounds.Height}" + (screens[i].Primary ? "（主屏）" : "");
        return names;
    }
    public static byte[] Capture(PrivacySettings settings)
    {
        if (!settings.ScreenEnabled) throw new InvalidOperationException("请先在「屏幕与隐私」中启用本次运行的屏幕授权。");
        var screens = Forms.Screen.AllScreens;
        if (settings.MonitorIndex < 0 || settings.MonitorIndex >= screens.Length) throw new InvalidOperationException("显示器已变化，请重新选择。");
        var bounds = screens[settings.MonitorIndex].Bounds;
        if (NativeDesktop.HasExcludedWindow(bounds, settings.ExcludedApps)) throw new InvalidOperationException("所选屏幕存在排除的应用，本次未截图。");
        using var image = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Drawing.Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
        // Cap model image dimensions while preserving aspect ratio, reducing upload size.
        int width = Math.Min(1600, image.Width), height = Math.Max(1, (int)((double)image.Height*width/image.Width));
        using var scaled = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(scaled)) { graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, 0, 0, width, height); }
        using var output = new MemoryStream(); scaled.Save(output, Drawing.Imaging.ImageFormat.Png); return output.ToArray();
    }
    public static void Preview(Window owner, byte[] bytes, Action<byte[]> attach)
    {
        var window = new Window { Title = "截图预览", Width = 760, Height = 560, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Ui.Canvas, Topmost = true };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Ui.Text("预览不会上传。选择「附到聊天」后，再点击聊天的发送按钮才会交给模型。", 12, Ui.Muted));
        var buttons = Ui.Actions(bottom);
        buttons.Children.Add(Ui.Button("附到聊天", () => { attach(bytes); window.Close(); }, true));
        buttons.Children.Add(Ui.Button("关闭预览", window.Close));
        panel.Children.Add(bottom);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes); bitmap.EndInit(); bitmap.Freeze();
        panel.Children.Add(new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform });
        window.Content = panel; window.Show();
    }
}
