using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Naifrog.Agent;

internal static class AgentUi
{
    public static readonly Brush Ink = Color("#20252D"), Muted = Color("#737B88"), Line = Color("#E5E7EB"), Blue = Color("#356BE8"), Surface = Color("#F6F7F9");
    public static SolidColorBrush Color(string color) { var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
    public static void Install(Window window)
    {
        window.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); window.FontSize = 13; window.Foreground = Ink; window.Background = Brushes.White;
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White)); button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        button.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        button.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
 <Border x:Name='B' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='7' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
 <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Opacity' Value='0.72'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='B' Property='Opacity' Value='0.38'/></Trigger></ControlTemplate.Triggers>
</ControlTemplate>")));
        window.Resources[typeof(Button)] = button;
        foreach (string type in new[] { "TextBox", "PasswordBox" })
        {
            var style = new Style(type == "TextBox" ? typeof(TextBox) : typeof(PasswordBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White)); style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8))); style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
            style.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='" + type + @"'>
 <Border x:Name='B' CornerRadius='8' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ScrollViewer x:Name='PART_ContentHost'/></Border>
 <ControlTemplate.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#799CEE'/></Trigger></ControlTemplate.Triggers>
</ControlTemplate>")));
            window.Resources[type == "TextBox" ? typeof(TextBox) : typeof(PasswordBox)] = style;
        }
        window.Resources[typeof(CheckBox)] = new Style(typeof(CheckBox)) { Setters = { new Setter(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 5)), new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center) } };
    }
    public static TextBlock Text(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, MinHeight = 32 };
        if (primary) { button.Background = Ink; button.Foreground = Brushes.White; button.BorderBrush = Ink; }
        button.Click += (_, _) => action(); return button;
    }
    public static TextBox Code(string text = "", double maxHeight = 300) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"), FontSize = 12, Background = Surface, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = maxHeight, Padding = new Thickness(12) };
}
