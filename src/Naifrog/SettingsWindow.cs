using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using Naifrog.Animation;
using Naifrog.Chat;
using Naifrog.Core;
using Naifrog.Desktop;
using Naifrog.UI;

namespace Naifrog;

internal sealed class SettingsWindow : Window
{
    private readonly PetRuntime runtime;
    private AppSettings draft;
    private AppSettings baseline;
    private string baselineKey;
    private PasswordBox apiKey = new() { Padding = new Thickness(9, 7, 9, 7) };
    private readonly TextBlock feedback = Ui.Text("配置修改后，点击「保存并应用」。", 12, Ui.Muted);
    private TextBlock diagnostics = Ui.Text("", 12, Ui.Muted);
    private TextBlock overviewStatus = Ui.Text("", 13, Ui.Muted);
    private readonly ContentControl pageHost = new();
    private readonly ListBox navigation = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(18, 3, 18, 4) };
    private readonly List<Func<string?>> fieldChecks = new();
    private readonly List<StackPanel> pages = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool closed;
    private long modelCatalogGeneration;
    private CancellationTokenSource? modelCatalogCancellation;

    public SettingsWindow(PetRuntime runtime)
    {
        this.runtime = runtime;
        var snapshot = runtime.Store.Snapshot(); draft = snapshot.Settings; baseline = draft.Clone(); baselineKey = snapshot.ApiKey; apiKey.Password = baselineKey;
        Title = "奶蛙设置"; Width = 940; Height = 750; MinWidth = 720; MinHeight = 500;
        MaxHeight = Math.Max(500, SystemParameters.WorkArea.Height-30); Height = Math.Min(Height, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Ui.Canvas; Topmost = false;
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(30, 20, 30, 15) };
        var title = new StackPanel(); title.Children.Add(Ui.Text("奶蛙的小屋", 25)); title.Children.Add(Ui.Text("日常陪伴，由你设定", 12, Ui.Muted));
        var chat = Ui.Button("打开聊天", () => runtime.Pet?.ShowChat(), true); DockPanel.SetDock(chat, Dock.Right); header.Children.Add(chat);
        var avatar = PetWindow.CreateCharacter(); avatar.Width = 38; avatar.Height = 51;
        var badge = new Border { Child = avatar, Background = Ui.Brush("#F2E4AC"), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(badge, Dock.Left); header.Children.Add(badge); header.Children.Add(title); root.Children.Add(header);
        var wrapFactory = new FrameworkElementFactory(typeof(WrapPanel));
        navigation.ItemsPanel = new ItemsPanelTemplate(wrapFactory);
        navigation.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        navigation.MaxHeight = 105;
        navigation.ItemContainerStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>
 <Setter Property='Foreground' Value='#697768'/><Setter Property='Cursor' Value='Hand'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
  <Grid Margin='2,0,2,0'><Border x:Name='Tab' CornerRadius='7,7,0,0' Padding='{TemplateBinding Padding}' Background='Transparent'><ContentPresenter/></Border>
   <Border x:Name='Line' Height='3' VerticalAlignment='Bottom' Background='Transparent' Margin='9,0,9,0' CornerRadius='1'/></Grid>
  <ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='#213C32'/><Setter Property='FontWeight' Value='SemiBold'/><Setter TargetName='Tab' Property='Background' Value='#FFF9E6'/><Setter TargetName='Line' Property='Background' Value='#CDAA46'/></Trigger>
   <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Tab' Property='Background' Value='#F1EBD8'/></Trigger></ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>");
        var navBorder = new Border { Child = navigation, BorderBrush = Ui.Brush("#DEDCCB"), BorderThickness = new Thickness(0, 0, 0, 1) }; Grid.SetRow(navBorder, 1); root.Children.Add(navBorder);
        var scroll = new ScrollViewer { Content = pageHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(22, 10, 22, 12), LastChildFill = true };
        var save = Ui.Button("保存并应用", () => Save(), true); DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save);
        var close = Ui.Button("关闭", Close); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        footer.Children.Add(feedback); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        BuildPages();
        string[] labels = { "概览", "外观与动作", "模型与聊天", "情绪与性格", "陪伴与专注", "屏幕与隐私", "数据与高级", "工程 Agent" };
        foreach (string label in labels) navigation.Items.Add(new ListBoxItem { Content = label, Padding = new Thickness(12, 12, 12, 12) });
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedIndex >= 0) { pageHost.Content = pages[navigation.SelectedIndex]; scroll.ScrollToTop(); } };
        navigation.SelectedIndex = 0;
        statusTimer.Tick += (_, _) => RefreshStatus(); statusTimer.Start(); RefreshStatus();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); statusTimer.Stop(); };
    }
    private void BuildPages()
    {
        pages.Add(Overview()); pages.Add(Appearance()); pages.Add(Model()); pages.Add(Emotions()); pages.Add(Companion()); pages.Add(Privacy()); pages.Add(Data()); pages.Add(AgentPage());
    }
    private StackPanel Overview()
    {
        var page = Ui.Page("让日常多一点陪伴", "如何聊天、何时提醒，以及奶蛙在桌面上如何活动，都由你决定。");
        var card = Ui.Card(page, "现在的状态"); card.Children.Add(overviewStatus);
        var actions = Ui.Actions(card);
        actions.Children.Add(Ui.Button("和奶蛙聊天", () => runtime.Pet?.ShowChat(), true));
        actions.Children.Add(Ui.Button("召唤到鼠标旁", () => runtime.Pet?.Summon()));
        actions.Children.Add(Ui.Button("暂停 / 恢复自动行为", runtime.TogglePause));
        var tools = Ui.Card(page, "一起做点什么", "工具会直接打开；配置修改需先保存。");
        var buttons = Ui.Actions(tools);
        buttons.Children.Add(Ui.Button("工程工作台", () => runtime.Pet?.ShowAgent()));
        buttons.Children.Add(Ui.Button("开始 / 结束专注", runtime.ToggleFocus));
        buttons.Children.Add(Ui.Button("陪我一会儿", async () => await runtime.ProactiveNowAsync()));
        buttons.Children.Add(Ui.Button("预览屏幕", runtime.PreviewScreen));
        var guide = Ui.Card(page, "开始使用");
        guide.Children.Add(Ui.Text("1. 在「模型与聊天」填写 API 地址、模型名称、Key 和提示词。\n2. 保存后测试连接，单击奶蛙即可打开聊天。\n3. 在「外观与动作」选择立绘或动作包，拖动奶蛙调整位置。\n4. 主动陪伴与屏幕功能可分别启用，屏幕授权只在本次运行有效。", 13, Ui.Muted));
        return page;
    }
    private StackPanel Appearance()
    {
        var page = Ui.Page("外观与动作", "选择显示方式、大小和位置；动作预览使用已保存的配置。");
        var card = Ui.Card(page, "桌面显示");
        Number(card, "角色高度（120–700）", draft.Appearance.Height, v => draft.Appearance.Height = v);
        Number(card, "不透明度（0.25–1）", draft.Appearance.Opacity, v => draft.Appearance.Opacity = v);
        Toggle(card, "桌宠始终置顶（不影响工具窗口）", draft.Appearance.Topmost, v => draft.Appearance.Topmost = v);
        Toggle(card, "允许拖动奶蛙", draft.Appearance.DragEnabled, v => draft.Appearance.DragEnabled = v);
        Toggle(card, "记住桌面位置", draft.Appearance.RememberPosition, v => draft.Appearance.RememberPosition = v);
        Ui.Actions(card).Children.Add(Ui.Button("重置到屏幕右下角", () => runtime.Pet?.ResetPosition()));
        var motion = Ui.Card(page, "表情与动作包", "动作包使用参考项目的透明序列与定格素材；关闭后显示原始正面立绘。微动作是二维图像变换。");
        Toggle(motion, "使用参考动作包", draft.Appearance.UseAnimationPack, v => draft.Appearance.UseAnimationPack = v);
        Toggle(motion, "启用呼吸、摇摆等微动作", draft.Emotion.MicroMotion, v => draft.Emotion.MicroMotion = v);
        Number(motion, "微动作幅度（0.1–1）", draft.Emotion.Strength, v => draft.Emotion.Strength = v);
        var choices = new ComboBox { ItemsSource = EmotionCatalog.Entries, DisplayMemberPath = "Label", SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 4), MinHeight = 32 };
        AutomationProperties.SetName(choices, "预览动作"); motion.Children.Add(choices);
        Ui.Actions(motion).Children.Add(Ui.Button("播放所选动作", () => { if (choices.SelectedItem is ExpressionDefinition entry) runtime.Emote(entry.Name); }));
        return page;
    }
    private StackPanel Model()
    {
        modelCatalogGeneration++;
        modelCatalogCancellation?.Cancel();
        var page = Ui.Page("连接你的模型", "使用你自己的兼容模型服务。连接测试会向所填服务发一条简短请求。");
        var card = Ui.Card(page, "模型连接", "API 地址通常是服务商提供的 Base URL，例如 https://example.com/v1。本机服务可以使用 HTTP。");
        var models = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true, Text = draft.Model.ModelId, MinHeight = 34, Margin = new Thickness(0, 0, 0, 10) };
        const string modelLabel = "模型名称 / Model ID";
        AutomationProperties.SetName(models, modelLabel);
        models.Loaded += (_, _) =>
        {
            if (models.Template.FindName("PART_EditableTextBox", models) is TextBox input) AutomationProperties.SetName(input, modelLabel);
        };
        bool updatingModels = false;
        models.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!updatingModels) draft.Model.ModelId = models.Text;
        }));
        var modelStatus = Ui.Text("可直接输入模型 ID，或从当前 API 获取全部模型后选择。", 12, Ui.Muted);
        void InvalidateCatalog()
        {
            modelCatalogGeneration++;
            modelCatalogCancellation?.Cancel();
            string value = models.Text;
            updatingModels = true;
            try { models.ItemsSource = null; models.Text = value; }
            finally { updatingModels = false; }
            modelStatus.Text = "服务配置已更改，可重新获取模型；也可以手动输入。";
        }
        TextField(card, "API 地址", draft.Model.BaseUrl, v => { draft.Model.BaseUrl = v; InvalidateCatalog(); });
        card.Children.Add(Ui.Text(modelLabel, 12, Ui.Muted)); card.Children.Add(models);
        card.Children.Add(Ui.Text("API Key（仅在本机加密保存）", 12, Ui.Muted)); AutomationProperties.SetName(apiKey, "API Key"); card.Children.Add(apiKey);
        var keyInput = apiKey;
        keyInput.PasswordChanged += (_, _) => InvalidateCatalog();
        var actions = Ui.Actions(card);
        actions.Children.Add(Ui.Button("获取模型", async () =>
        {
            long generation = ++modelCatalogGeneration;
            modelCatalogCancellation?.Cancel();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            modelCatalogCancellation = cancellation;
            AppSettings snapshot = draft.Clone();
            string key = keyInput.Password;
            bool IsCurrent() => !closed && generation == modelCatalogGeneration &&
                ReferenceEquals(apiKey, keyInput) && snapshot.Model.BaseUrl == draft.Model.BaseUrl && keyInput.Password == key;
            modelStatus.Text = "正在获取模型列表……";
            try
            {
                using var catalog = new ModelCatalogService();
                var result = await catalog.ListAsync(snapshot, key, cancellation.Token);
                if (!IsCurrent()) return;
                string selected = models.Text;
                updatingModels = true;
                try { models.ItemsSource = result; models.SelectedIndex = -1; models.Text = selected; }
                finally { updatingModels = false; }
                modelStatus.Text = result.Count == 0 ? "服务返回了空模型列表，可继续手动填写模型 ID。" : $"已获取 {result.Count} 个模型。可输入搜索或手动填写；保存后应用。";
            }
            catch (OperationCanceledException) { if (IsCurrent()) modelStatus.Text = "模型获取已取消，可继续手动填写。"; }
            catch (Exception e) { if (IsCurrent()) modelStatus.Text = e.Message; }
            finally { if (ReferenceEquals(modelCatalogCancellation, cancellation)) modelCatalogCancellation = null; }
        }));
        actions.Children.Add(Ui.Button("保存并测试连接", TestConnection, true));
        card.Children.Add(modelStatus);
        var prompt = Ui.Card(page, "系统提示词", "定义奶蛙的身份、语气和回答要求。这里的文本会作为正式聊天的系统提示词发送给模型。");
        TextField(prompt, "聊天系统提示词", draft.Model.SystemPrompt, v => draft.Model.SystemPrompt = v, true);
        var options = Ui.Card(page, "回答方式");
        Toggle(options, "流式显示回复", draft.Model.Streaming, v => draft.Model.Streaming = v);
        Integer(options, "回复 token 上限（128–8192）", draft.Model.MaxTokens, v => draft.Model.MaxTokens = v);
        Integer(options, "请求超时秒数（5–180）", draft.Model.TimeoutSeconds, v => draft.Model.TimeoutSeconds = v);
        Integer(options, "携带最近对话轮数（0–30）", draft.Model.ContextPairs, v => draft.Model.ContextPairs = v);
        Ui.Actions(options).Children.Add(Ui.Button("打开聊天窗口", () => runtime.Pet?.ShowChat()));
        return page;
    }
    private StackPanel Emotions()
    {
        var page = Ui.Page("情绪与性格", "聊天内容和表情判断分开处理，表情分析失败时仍会继续生成正文。");
        var card = Ui.Card(page, "情绪表现");
        Toggle(card, "启用情绪与表情", draft.Emotion.Enabled, v => draft.Emotion.Enabled = v);
        Toggle(card, "使用模型独立分析情绪（每次聊天增加一次请求）", draft.Emotion.AnalyzeWithModel, v => draft.Emotion.AnalyzeWithModel = v);
        Number(card, "最低置信度（0–1）", draft.Emotion.MinimumConfidence, v => draft.Emotion.MinimumConfidence = v);
        Integer(card, "相同动作冷却秒数（0–60）", draft.Emotion.CooldownSeconds, v => draft.Emotion.CooldownSeconds = v);
        var rules = Ui.Card(page, "情绪判断提示词", "补充奶蛙如何回应不同情境。可选表情及输出格式由程序补充。");
        TextField(rules, "情绪分析提示词", draft.Emotion.Instruction, v => draft.Emotion.Instruction = v, true);
        var persona = Ui.Card(page, "性格设定");
        persona.Children.Add(Ui.Text("性格由「模型与聊天」里的系统提示词决定。可以写温柔程度、幽默风格、回答长度和称呼习惯。", 13, Ui.Muted));
        Ui.Actions(persona).Children.Add(Ui.Button("编辑聊天提示词", () => navigation.SelectedIndex = 2));
        return page;
    }
    private StackPanel Companion()
    {
        var page = Ui.Page("陪伴与专注", "自动行为会避开输入、回复、阅读窗口和专注时段；也可以随时暂停。");
        var daily = Ui.Card(page, "日常行为");
        Toggle(daily, "启动时打招呼", draft.Companion.Greeting, v => draft.Companion.Greeting = v);
        Toggle(daily, "闲置和深夜的小动作", draft.Companion.IdleActions, v => draft.Companion.IdleActions = v);
        Toggle(daily, "空闲时在桌面小范围散步", draft.Companion.Wander, v => draft.Companion.Wander = v);
        Toggle(daily, "专注结束时播放提示音", draft.Companion.SoundEnabled, v => draft.Companion.SoundEnabled = v);
        var proactive = Ui.Card(page, "主动陪伴", "默认只使用本地提醒。启用模型陪伴后，自动发言会调用已配置的模型服务。");
        Toggle(proactive, "启用定时主动陪伴", draft.Companion.ProactiveEnabled, v => draft.Companion.ProactiveEnabled = v);
        Toggle(proactive, "使用模型生成陪伴内容", draft.Companion.ProactiveUseModel, v => draft.Companion.ProactiveUseModel = v);
        Integer(proactive, "最短间隔分钟（1–180）", draft.Companion.IntervalMinutes, v => draft.Companion.IntervalMinutes = v);
        Integer(proactive, "每小时最多次数（1–30）", draft.Companion.MaxPerHour, v => draft.Companion.MaxPerHour = v);
        Integer(proactive, "免打扰开始小时（0–23）", draft.Companion.QuietStart, v => draft.Companion.QuietStart = v);
        Integer(proactive, "免打扰结束小时（0–23）", draft.Companion.QuietEnd, v => draft.Companion.QuietEnd = v);
        TextField(proactive, "主动陪伴提示词", draft.Model.ProactivePrompt, v => draft.Model.ProactivePrompt = v, true);
        Ui.Actions(proactive).Children.Add(Ui.Button("立即陪我一会儿", async () => { if (Save()) await runtime.ProactiveNowAsync(); }));
        var focus = Ui.Card(page, "专注计时");
        Integer(focus, "专注分钟数（1–180）", draft.Companion.FocusMinutes, v => draft.Companion.FocusMinutes = v);
        Ui.Actions(focus).Children.Add(Ui.Button("开始 / 结束专注", () => { if (Save()) runtime.ToggleFocus(); }));
        return page;
    }
    private StackPanel Privacy()
    {
        var page = Ui.Page("屏幕与隐私", "模型只会收到你启用的附图。屏幕授权不会在程序重启后自动恢复。");
        var card = Ui.Card(page, "本次运行的屏幕授权", "启用后仍可只做本机预览。附图和定时观察会将所选屏幕画面发送给你的模型服务，模型需要支持图片。");
        var screenToggle = Toggle(card, "允许本次运行捕获所选屏幕", draft.Privacy.ScreenEnabled, v => draft.Privacy.ScreenEnabled = v);
        string[] monitors = ScreenCapture.MonitorNames();
        Choice(card, "截图范围", monitors.Select((n,i) => (n,i.ToString(CultureInfo.InvariantCulture))).ToArray(), draft.Privacy.MonitorIndex.ToString(CultureInfo.InvariantCulture), v => draft.Privacy.MonitorIndex = int.Parse(v, CultureInfo.InvariantCulture));
        Toggle(card, "每次聊天自动附上所选屏幕", draft.Privacy.AttachOnMessage, v => draft.Privacy.AttachOnMessage = v);
        Toggle(card, "空闲时定时观察并回应", draft.Privacy.PeriodicObservation, v => draft.Privacy.PeriodicObservation = v);
        Integer(card, "定时观察间隔分钟（1–120）", draft.Privacy.PeriodicMinutes, v => draft.Privacy.PeriodicMinutes = v);
        TextField(card, "排除应用进程名（逗号分隔）", draft.Privacy.ExcludedApps, v => draft.Privacy.ExcludedApps = v);
        card.Children.Add(Ui.Text("例如 passwordmanager, banking。所选屏幕存在这些可见应用时，整次截图会跳过。", 12, Ui.Muted));
        var actions = Ui.Actions(card); actions.Children.Add(Ui.Button("保存并预览截图", () => { if (Save()) runtime.PreviewScreen(); }));
        actions.Children.Add(Ui.Button("立即撤销屏幕授权", () => RunAction(() => { screenToggle.IsChecked = false; var current = runtime.Store.Current.Clone(); current.Privacy.ScreenEnabled = false; runtime.Store.Save(current); SetFeedback("屏幕授权已撤销；重新勾选并保存才会启用。"); })));
        return page;
    }
    private StackPanel AgentPage()
    {
        var page = Ui.Page("工程 Agent", "选择本地工作区，让模型读取文件、修改代码、运行构建，并根据执行结果继续处理。");
        var start = Ui.Card(page, "连接与工作区", "复用「模型与聊天」中的 API 地址、Key 和模型；模型须支持 chat/completions 的 tools / tool_calls。任务不会混入陪伴聊天记忆。");
        Ui.Actions(start).Children.Add(Ui.Button("保存并打开工程工作台", () => { if (Save()) runtime.Pet?.ShowAgent(); }, true));
        start.Children.Add(Ui.Text("在工作台选择目录和权限。文件工具限于工作区；终端具有当前用户权限，可执行编译、测试与 Git。默认逐条确认命令。不同工程最多同时运行 3 个任务，重叠目录串行。", 12, Ui.Muted));
        var instruction = Ui.Card(page, "工程任务提示词", "独立于奶蛙聊天人设。基础工具协议与工作区边界由程序附加，不会因修改提示词而关闭。");
        TextField(instruction, "Agent 系统提示词", draft.Agent.SystemPrompt, value => draft.Agent.SystemPrompt = value, true);
        var limits = Ui.Card(page, "上下文与执行", "任务持续执行至完成或手动停止。接近模型窗口时自动整理旧内容，保留任务目标和最近执行结果；请求超时沿用模型设置。");
        Integer(limits, "模型上下文窗口 tokens（8192–2000000）", draft.Agent.ContextWindowTokens, v => draft.Agent.ContextWindowTokens = v);
        limits.Children.Add(Ui.Text("请按所选模型的实际规格填写；默认 128000 是本地估算预算，不代表 API Key 额度。服务明确提示上下文过长时，会整理后重试当前请求。", 12, Ui.Muted));
        Toggle(limits, "自动整理旧上下文", draft.Agent.AutoCompact, v => draft.Agent.AutoCompact = v);
        Integer(limits, "单次模型输出 token 上限（512–16384）", draft.Agent.MaxTokens, v => draft.Agent.MaxTokens = v);
        Integer(limits, "单条命令超时秒数（5–1800）", draft.Agent.CommandTimeoutSeconds, v => draft.Agent.CommandTimeoutSeconds = v);
        var records = Ui.Card(page, "记录与撤销", "执行记录和文件工具的前后快照在本机加密保存。文件改动页可查看前后内容并保守撤销；终端产生的改动由 Git 检查。继续任务仅限当前窗口，重启后历史记录可只读查看。");
        records.Children.Add(Ui.Text("工程源码和命令结果会作为上下文发送给你选择的模型服务。API Key 使用现有 Windows 用户加密保存；程序没有内置 Key。", 12, Ui.Muted));
        return page;
    }
    private StackPanel Data()
    {
        var page = Ui.Page("数据与高级", "配置保存在当前用户目录；敏感记忆与密钥使用 Windows 用户加密。");
        var memory = Ui.Card(page, "聊天记忆");
        Toggle(memory, "保留聊天记忆并用于后续对话", draft.Data.RememberChat, v => draft.Data.RememberChat = v);
        Integer(memory, "最多保存的问答轮数（1–500）", draft.Data.MaxMemoryPairs, v => draft.Data.MaxMemoryPairs = v);
        var actions = Ui.Actions(memory);
        actions.Children.Add(Ui.Button("导出聊天文本", ExportMemory));
        actions.Children.Add(Ui.Button("清空聊天记忆", () =>
        {
            if (System.Windows.MessageBox.Show(this, "清空本机保存的聊天记忆？", "清空记忆", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { RunAction(() => { runtime.ClearMemory(); SetFeedback("聊天记忆已清空。"); }); }
        }));
        var system = Ui.Card(page, "桌面集成");
        Toggle(system, "开机启动", draft.Data.AutoStart, v => draft.Data.AutoStart = v);
        Toggle(system, "启用全局快捷键（Ctrl+Alt+T / H / P）", draft.Data.GlobalHotkeys, v => draft.Data.GlobalHotkeys = v);
        Toggle(system, "允许多个奶蛙实例（下次启动生效）", draft.Data.AllowMultipleInstances, v => draft.Data.AllowMultipleInstances = v);
        var buttons = Ui.Actions(system); buttons.Children.Add(Ui.Button("创建桌面快捷方式", () => RunAction(() => { NativeDesktop.CreateShortcut(); SetFeedback("桌面快捷方式已创建。"); })));
        buttons.Children.Add(Ui.Button("打开数据目录", () => OpenFolder(runtime.Store.DataDirectory)));
        var info = Ui.Card(page, "运行诊断"); info.Children.Add(diagnostics);
        var reset = Ui.Card(page, "恢复默认", "恢复配置并清空聊天记忆及 API Key；已接收、整理的文件及撤销记录会保留。");
        Ui.Actions(reset).Children.Add(Ui.Button("恢复默认设置", () =>
        {
            if (System.Windows.MessageBox.Show(this, "恢复默认配置并清空 API Key 和聊天记忆？已接收和整理的文件不会删除。", "恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                RunAction(() => { runtime.ClearMemory(); runtime.Store.Reset(); Close(); runtime.Pet?.ShowSettings(); });
        }));
        return page;
    }

    private bool Save()
    {
        try
        {
            foreach (var check in fieldChecks) { string? error = check(); if (error is not null) throw new ArgumentException(error); }
            var latest = runtime.Store.Snapshot();
            var merged = MergeChanges(latest.Settings, baseline, draft);
            merged.Validate();
            var old = latest.Settings.Privacy; var next = merged.Privacy;
            bool targetChanged = merged.Model.BaseUrl != latest.Settings.Model.BaseUrl || merged.Model.ModelId != latest.Settings.Model.ModelId || apiKey.Password != baselineKey;
            bool expanded = next.ScreenEnabled && (!old.ScreenEnabled || targetChanged || next.MonitorIndex != old.MonitorIndex || next.AttachOnMessage && !old.AttachOnMessage || next.PeriodicObservation && !old.PeriodicObservation || next.ExcludedApps != old.ExcludedApps);
            if (expanded && System.Windows.MessageBox.Show(this, "允许奶蛙在本次运行捕获所选屏幕？\n\n启用自动附图或定时观察时，截图会发送给你配置的模型服务。重启后屏幕授权会关闭。", "屏幕授权", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            merged.Appearance.Left = latest.Settings.Appearance.Left; merged.Appearance.Top = latest.Settings.Appearance.Top;
            bool memoryChanged = merged.Data.RememberChat != latest.Settings.Data.RememberChat || merged.Data.MaxMemoryPairs != latest.Settings.Data.MaxMemoryPairs;
            runtime.Store.Save(merged, apiKey.Password == baselineKey ? null : apiKey.Password, authorizeScreenGrant: expanded);
            if (memoryChanged) runtime.Chat.ReloadMemory();
            var saved = runtime.Store.Snapshot(); draft = saved.Settings; baseline = draft.Clone(); baselineKey = saved.ApiKey;
            int selected = navigation.SelectedIndex;
            pageHost.Content = null; pages.Clear(); fieldChecks.Clear();
            apiKey = new PasswordBox { Padding = new Thickness(9, 7, 9, 7), Password = baselineKey };
            diagnostics = Ui.Text("", 12, Ui.Muted); overviewStatus = Ui.Text("", 13, Ui.Muted);
            BuildPages(); pageHost.Content = pages[Math.Max(0, selected)];
            SetFeedback("已保存并应用。"); RefreshStatus(); return true;
        }
        catch (Exception e) { SetFeedback(e.Message, true); return false; }
    }
    internal static AppSettings MergeChanges(AppSettings current, AppSettings baseline, AppSettings edited)
    {
        var currentNode = JsonSerializer.SerializeToNode(current)!.AsObject();
        Apply(currentNode, JsonSerializer.SerializeToNode(baseline)!.AsObject(), JsonSerializer.SerializeToNode(edited)!.AsObject());
        return currentNode.Deserialize<AppSettings>()!;
        static void Apply(JsonObject target, JsonObject before, JsonObject after)
        {
            foreach (var item in after)
            {
                if (item.Key == nameof(AppSettings.CredentialId) || JsonNode.DeepEquals(before[item.Key], item.Value)) continue;
                if (item.Value is JsonObject changed && before[item.Key] is JsonObject original && target[item.Key] is JsonObject destination) Apply(destination, original, changed);
                else target[item.Key] = item.Value?.DeepClone();
            }
        }
    }
    private async void TestConnection()
    {
        if (!Save()) return;
        SetFeedback("正在测试连接……");
        try { string result = await runtime.Chat.TestConnectionAsync(lifetime.Token); if (!closed) SetFeedback(result.StartsWith("连接成功", StringComparison.Ordinal) ? result : "连接成功：" + result); }
        catch (OperationCanceledException) { if (!closed) SetFeedback("测试已取消。"); }
        catch (Exception e) { if (!closed) SetFeedback(e.Message, true); }
    }
    private void ExportMemory()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出聊天记忆（明文）", Filter = "文本文件|*.txt", FileName = "奶蛙聊天记忆.txt" };
        if (dialog.ShowDialog(this) == true) RunAction(() => { runtime.Chat.ExportMemory(dialog.FileName); SetFeedback("聊天记忆已导出为明文，请妥善保存。"); });
    }
    private void RefreshStatus()
    {
        if (closed) return;
        diagnostics.Text = runtime.Diagnostic;
        string focus = runtime.FocusEnd is DateTime end ? $"\n专注剩余：{Math.Max(0,(int)(end-DateTime.Now).TotalMinutes)} 分钟" : "";
        string emotion = EmotionCatalog.Find(runtime.Animator?.CurrentEmotion ?? "neutral")?.Label ?? "待机";
        overviewStatus.Text = $"{runtime.Status}\n模型连接：{(runtime.Chat.IsConfigured ? "已配置" : "等待填写")}\n表情：{emotion}\n自动行为：{(runtime.Paused ? "已暂停" : "运行中")}" + focus;
    }
    private void SetFeedback(string text, bool error = false) { feedback.Text = text; feedback.Foreground = error ? Ui.Brush("#9E342E") : Ui.Muted; }
    private void RunAction(Action action) { try { action(); } catch (Exception e) { SetFeedback(e.Message, true); } }
    private void OpenFolder(string path) => RunAction(() => { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }); });
    private CheckBox Toggle(Panel parent, string label, bool value, Action<bool> assign)
    {
        var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
        AutomationProperties.SetName(check, label); check.Checked += (_, _) => assign(true); check.Unchecked += (_, _) => assign(false); parent.Children.Add(check); return check;
    }
    private TextBox TextField(Panel parent, string label, string value, Action<string> assign, bool multiline = false)
    {
        parent.Children.Add(Ui.Text(label, 12, Ui.Muted));
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 10), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 135 : 34, MaxHeight = multiline ? 220 : 60, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
        AutomationProperties.SetName(input, label); input.TextChanged += (_, _) => assign(input.Text); parent.Children.Add(input); return input;
    }
    private void Number(Panel parent, string label, double value, Action<double> assign)
    {
        var input = TextField(parent, label, value.ToString(CultureInfo.InvariantCulture), text => { if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) assign(number); });
        fieldChecks.Add(() => double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? null : label+"请填写有效数字。");
    }
    private void Integer(Panel parent, string label, int value, Action<int> assign)
    {
        var input = TextField(parent, label, value.ToString(CultureInfo.InvariantCulture), text => { if (int.TryParse(text, out int number)) assign(number); });
        fieldChecks.Add(() => int.TryParse(input.Text, out _) ? null : label+"请填写整数。");
    }
    private void Choice(Panel parent, string label, (string Label,string Value)[] values, string selected, Action<string> assign)
    {
        parent.Children.Add(Ui.Text(label, 12, Ui.Muted)); var combo = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var choice in values) combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(values, v => v.Value == selected));
        AutomationProperties.SetName(combo, label); combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem item) assign((string)item.Tag); };
        parent.Children.Add(combo);
    }
}
