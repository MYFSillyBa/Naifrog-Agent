using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;

namespace Naifrog.UI;

internal static class Ui
{
    public static readonly Brush Ink = Brush("#213C32");
    public static readonly Brush Muted = Brush("#7D8071");
    public static readonly Brush Accent = Brush("#315F49");
    public static readonly Brush Canvas = Brush("#F7F3EA");
    public static SolidColorBrush Brush(string color)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public static void Install()
    {
        var resources = System.Windows.Application.Current.Resources;
        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
        window.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        window.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        resources[typeof(Window)] = window;
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 4, 8, 4)));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#EDF1E8")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#D4DDCF")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        button.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        button.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
 <Border x:Name='Surface' CornerRadius='7' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' Padding='{TemplateBinding Padding}'>
  <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
 </Border>
 <ControlTemplate.Triggers>
  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.82'/></Trigger>
  <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.65'/></Trigger>
  <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#B88D27'/></Trigger>
  <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger>
 </ControlTemplate.Triggers>
</ControlTemplate>")));
        resources[typeof(Button)] = button;
        var text = new Style(typeof(TextBox));
        text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        text.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#CCD6C6")));
        text.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        text.Setters.Add(new Setter(Control.TemplateProperty, InputTemplate("TextBox")));
        resources[typeof(TextBox)] = text;
        var password = new Style(typeof(PasswordBox));
        password.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        password.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#CCD6C6")));
        password.Setters.Add(new Setter(Control.TemplateProperty, InputTemplate("PasswordBox")));
        resources[typeof(PasswordBox)] = password;
        var check = new Style(typeof(CheckBox));
        check.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 7, 0, 7)));
        check.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        resources[typeof(CheckBox)] = check;
    }
    private static ControlTemplate InputTemplate(string type) => (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='" + type + @"'>
 <Border x:Name='Field' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='6' Padding='{TemplateBinding Padding}'>
  <ScrollViewer x:Name='PART_ContentHost'/>
 </Border>
 <ControlTemplate.Triggers>
  <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Field' Property='BorderBrush' Value='#52795B'/></Trigger>
  <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Field' Property='Opacity' Value='0.5'/></Trigger>
 </ControlTemplate.Triggers>
</ControlTemplate>");
    public static TextBlock Text(string text, double size = 13, Brush? foreground = null) => new()
    { Text = text, FontSize = size, Foreground = foreground ?? Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
    public static StackPanel Page(string title, string description)
    {
        var page = new StackPanel { Margin = new Thickness(32, 24, 32, 28), MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heading = Text(title, 25); heading.FontWeight = FontWeights.SemiBold;
        page.Children.Add(heading); page.Children.Add(Text(description, 13, Muted)); return page;
    }
    public static StackPanel Card(StackPanel page, string title, string? description = null)
    {
        var body = new StackPanel { Margin = new Thickness(18, 15, 18, 16) };
        var row = new DockPanel();
        var mark = new Border { Width = 4, Height = 17, Background = Brush("#D9B54B"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 10, 7) }; DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
        var heading = Text(title, 16); heading.FontWeight = FontWeights.SemiBold; row.Children.Add(heading); body.Children.Add(row);
        if (description is not null) body.Children.Add(Text(description, 12, Muted));
        page.Children.Add(new Border { Child = body, Background = Brushes.White, BorderBrush = Brush("#E5E3D7"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 14, 0, 0) });
        return body;
    }
    public static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, MinHeight = 34 };
        if (primary) { button.Background = Accent; button.Foreground = Brushes.White; button.BorderBrush = Accent; }
        button.Click += (_, _) => action(); return button;
    }
    public static WrapPanel Actions(Panel parent)
    { var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; parent.Children.Add(row); return row; }
}
