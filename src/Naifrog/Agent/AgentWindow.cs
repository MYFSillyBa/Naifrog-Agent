using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Naifrog.Agent;
using Naifrog.Chat;
using Naifrog.Core;
using Naifrog.Desktop;

namespace Naifrog;

internal sealed class AgentWindow : Window
{
    private readonly PetRuntime runtime;
    private readonly ListBox tasks = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly List<AgentSession> sessions = new();
    private readonly Dictionary<AgentSession, ListBoxItem> rows = new();
    private readonly Dictionary<AgentSession, (bool Edits, int Commands)> grantsByTask = new();
    private readonly Dictionary<AgentSession, string> titles = new(), drafts = new();
    private readonly TextBox workspace = new() { MinHeight = 33, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox prompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 66, MaxHeight = 180, FontSize = 14, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox edits = new() { Content = "允许修改工程文件", IsChecked = true };
    private readonly ComboBox commands = new() { MinWidth = 160, MinHeight = 30 };
    private readonly AgentEventView conversation = new();
    private readonly FileChangesView changePane = new();
    private readonly TextBlock state = AgentUi.Text("就绪", 12, AgentUi.Muted);
    private readonly ComboBox model = new() { IsEditable = true, IsTextSearchEnabled = true, MinWidth = 155, MaxWidth = 260, MinHeight = 28, MaxDropDownHeight = 350 };
    private readonly ModelCatalogService catalog = new();
    private readonly Dictionary<AgentSession, string> runModels = new();
    private readonly Button send, stop, browse, review, refreshModels;
    private readonly Grid center = new();
    private readonly ColumnDefinition diffColumn = new() { Width = new GridLength(0) };
    private readonly CancellationTokenSource lifetime = new();
    private AgentSession? selected;
    private bool closed, refreshPending;
    private bool updatingModel, loadingModels;
    private string modelScope = "";
    private int modelGeneration;
    private CancellationTokenSource? modelLookup;

    public AgentWindow(PetRuntime runtime)
    {
        this.runtime = runtime;
        Title = "奶蛙 · 工程工作台"; Width = 1220; Height = 820; MinWidth = 820; MinHeight = 600;
        Height = Math.Min(Height, Math.Max(600, SystemParameters.WorkArea.Height - 30));
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = false; ShowInTaskbar = true; AgentUi.Install(this);
        ScrollViewer.SetHorizontalScrollBarVisibility(tasks, ScrollBarVisibility.Disabled);
        tasks.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(218) }); root.ColumnDefinitions.Add(new ColumnDefinition()); Content = root;
        var sidebar = new DockPanel { Margin = new Thickness(14, 18, 14, 14) };
        root.Children.Add(new Border { Background = AgentUi.Surface, BorderBrush = AgentUi.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar });
        var brand = new DockPanel { Margin = new Thickness(6, 0, 0, 22) };
        var mascot = PetWindow.CreateCharacter(); mascot.Width = 30; mascot.Height = 38; AutomationProperties.SetName(mascot, "奶蛙工作台图标");
        brand.Children.Add(new Border { Width = 34, Height = 38, Margin = new Thickness(0, -6, 9, -6), Child = mascot });
        var brandName = AgentUi.Text("奶蛙  /  工作台", 14); brandName.VerticalAlignment = VerticalAlignment.Center; brand.Children.Add(brandName); DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var fresh = AgentUi.Button("＋  新任务", NewTask); fresh.Margin = new Thickness(0, 0, 0, 22); DockPanel.SetDock(fresh, Dock.Top); sidebar.Children.Add(fresh);
        var caption = AgentUi.Text("任务", 11, AgentUi.Muted); caption.Margin = new Thickness(8, 0, 0, 8); DockPanel.SetDock(caption, Dock.Top); sidebar.Children.Add(caption);
        var bottom = new StackPanel(); var history = AgentUi.Button("历史记录", OpenHistory); history.Margin = new Thickness(0, 12, 0, 8); bottom.Children.Add(history); bottom.Children.Add(AgentUi.Button("模型与 Agent 设置", () => runtime.Pet?.ShowSettings())); DockPanel.SetDock(bottom, Dock.Bottom); sidebar.Children.Add(bottom); sidebar.Children.Add(tasks);
        var main = new DockPanel(); Grid.SetColumn(main, 1); root.Children.Add(main);
        var header = new Grid { Margin = new Thickness(24, 15, 24, 14) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var location = new DockPanel(); browse = AgentUi.Button("选择目录", Browse); browse.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); location.Children.Add(browse); location.Children.Add(workspace); header.Children.Add(location);
        review = AgentUi.Button("文件改动", ToggleChanges); review.Margin = new Thickness(18, 0, 0, 0); Grid.SetColumn(review, 1); header.Children.Add(review);
        var headerLine = new Border { Child = header, BorderBrush = AgentUi.Line, BorderThickness = new Thickness(0, 0, 0, 1) }; DockPanel.SetDock(headerLine, Dock.Top); main.Children.Add(headerLine);
        var composeArea = new StackPanel { Margin = new Thickness(28, 0, 28, 15), MaxWidth = 940, HorizontalAlignment = HorizontalAlignment.Stretch };
        var statusLine = new DockPanel { Margin = new Thickness(2, 8, 2, 9) };
        var modelArea = new StackPanel { Orientation = Orientation.Horizontal }; modelArea.Children.Add(model);
        refreshModels = AgentUi.Button("↻", () => LoadModels()); refreshModels.ToolTip = "从 API 刷新全部模型"; refreshModels.Margin = new Thickness(5, 0, 0, 0); modelArea.Children.Add(refreshModels); DockPanel.SetDock(modelArea, Dock.Right); statusLine.Children.Add(modelArea); statusLine.Children.Add(state); composeArea.Children.Add(statusLine);
        AutomationProperties.SetName(model, "工作台模型"); AutomationProperties.SetName(refreshModels, "刷新模型列表");
        model.DropDownOpened += (_, _) => { if (model.Items.Count == 0 && !loadingModels) LoadModels(); };
        model.SelectionChanged += (_, _) => { if (!updatingModel && model.SelectedItem is string name) SaveModel(name); };
        model.LostKeyboardFocus += (_, _) => { if (!updatingModel && !model.IsDropDownOpen) SaveModel(model.Text); };
        model.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SaveModel(model.Text); model.IsDropDownOpen = false; } };
        var composer = new StackPanel(); var entry = new Grid(); entry.Children.Add(prompt);
        var watermark = AgentUi.Text("描述任务，或继续补充要求…", 14, AgentUi.Muted); watermark.Margin = new Thickness(12, 11, 12, 0); watermark.IsHitTestVisible = false; watermark.VerticalAlignment = VerticalAlignment.Top; entry.Children.Add(watermark); prompt.TextChanged += (_, _) => watermark.Visibility = prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; composer.Children.Add(entry);
        var controls = new DockPanel { Margin = new Thickness(9, 0, 9, 8) };
        var permission = new Expander { Header = "权限", Foreground = AgentUi.Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        var grants = new StackPanel { Margin = new Thickness(4, 7, 8, 0) }; grants.Children.Add(edits); commands.Items.Add("每条命令询问"); commands.Items.Add("本任务自动执行（不限工作区）"); commands.Items.Add("禁止终端命令"); commands.SelectedIndex = 0; grants.Children.Add(commands);
        var scopeNote = AgentUi.Text("终端使用当前 Windows 用户权限。", 11, AgentUi.Muted); scopeNote.Margin = new Thickness(0, 5, 0, 3); grants.Children.Add(scopeNote); permission.Content = grants;
        var buttons = new WrapPanel(); send = AgentUi.Button("发送", Start, true); stop = AgentUi.Button("停止", () => selected?.Cancel()); stop.Margin = new Thickness(0, 0, 7, 0); buttons.Children.Add(stop); buttons.Children.Add(send); DockPanel.SetDock(buttons, Dock.Right); controls.Children.Add(buttons); controls.Children.Add(permission); composer.Children.Add(controls);
        composeArea.Children.Add(new Border { Background = Brushes.White, BorderBrush = AgentUi.Color("#D9DDE4"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Child = composer });
        var note = AgentUi.Text("Enter 发送 · Shift+Enter 换行   /   工程内容交给你配置的模型处理", 10, AgentUi.Muted); note.Margin = new Thickness(3, 7, 0, 0); composeArea.Children.Add(note); DockPanel.SetDock(composeArea, Dock.Bottom); main.Children.Add(composeArea);
        center.ColumnDefinitions.Add(new ColumnDefinition()); center.ColumnDefinitions.Add(diffColumn); center.Children.Add(conversation); main.Children.Add(center); Grid.SetColumn(changePane, 1); center.Children.Add(changePane); changePane.Visibility = Visibility.Collapsed;
        changePane.UndoRequested += Undo;
        changePane.SummaryChanged += (count, added, removed) => { var label = AgentUi.Text($"{count} 个文件  ", 12); label.Inlines.Add(new System.Windows.Documents.Run($"+{added}") { Foreground = FileChangesView.AddedColor }); label.Inlines.Add("  "); label.Inlines.Add(new System.Windows.Documents.Run($"−{removed}") { Foreground = FileChangesView.RemovedColor }); review.Content = count > 0 ? label : "文件改动"; };
        AutomationProperties.SetName(workspace, "工作区目录"); AutomationProperties.SetName(prompt, "工程任务输入"); AutomationProperties.SetName(commands, "终端命令策略"); AutomationProperties.SetName(conversation, "Agent 对话");
        tasks.SelectionChanged += (_, _) => SelectTask((tasks.SelectedItem as ListBoxItem)?.Tag as AgentSession);
        prompt.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; if (send.IsEnabled) Start(); } };
        Activated += (_, _) => { _ = runtime.Store.Snapshot(); Refresh(); }; Closed += (_, _) => { closed = true; modelLookup?.Cancel(); modelLookup?.Dispose(); catalog.Dispose(); changePane.Dispose(); lifetime.Cancel(); foreach (var session in sessions) { session.Changed -= SessionChanged; session.Dispose(); } lifetime.Dispose(); }; Refresh();
    }
    private void ToggleChanges() { bool show = changePane.Visibility != Visibility.Visible; changePane.Visibility = show ? Visibility.Visible : Visibility.Collapsed; diffColumn.Width = new GridLength(show ? Math.Clamp(ActualWidth * .46, 340, 650) : 0); }
    private void Browse() { var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择工程目录", Multiselect = false }; if (dialog.ShowDialog(this) == true) workspace.Text = dialog.FolderName; }
    private void NewTask()
    {
        if (selected is not null) drafts[selected] = prompt.Text;
        tasks.SelectedItem = null; selected = null; edits.IsChecked = true; commands.SelectedIndex = 0; prompt.Clear(); Refresh(); prompt.Focus();
    }
    private void SelectTask(AgentSession? session)
    {
        if (selected is not null) drafts[selected] = prompt.Text; selected = session;
        if (session is not null) { workspace.Text = session.Workspace; var grants = grantsByTask[session]; edits.IsChecked = grants.Edits; commands.SelectedIndex = grants.Commands; }
        prompt.Text = session is not null && drafts.TryGetValue(session, out var draft) ? draft : ""; Refresh();
    }
    private void SessionChanged()
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        lock (rows) { if (refreshPending) return; refreshPending = true; }
        Dispatcher.BeginInvoke(() => { lock (rows) refreshPending = false; Refresh(); });
    }
    private void Refresh()
    {
        if (closed) return;
        foreach (var entry in rows)
        {
            var label = new StackPanel { Margin = new Thickness(3, 6, 3, 6) }; var title = AgentUi.Text(titles[entry.Key], 12); title.MaxHeight = 40; title.TextTrimming = TextTrimming.CharacterEllipsis; label.Children.Add(title); var detail = AgentUi.Text(Path.GetFileName(entry.Key.Workspace) + " · " + entry.Key.Status, 10, AgentUi.Muted); detail.Margin = new Thickness(0, 4, 0, 0); label.Children.Add(detail); entry.Value.Content = label;
        }
        bool existing = selected is not null, running = selected?.IsRunning == true;
        workspace.IsReadOnly = existing; browse.IsEnabled = edits.IsEnabled = commands.IsEnabled = !existing; send.IsEnabled = selected?.Status != "撤销中"; send.Content = running ? "补充引用" : "发送"; stop.Visibility = running ? Visibility.Visible : Visibility.Collapsed; stop.IsEnabled = running;
        model.IsEnabled = refreshModels.IsEnabled = !running && !loadingModels;
        string scope = CatalogScope(runtime.Store.Current); if (scope != modelScope) { modelScope = scope; modelGeneration++; modelLookup?.Cancel(); loadingModels = false; model.IsEnabled = refreshModels.IsEnabled = !running; updatingModel = true; model.ItemsSource = null; updatingModel = false; }
        if (!model.IsKeyboardFocusWithin && !model.IsDropDownOpen) { updatingModel = true; model.Text = running && runModels.TryGetValue(selected!, out var used) ? used : runtime.Store.Current.Model.ModelId; updatingModel = false; }
        state.Text = selected?.Status ?? "选择工程，开始一个新任务"; if (selected?.PendingReferenceCount > 0) state.Text += $" · {selected.PendingReferenceCount} 条补充待引用";
        conversation.Render(selected?.Id ?? "new", selected?.Events ?? Array.Empty<AgentEvent>());
        var changed = selected?.Tools.Changes ?? Array.Empty<AgentFileChange>(); changePane.CanUndo = existing && !running && changed.Count > 0; changePane.SetChanges(changed);
    }
    private async void Start()
    {
        string text = prompt.Text.Trim(); if (text.Length == 0) { state.Text = "先描述要完成的任务。"; return; } if (text.Length > 32000) { state.Text = "任务输入请控制在 32000 字符以内。"; return; }
        try
        {
            if (selected?.IsRunning == true)
            {
                if (selected.TryQueueReference(text)) { prompt.Clear(); drafts[selected] = ""; Refresh(); }
                else state.Text = "暂未提交：任务正在结束或引用队列已满。输入已保留，可稍后发送。";
                return;
            }
            if (!SaveModel(model.Text)) return;
            var connection = runtime.Store.Snapshot(); if (string.IsNullOrWhiteSpace(connection.Settings.Model.BaseUrl) || string.IsNullOrWhiteSpace(connection.Settings.Model.ModelId)) { state.Text = "请先在模型设置中配置支持工具调用的模型。"; return; }
            if (selected is null)
            {
                int policy = commands.SelectedIndex; var session = new AgentSession(runtime.Store, workspace.Text.Trim(), edits.IsChecked == true, (command, cwd, token) => ApproveCommand(policy, command, cwd, token));
                grantsByTask.Add(session, (edits.IsChecked == true, policy)); string first = text.Split('\n')[0]; titles[session] = first.Length > 45 ? first[..45] + "…" : first; sessions.Add(session); session.Changed += SessionChanged;
                var item = new ListBoxItem { Tag = session, Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch }; rows.Add(session, item); tasks.Items.Add(item); tasks.SelectedItem = item;
            }
            var active = selected!; runModels[active] = connection.Settings.Model.ModelId; var work = active.RunAsync(text, lifetime.Token); prompt.Clear(); drafts[active] = ""; Refresh(); await work; if (!closed) { Refresh(); runtime.Notify("工程任务：" + active.Status, false); }
        }
        catch (OperationCanceledException) { if (!closed) { Refresh(); state.Text = "任务已停止，已执行的改动保留。"; } }
        catch (Exception e) { if (!closed) { Refresh(); state.Text = e.Message; } }
    }
    private static string CatalogScope(AppSettings settings) => settings.Model.BaseUrl.Trim() + "\n" + settings.CredentialId;
    private bool SaveModel(string? name)
    {
        if (updatingModel || closed) return false;
        string id = (name ?? "").Trim(); if (selected?.IsRunning == true) return false;
        try
        {
            var snapshot = runtime.Store.Snapshot(); if (id == snapshot.Settings.Model.ModelId) return true;
            if (id.Length == 0) { state.Text = "请选择模型或手动输入模型 ID。"; return false; }
            snapshot.Settings.Model.ModelId = id; runtime.Store.Save(snapshot.Settings); return true;
        }
        catch (Exception e) { state.Text = e.Message; return false; }
    }
    private async void LoadModels()
    {
        if (closed || selected?.IsRunning == true) return;
        modelLookup?.Cancel(); modelLookup?.Dispose(); modelLookup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int version = ++modelGeneration; var token = modelLookup.Token; var snapshot = runtime.Store.Snapshot(); string scope = CatalogScope(snapshot.Settings);
        modelScope = scope; loadingModels = true; refreshModels.IsEnabled = false; state.Text = "正在获取 API 模型列表…";
        try
        {
            var ids = await catalog.ListAsync(snapshot.Settings, snapshot.ApiKey, token);
            if (closed || version != modelGeneration || scope != CatalogScope(runtime.Store.Snapshot().Settings)) return;
            string typed = model.Text; updatingModel = true; model.ItemsSource = ids; model.Text = typed; updatingModel = false;
            state.Text = ids.Count > 0 ? $"已获取 {ids.Count} 个模型，可选择或输入 ID。" : "服务未返回模型，可手动输入 ID。";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!closed && version == modelGeneration) state.Text = e.Message; }
        finally { if (!closed && version == modelGeneration) { loadingModels = false; refreshModels.IsEnabled = model.IsEnabled = selected?.IsRunning != true; } }
    }
    private Task<bool> ApproveCommand(int policy, string command, string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (policy == 1) return Task.FromResult(true); if (policy == 2) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            if (closed || token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            var dialog = new Window { Title = "允许此命令？", Width = 660, Height = 410, MinWidth = 440, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = false }; AgentUi.Install(dialog); var panel = new DockPanel { Margin = new Thickness(22) };
            var info = AgentUi.Text("确认执行命令\n\n工作目录：" + cwd + "\n终端使用当前 Windows 用户权限。", 13); info.Margin = new Thickness(0, 0, 0, 15); DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); buttons.Children.Add(AgentUi.Button("允许执行", () => { completion.TrySetResult(true); dialog.Close(); }, true)); var deny = AgentUi.Button("拒绝", () => { completion.TrySetResult(false); dialog.Close(); }); deny.Margin = new Thickness(9, 0, 0, 0); buttons.Children.Add(deny); panel.Children.Add(buttons); panel.Children.Add(AgentUi.Code(command)); dialog.Content = panel;
            var registration = token.Register(() => { completion.TrySetCanceled(token); if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(dialog.Close); }); dialog.Closed += (_, _) => { registration.Dispose(); completion.TrySetResult(false); }; dialog.Show();
        }); return completion.Task;
    }
    private async void Undo()
    {
        var session = selected; if (session is null || session.IsRunning) return; if (MessageBox.Show(this, "撤销文件工具所做的修改？\n终端改动不在此范围；有后续修改的文件会保留并报告冲突。", "撤销文件改动", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { var task = session.UndoAsync(lifetime.Token); Refresh(); string result = await task; if (!closed) { Refresh(); state.Text = result; } } catch (Exception e) { if (!closed) { Refresh(); state.Text = e.Message; } }
    }
    private void OpenHistory()
    {
        try
        {
            string directory = Path.Combine(runtime.Store.DataDirectory, "AgentTasks"); Directory.CreateDirectory(directory); var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择任务记录", InitialDirectory = directory, Filter = "加密任务记录|*.dpapi" }; if (picker.ShowDialog(this) != true) return;
            if (new FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new InvalidOperationException("记录文件过大。"); using var document = JsonDocument.Parse(LocalVault.Unprotect(File.ReadAllBytes(picker.FileName)));
            var history = new Window { Title = "任务历史 · 只读", Owner = this, Width = 920, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner }; AgentUi.Install(history);
            if (document.RootElement.TryGetProperty("Events", out var events)) { var view = new AgentEventView(); view.Render("history", events.Deserialize<AgentEvent[]>() ?? Array.Empty<AgentEvent>()); history.Content = view; } else history.Content = AgentUi.Code(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }), double.PositiveInfinity); history.Show();
        }
        catch (Exception e) { state.Text = "无法打开记录：" + e.Message; }
    }
}
