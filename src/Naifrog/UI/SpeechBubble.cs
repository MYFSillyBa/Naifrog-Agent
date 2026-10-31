using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Naifrog.UI;

internal sealed class SpeechBubble : Window
{
    private readonly Window pet;
    private readonly TextBlock message = Ui.Text("");
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(18) };
    public SpeechBubble(Window pet, Action<byte[]?> openChat)
    {
        this.pet = pet; Owner = pet; Width = 320; MaxHeight = 230; SizeToContent = SizeToContent.Height;
        Title = "奶蛙说"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        var body = new StackPanel { Margin = new Thickness(15) };
        var actions = Ui.Actions(body); actions.Children.Add(Ui.Button("聊天", () => openChat(null))); actions.Children.Add(Ui.Button("收起", Hide));
        body.Children.Add(new ScrollViewer { Content = message, MaxHeight = 135, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = new Border { Child = body, Background = Ui.Brush("#FFFCEB"), BorderBrush = Ui.Brush("#D6DDAA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        timer.Tick += (_, _) => { if (!IsMouseOver) { Hide(); timer.Stop(); } };
        Closed += (_, _) => timer.Stop(); SizeChanged += (_, _) => Reposition();
    }
    public void SetText(string text) { message.Text = text; Show(); Reposition(); timer.Stop(); timer.Start(); }
    public void Reposition()
    {
        if (!IsVisible) return;
        Rect area = SystemParameters.WorkArea;
        double x = pet.Left-Width-10; if (x < area.Left) x = pet.Left+pet.Width+10;
        Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right-Width));
        Top = Math.Clamp(pet.Top+30, area.Top, Math.Max(area.Top, area.Bottom-Math.Max(80, ActualHeight)));
    }
}
