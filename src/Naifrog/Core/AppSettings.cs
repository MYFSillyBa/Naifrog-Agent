using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Naifrog.Core;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string? CredentialId { get; set; }
    public AppearanceSettings Appearance { get; set; } = new();
    public ModelSettings Model { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();
    public EmotionSettings Emotion { get; set; } = new();
    public CompanionSettings Companion { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public void Validate()
    {
        if (CredentialId is not null && !Guid.TryParseExact(CredentialId, "N", out _)) throw new ArgumentException("密钥引用无效。");
        Appearance ??= new(); Model ??= new(); Agent ??= new(); Emotion ??= new(); Companion ??= new(); Privacy ??= new(); Files ??= new(); Data ??= new();
        void Range(double value, double min, double max, string label)
        { if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"{label}应在 {min}–{max} 之间。"); }
        Range(Appearance.Height, 120, 700, "角色高度");
        Range(Appearance.Opacity, .25, 1, "不透明度");
        if (Appearance.Left is double x && !double.IsFinite(x) || Appearance.Top is double y && !double.IsFinite(y)) throw new ArgumentException("窗口位置无效。");
        Range(Model.MaxTokens, 128, 8192, "回复长度上限");
        Range(Model.TimeoutSeconds, 5, 180, "请求超时");
        Range(Model.ContextPairs, 0, 30, "上下文轮数");
        Range(Agent.ContextWindowTokens, 8192, 2000000, "Agent 上下文窗口 token 数");
        Range(Agent.CommandTimeoutSeconds, 5, 1800, "Agent 命令超时");
        Range(Agent.MaxTokens, 512, 16384, "Agent 单次输出上限");
        if ((Agent.SystemPrompt ?? "").Length > 24000) throw new ArgumentException("Agent 提示词过长。");
        Range(Emotion.MinimumConfidence, 0, 1, "情绪置信度");
        Range(Emotion.CooldownSeconds, 0, 60, "动作冷却");
        Range(Emotion.Strength, .1, 1, "动作幅度");
        Range(Companion.IntervalMinutes, 1, 180, "陪伴间隔");
        Range(Companion.MaxPerHour, 1, 30, "每小时次数");
        Range(Companion.QuietStart, 0, 23, "免打扰开始"); Range(Companion.QuietEnd, 0, 23, "免打扰结束");
        Range(Companion.FocusMinutes, 1, 180, "专注时长");
        Range(Privacy.PeriodicMinutes, 1, 120, "观察间隔");
        Range(Data.MaxMemoryPairs, 1, 500, "记忆轮数");
        if (!string.IsNullOrWhiteSpace(Model.BaseUrl))
        {
            if (!Uri.TryCreate(Model.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
                (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("API 地址须使用 HTTPS；本机服务可使用 HTTP。不要在地址内填写密钥。");
        }
        if ((Model.ModelId ?? "").Length > 200 || (Model.SystemPrompt ?? "").Length > 16000) throw new ArgumentException("模型名称或提示词过长。");
        if (Files.ReceiveMode != "Copy" && Files.ReceiveMode != "Move") throw new ArgumentException("无效的文件接收方式。");
    }
}

public sealed class AppearanceSettings
{
    public double Height { get; set; } = 330;
    public double Opacity { get; set; } = 1;
    public bool Topmost { get; set; } = true;
    public bool UseAnimationPack { get; set; } = true;
    public bool DragEnabled { get; set; } = true;
    public bool RememberPosition { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
}
public sealed class ModelSettings
{
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string SystemPrompt { get; set; } = "你是奶蛙，一只温柔、憨厚、有点笨拙但有边界的黄色小青蛙。用自然简洁的中文陪伴用户，认真回答问题。不要声称看到了未提供的屏幕，不编造经历。";
    public string ProactivePrompt { get; set; } = "结合最近聊天，自然地说一小段陪伴的话。避免重复，不催促，不要每次都提醒喝水。没有截图时不要声称看到了屏幕。";
    public bool Streaming { get; set; } = true;
    public int MaxTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 60;
    public int ContextPairs { get; set; } = 8;
}
public sealed class EmotionSettings
{
    public bool Enabled { get; set; } = true;
    public bool AnalyzeWithModel { get; set; } = false;
    public bool MicroMotion { get; set; } = true;
    public double Strength { get; set; } = .6;
    public double MinimumConfidence { get; set; } = .72;
    public int CooldownSeconds { get; set; } = 6;
    public string Instruction { get; set; } = "判断奶蛙适合表现的回应情绪，结合当前消息与语境；不机械复制用户情绪。用户难过时温柔共情，不大笑；普通问答可选 neutral。";
}
public sealed class CompanionSettings
{
    public bool Greeting { get; set; } = true;
    public bool IdleActions { get; set; } = true;
    public bool Wander { get; set; } = false;
    public bool ProactiveEnabled { get; set; } = false;
    public bool ProactiveUseModel { get; set; } = false;
    public int IntervalMinutes { get; set; } = 15;
    public int MaxPerHour { get; set; } = 4;
    public int QuietStart { get; set; } = 23;
    public int QuietEnd { get; set; } = 8;
    public int FocusMinutes { get; set; } = 25;
    public bool SoundEnabled { get; set; } = false;
}
public sealed class PrivacySettings
{
    public bool ScreenEnabled { get; set; } = false;
    public bool AttachOnMessage { get; set; } = false;
    public bool PeriodicObservation { get; set; } = false;
    public int PeriodicMinutes { get; set; } = 10;
    public int MonitorIndex { get; set; } = 0;
    public string ExcludedApps { get; set; } = "";
}
public sealed class FileSettings
{
    public bool ReceiveEnabled { get; set; } = true;
    public string ReceiveFolder { get; set; } = "";
    public string ReceiveMode { get; set; } = "Copy";
    public string OrganizerSource { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string OrganizerTarget { get; set; } = "";
    public string OrganizerMode { get; set; } = "Reference";
    public bool UseAiClassification { get; set; } = false;
}
public sealed class DataSettings
{
    public bool RememberChat { get; set; } = true;
    public int MaxMemoryPairs { get; set; } = 100;
    public bool AutoStart { get; set; } = false;
    public bool GlobalHotkeys { get; set; } = false;
    public bool AllowMultipleInstances { get; set; } = false;
}

public sealed record EmotionIntent(string Name, double Intensity = .7, double Confidence = 1, int DurationMs = 6000, string Source = "manual", int Priority = 20);
public sealed record ChatMessage(string Role, string Content);
public sealed record ChatResult(string Text, EmotionIntent? Emotion = null);
