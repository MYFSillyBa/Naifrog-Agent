using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Naifrog.Animation;
using Naifrog.Chat;
using Naifrog.Core;

namespace Naifrog.Desktop;

internal sealed class PetRuntime : IDisposable
{
    public SettingsStore Store { get; }
    public ChatService Chat { get; }
    public PetAnimator? Animator { get; private set; }
    public PetWindow? Pet { get; private set; }
    public bool IsBusy { get; private set; }
    public bool Paused { get; private set; }
    public string Status { get; private set; } = "准备就绪";
    public DateTime LastInteraction { get; private set; } = DateTime.Now;
    public DateTime? FocusEnd { get; private set; }
    public event Action? StateChanged;
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource screenGrant = new();
    private CancellationTokenSource automaticWork = new();
    private CancellationTokenSource memoryContext = new();
    private string screenSignature = "";
    private bool rememberHistory;
    private string memoryRevision = "initial";
    private DateTime lastProactive = DateTime.Now, lastScreen = DateTime.Now, lastWander = DateTime.Now, lastSleepy = DateTime.Now;
    private DateTime reminderHour = DateTime.Now;
    private int hourlyCount, localLine;
    private bool disposed;
    private readonly bool configureDesktop;
    private readonly Queue<string> proactiveHistory = new();

    public PetRuntime(SettingsStore store, HttpMessageHandler? handler = null, bool startTimers = true, bool configureDesktop = true)
    {
        this.configureDesktop = configureDesktop;
        Store = store; Chat = new ChatService(store, handler);
        rememberHistory = Store.Current.Data.RememberChat;
        memoryRevision = Chat.MemoryRevision;
        Store.Changed += ApplySettings;
        heartbeat.Tick += Tick; if (startTimers) heartbeat.Start();
    }
    public void Attach(PetWindow pet, System.Windows.Controls.Image character)
    {
        Pet = pet;
        Animator = new PetAnimator(character, () => Store.Current.Appearance, () => Store.Current.Emotion, Path.Combine(AppContext.BaseDirectory, "animations"));
        Animator.StateChanged += UpdateState;
        ApplySettings();
        if (Store.Current.Companion.Greeting) pet.Dispatcher.BeginInvoke(() => Emote("greet", 4000, false), DispatcherPriority.ContextIdle);
    }
    public void Touch() { LastInteraction = DateTime.Now; }
    public void Emote(string name, int durationMs = 6000, bool manual = true)
    { Animator?.SetEmotion(new EmotionIntent(name, 1, 1, durationMs, manual ? "manual" : "automatic"), manual); UpdateState(); }
    public void Notify(string text, bool showBubble = true)
    {
        if (disposed) return;
        Status = text; if (showBubble) Pet?.Speak(text); UpdateState();
    }
    private void UpdateState() => StateChanged?.Invoke();
    public string Diagnostic => $"运行状态：{Status}\n模型：{(Chat.IsConfigured ? "已配置" : "未配置")}\n当前表情：{Animator?.CurrentEmotion ?? "neutral"}\n动作：{Animator?.Status ?? "加载中"}\n请求：{(IsBusy ? "进行中" : "空闲")}\n自动行为：{(Paused ? "已暂停" : "运行中")}\n屏幕授权：{(Store.Current.Privacy.ScreenEnabled ? "本次运行已启用" : "关闭")}\n快捷键：{Pet?.HotkeyStatus ?? "未启用"}\n模型诊断：{Chat.Diagnostic}\n数据目录：{Store.DataDirectory}";

