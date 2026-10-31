using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Naifrog.Desktop;
using Naifrog.UI;

namespace Naifrog;

internal sealed class ChatWindow : Window
{
    private readonly PetRuntime runtime;
    private readonly StackPanel conversation = new() { Margin = new Thickness(18, 12, 18, 14) };
    private readonly ScrollViewer scroll;
    private readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = Ui.Text("", 12, Ui.Muted), attachment = Ui.Text("", 12, Ui.Muted);
    private readonly Button send, cancel, retry;
    private CancellationTokenSource? request;
    private byte[]? image;
    private string lastText = "";
    private byte[]? lastImage;
    private bool closed;
    public ChatWindow(PetRuntime runtime)
    {
        this.runtime = runtime;
        Title = "和奶蛙聊天"; Width = 470; Height = 600; MinWidth = 360; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = false; Background = Ui.Canvas;
        var layout = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(18, 15, 18, 10) };
        var greeting = Ui.Text("奶蛙在听", 25); greeting.FontWeight = FontWeights.SemiBold; header.Children.Add(greeting);
        header.Children.Add(Ui.Text("有想法、有问题，或只是想说两句，都可以。", 12, Ui.Muted));
        var toolbar = Ui.Actions(header);
        toolbar.Children.Add(Ui.Button("模型设置", () => runtime.Pet?.ShowSettings()));
        toolbar.Children.Add(Ui.Button("工程工作台", () => runtime.Pet?.ShowAgent()));
        toolbar.Children.Add(Ui.Button("预览截图", runtime.PreviewScreen));
        toolbar.Children.Add(Ui.Button("移除附图", () => { image = null; attachment.Text = ""; }));
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var composer = new StackPanel { Margin = new Thickness(18, 8, 18, 15) };
        composer.Children.Add(attachment); composer.Children.Add(input);
        composer.Children.Add(Ui.Text("Enter 发送 · Shift+Enter 换行", 11, Ui.Muted));
        var actions = Ui.Actions(composer);
        send = Ui.Button("发送", () => Send(false), true); cancel = Ui.Button("取消", () => request?.Cancel()); retry = Ui.Button("重试上一条", () => Send(true));
        actions.Children.Add(send); actions.Children.Add(cancel); actions.Children.Add(retry);
        composer.Children.Add(status); DockPanel.SetDock(composer, Dock.Bottom); layout.Children.Add(composer);
        scroll = new ScrollViewer { Content = conversation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; layout.Children.Add(scroll);
        Content = layout;
        AutomationProperties.SetName(input, "聊天输入");
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(false); }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        runtime.StateChanged += RefreshStatus;
        Closed += (_, _) => { closed = true; request?.Cancel(); runtime.StateChanged -= RefreshStatus; image = lastImage = null; };
        Loaded += (_, _) => input.Focus();
        ReloadHistory(); RefreshStatus();
    }
    public void AttachImage(byte[] bytes)
    { image = bytes; attachment.Text = "已附截图 · 点击发送时上传；可用「移除附图」撤回。"; }
    public void ReloadHistory()
    {
        conversation.Children.Clear();
        foreach (var message in runtime.Chat.History) AddMessage(message.Role == "user" ? "你" : "奶蛙", message.Content, message.Role == "user");
        if (conversation.Children.Count == 0) conversation.Children.Add(Ui.Text(runtime.Chat.IsConfigured ? "模型已就绪。今天想从哪件事聊起？" : "先在模型设置里填好连接，再和奶蛙聊第一句吧。", 13, Ui.Muted));
    }
    private TextBlock AddMessage(string role, string text, bool user)
    {
        var content = new StackPanel { Margin = new Thickness(13, 10, 13, 12) };
        content.Children.Add(Ui.Text(role, 11, Ui.Muted));
        var body = Ui.Text(text, 14); content.Children.Add(body);
        conversation.Children.Add(new Border { Child = content, Background = user ? Ui.Brush("#FFF8E3") : Brushes.White, BorderBrush = user ? Ui.Brush("#D6B852") : Ui.Brush("#81A28A"), BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(0, 10, 10, 0), Margin = new Thickness(user ? 22 : 0, 4, user ? 0 : 22, 10) });
        scroll?.ScrollToEnd(); return body;
    }
    private void RefreshStatus()
    {
        if (closed) return;
        send.IsEnabled = request is null && !runtime.IsBusy;
        cancel.IsEnabled = request is not null;
        retry.IsEnabled = request is null && !runtime.IsBusy && lastText.Length > 0;
        if (request is null) status.Text = runtime.Chat.IsConfigured ? runtime.Status : "尚未配置模型，请打开「模型设置」。";
    }
    private async void Send(bool repeat)
    {
        if (request is not null || runtime.IsBusy) return;
        string text = repeat ? lastText : input.Text.Trim();
        byte[]? attached = repeat ? lastImage : image;
        if (text.Length == 0) { status.Text = "请先输入一句话。"; return; }
        if (!runtime.Chat.IsConfigured) { status.Text = "请先在设置中填写 API 地址、模型名称和所需的 API Key。"; return; }
        if (!repeat) { lastText = text; lastImage = attached; input.Clear(); image = null; attachment.Text = ""; }
        if (conversation.Children.Count == 1 && conversation.Children[0] is TextBlock) conversation.Children.Clear();
        AddMessage("你", text + (attached is null ? "" : "\n[附截图]"), true);
        var body = AddMessage("奶蛙", "奶蛙正在想……", false);
        var current = new CancellationTokenSource(); request = current; RefreshStatus();
        bool hasText = false; status.Text = "正在回复……";
        try
        {
            var result = await runtime.SendAsync(text, accumulated =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (closed || request != current) return;
                    hasText = true; body.Text = accumulated;
                    if (!scroll.IsMouseOver) scroll.ScrollToEnd();
                });
            }, current.Token, attached);
            if (!closed) { body.Text = result.Text; status.Text = "回复完成"; }
        }
        catch (OperationCanceledException) { if (!closed) { if (!hasText) body.Text = "本次回复已取消。"; status.Text = "已取消；不完整回复不会写入记忆。"; } }
        catch (Exception e) { if (!closed) { if (!hasText) body.Text = e.Message; else body.Text += "\n\n" + e.Message; status.Text = "请求未完成，可修改配置或重试。"; } }
        finally { if (request == current) request = null; current.Dispose(); if (!closed) { RefreshStatus(); input.Focus(); } }
    }
}