    private void ApplySettings()
    {
        if (disposed) return;
        SyncPrivacyScope(Store.Current);
        automaticWork.Cancel(); automaticWork.Dispose(); automaticWork = new();
        SyncMemoryPreference();
        Pet?.ApplySettings(); Animator?.ApplySettings();
        try { if (configureDesktop) NativeDesktop.ConfigureStartup(Store.Current.Data.AutoStart); }
        catch (Exception e) { Status = "开机启动设置未能应用：" + e.Message; }
        UpdateState();
    }
    private void SyncPrivacyScope(AppSettings settings)
    {
        string signature = JsonSerializer.Serialize(new { settings.Privacy, settings.Model.BaseUrl, settings.Model.ModelId, settings.CredentialId });
        if (signature != screenSignature)
        {
            screenSignature = signature;
            screenGrant.Cancel(); screenGrant.Dispose(); screenGrant = new();
        }
    }
    private void SyncMemoryPreference()
    {
        string revision = Chat.MemoryRevision;
        if (rememberHistory != Store.Current.Data.RememberChat || memoryRevision != revision)
        { rememberHistory = Store.Current.Data.RememberChat; memoryRevision = revision; ResetProactiveContext(); }
    }
    public async Task<ChatResult> SendAsync(string text, Action<string>? delta, CancellationToken token, byte[]? attachedImage = null, bool userInitiated = true)
    {
        if (IsBusy) throw new InvalidOperationException("奶蛙正在回复，请先取消当前请求或稍等。");
        if (!Chat.IsConfigured) throw new InvalidOperationException("请先打开设置，在「模型与聊天」填写 API 地址和模型名称。");
        if (userInitiated) Touch();
        IsBusy = true; UpdateState(); Emote("ponder", 20000, false);
        bool replyEmotionRequested = false;
        try
        {
            var settings = Store.Snapshot().Settings;
            SyncPrivacyScope(settings); SyncMemoryPreference();
            var privacy = settings.Privacy;
            byte[]? image = attachedImage;
            if (image is null && privacy.ScreenEnabled && privacy.AttachOnMessage) image = ScreenCapture.Capture(privacy);
            if (image is not null && !privacy.ScreenEnabled) throw new InvalidOperationException("屏幕授权已关闭，请移除截图或重新启用。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token, image is null ? CancellationToken.None : screenGrant.Token);
            var result = await Chat.SendAsync(text, delta, linked.Token, image);
            if (result.Emotion is { Name: not "neutral" } && Store.Current.Emotion.Enabled)
            { Animator?.SetEmotion(result.Emotion); replyEmotionRequested = true; }
            else Animator?.SetEmotion(new EmotionIntent("neutral"));
            Status = "回复完成"; return result;
        }
        catch (OperationCanceledException) { Status = "本次请求已取消，未保存不完整回复。"; throw; }
        catch (Exception e) { Status = e.Message; throw; }
        finally
        {
            IsBusy = false;
            // A new response may still be waiting for the pondering pose's safe exit.
            // Clearing it here would also discard that pending response expression.
            if (!replyEmotionRequested && Animator?.CurrentEmotion == "ponder") Animator.SetEmotion(new EmotionIntent("neutral"));
            UpdateState();
        }
    }
    public async Task ProactiveNowAsync()
    {
        if (IsBusy) { Notify("奶蛙正在回复，稍后再试。"); return; }
        await RunProactive(true);
    }
    public void ClearMemory()
    {
        try { Chat.ClearMemory(); }
        finally { ResetProactiveContext(); Pet?.RefreshChatHistory(); }
    }
    private void ResetProactiveContext()
    {
        memoryContext.Cancel(); memoryContext.Dispose(); memoryContext = new();
        proactiveHistory.Clear();
    }
    private async Task RunProactive(bool manual)
    {
        if (disposed || IsBusy || !manual && (Paused || (Pet?.IsUiBusy ?? true))) return;
        IsBusy = true; UpdateState();
        try
        {
            string text;
            _ = Store.Snapshot(); SyncMemoryPreference();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, manual ? CancellationToken.None : automaticWork.Token, memoryContext.Token);
            if (Store.Current.Companion.ProactiveUseModel && Chat.IsConfigured)
            {
                int pairs = Math.Min(3, Store.Current.Model.ContextPairs);
                var recent = Store.Current.Data.RememberChat ? string.Join("\n", Chat.History.TakeLast(pairs*2).Select(m => m.Role + "：" + m.Content)) : "";
                var prior = Store.Current.Data.RememberChat && pairs > 0 ? string.Join("\n", proactiveHistory) : "";
                text = await Chat.CompleteRawAsync(Store.Current.Model.SystemPrompt + "\n" + Store.Current.Model.ProactivePrompt,
                    "最近对话（仅作语境）：\n" + recent + "\n此前主动发言（避免重复）：\n" + prior + "\n请自然说一小段话。", linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (Store.Current.Data.RememberChat)
                { proactiveHistory.Enqueue(text); while (proactiveHistory.Count > 4) proactiveHistory.Dequeue(); }
            }
            else
            {
                string[] lines = { "我在这里。忙完这一小段，想聊天时点我就好。", "可以放松一下肩膀，让眼睛看看远处。", "今天也可以慢慢来，完成一小步就很好。", "给你留一点安静的空间，我会在旁边陪着。" };
                text = lines[localLine++ % lines.Length];
            }
            linked.Token.ThrowIfCancellationRequested();
            lastProactive = DateTime.Now; hourlyCount++; Emote("smile", 5000, false); Notify(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Notify("主动陪伴暂时失败：" + e.Message, manual); }
        finally { IsBusy = false; UpdateState(); }
    }
    public void TogglePause()
    {
        Paused = !Paused;
        automaticWork.Cancel(); automaticWork.Dispose(); automaticWork = new();
        screenGrant.Cancel(); screenGrant.Dispose(); screenGrant = new();
        Notify(Paused ? "已暂停自动陪伴、散步与屏幕观察。" : "已恢复自动行为。");
    }
    public void ToggleFocus()
    {
        Touch();
        if (FocusEnd is not null) { FocusEnd = null; Notify("本次专注已结束。"); }
        else { FocusEnd = DateTime.Now.AddMinutes(Store.Current.Companion.FocusMinutes); Emote("focused", 8000); Notify($"开始专注 {Store.Current.Companion.FocusMinutes} 分钟，我会安静陪着你。"); }
        UpdateState();
    }
    public void PreviewScreen()
    {
        if (Pet is null) return;
        try { var settings = Store.Snapshot().Settings; SyncPrivacyScope(settings); var image = ScreenCapture.Capture(settings.Privacy); ScreenCapture.Preview(Pet, image, bytes => Pet.ShowChat(bytes)); }
        catch (Exception e) { Notify(e.Message); }
    }
    public static bool IsQuiet(DateTime now, int start, int end) => start != end && (start < end ? now.Hour >= start && now.Hour < end : now.Hour >= start || now.Hour < end);
    private async void Tick(object? sender, EventArgs e)
    {
        if (disposed || Pet is null) return;
        try
        {
            var now = DateTime.Now;
            var settings = Store.Current.Data.AllowMultipleInstances || Store.Current.Privacy.ScreenEnabled ? Store.Snapshot().Settings : Store.Current;
            SyncPrivacyScope(settings); SyncMemoryPreference();
            if (FocusEnd is DateTime end && now >= end)
            { FocusEnd = null; Emote("celebrate", 7000); Notify("专注时间到了，起来活动一下吧。"); PlaySound(); }
            if (reminderHour.Date != now.Date || reminderHour.Hour != now.Hour) { reminderHour = now; hourlyCount = 0; }
            UpdateState();
            if (Paused || IsBusy || Pet.IsUiBusy || FocusEnd is not null || Pet.IsMoving || Pet.IsDragging || IsQuiet(now, settings.Companion.QuietStart, settings.Companion.QuietEnd)) return;
            if (settings.Companion.IdleActions && (now.Hour >= 23 || now.Hour < 6) && (now - LastInteraction).TotalMinutes >= 10 && (now-lastSleepy).TotalMinutes >= 10)
            { lastSleepy = now; Emote("sleepy", 7000, false); }
            if (settings.Companion.Wander && (now-LastInteraction).TotalSeconds > 30 && (now-lastWander).TotalSeconds > 45)
            { lastWander = now; Pet.Wander(); }
            if (settings.Companion.ProactiveEnabled && hourlyCount < settings.Companion.MaxPerHour && (now-LastInteraction).TotalMinutes >= settings.Companion.IntervalMinutes && (now-lastProactive).TotalMinutes >= settings.Companion.IntervalMinutes)
            { await RunProactive(false); return; }
            if (settings.Privacy.ScreenEnabled && settings.Privacy.PeriodicObservation && Chat.IsConfigured && (now-lastScreen).TotalMinutes >= settings.Privacy.PeriodicMinutes)
            {
                lastScreen = now;
                try
                {
                    var image = ScreenCapture.Capture(settings.Privacy);
                    var result = await SendAsync("请简短概括这张授权截图中适合讨论的非敏感内容，不重复私人信息。", null, shutdown.Token, image, false);
                    Notify(result.Text);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Notify("定时观察已跳过：" + ex.Message, false); }
            }
        }
        catch (Exception ex) { Notify("后台任务：" + ex.Message, false); }
    }
    private void PlaySound() { if (Store.Current.Companion.SoundEnabled) System.Media.SystemSounds.Asterisk.Play(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        heartbeat.Stop(); heartbeat.Tick -= Tick; Store.Changed -= ApplySettings;
        shutdown.Cancel(); screenGrant.Cancel(); automaticWork.Cancel(); memoryContext.Cancel();
        if (Animator is not null) { Animator.StateChanged -= UpdateState; Animator.Dispose(); }
        Chat.Dispose(); shutdown.Dispose(); screenGrant.Dispose(); automaticWork.Dispose(); memoryContext.Dispose();
    }
}
