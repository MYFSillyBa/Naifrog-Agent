using System;
using System.Collections.Generic;
using Naifrog.Core;

namespace Naifrog.Animation;

internal enum PlaybackPhase { Idle, Entry, Main, Exit }
internal readonly record struct PlaybackTiming(double EntryMs, double MainMs, double ExitMs, bool Loop);

internal sealed class EmotionScheduler
{
    private readonly Func<double> clock;
    private readonly Func<EmotionSettings> options;
    private readonly Func<string, PlaybackTiming> timing;
    private Pending? pending;
    private PlaybackTiming currentTiming;
    private bool blocked;
    private bool currentManual;
    private int priority;
    private double expiresAt;
    private readonly Dictionary<string, double> lastStartedByEmotion = new(StringComparer.Ordinal);
    private double phaseStarted;
    private sealed record Pending(EmotionIntent Intent, bool Manual, double ExpiresAt);

    public EmotionScheduler(Func<double> clock, Func<EmotionSettings> options, Func<string, PlaybackTiming> timing)
    { this.clock = clock; this.options = options; this.timing = timing; }
    public string Name { get; private set; } = "neutral";
    public PlaybackPhase Phase { get; private set; }
    public double StartedAt { get; private set; }
    public double PhaseStartedAt => phaseStarted;
    public double Intensity { get; private set; }
    public bool IsBlocked => blocked;
    public bool HasWork => Phase != PlaybackPhase.Idle || pending != null;

    public void Submit(EmotionIntent intent, bool manual = false)
    {
        Tick();
        var now = clock();
        var settings = options();
        if (!manual && !settings.Enabled) return;
        string name = (intent.Name ?? "neutral").Trim().ToLowerInvariant();
        if (!double.IsFinite(intent.Intensity) || !double.IsFinite(intent.Confidence) ||
            intent.Intensity < .18 || intent.Confidence < settings.MinimumConfidence ||
            EmotionCatalog.Find(name) == null)
            name = "neutral";
        if (name == "happy" && intent.Intensity < .6) name = "smile";
        if (name == "neutral")
        {
            pending = null;
            if (!blocked) BeginExit(now);
            return;
        }
        var definition = EmotionCatalog.Find(name)!;
        var clean = intent with { Name = name, Intensity = Math.Clamp(intent.Intensity, 0, 1),
            Confidence = Math.Clamp(intent.Confidence, 0, 1), DurationMs = Math.Clamp(intent.DurationMs, 250, 60000),
            Priority = Math.Max(definition.Priority, Math.Clamp(intent.Priority, 0, 100)) };
        if (Name == name && Phase != PlaybackPhase.Idle)
        {
            Intensity = Intensity * .65 + clean.Intensity * .35;
            // An exit already in progress finishes once; a repeated classifier result must not replay it.
            if (Phase != PlaybackPhase.Exit) expiresAt = Math.Max(expiresAt, now + clean.DurationMs);
            return;
        }
        var next = new Pending(clean, manual, now + clean.DurationMs);
        if (pending == null || manual || clean.Priority >= pending.Intent.Priority) pending = next;
        Dispatch(now);
        Tick();
    }

    public void Tick()
    {
        double now = clock();
        if (!options().Enabled && !currentManual && Phase != PlaybackPhase.Idle) BeginExit(now);
        if (pending != null && pending.ExpiresAt <= now) pending = null;
        // A delayed UI tick may cross entry, main and exit in one update.
        for (int step = 0; step < 6; step++)
        {
            if (Phase == PlaybackPhase.Entry)
            {
                double end = Math.Min(StartedAt + currentTiming.EntryMs, expiresAt);
                if (now < end) break;
                Phase = PlaybackPhase.Main; phaseStarted = end;
            }
            else if (Phase == PlaybackPhase.Main)
            {
                double end = currentTiming.Loop ? expiresAt : Math.Min(expiresAt, phaseStarted + currentTiming.MainMs);
                if (now < end) break;
                BeginExit(end);
            }
            else if (Phase == PlaybackPhase.Exit)
            {
                if (now < phaseStarted + currentTiming.ExitMs) break;
                Phase = PlaybackPhase.Idle; Name = "neutral"; Intensity = 0; currentManual = false;
            }
            else break;
        }
        Dispatch(now);
    }

    public void SetBlocked(bool value)
    {
        blocked = value;
        Tick();
    }

    private bool Ready(Pending request, double now) => request.Manual ||
        (options().Enabled && (!lastStartedByEmotion.TryGetValue(request.Intent.Name, out double lastStarted) ||
            now - lastStarted >= Math.Clamp(options().CooldownSeconds, 0, 60) * 1000));

    private void Dispatch(double now)
    {
        if (blocked || pending == null || pending.ExpiresAt <= now || !Ready(pending, now)) return;
        if (Phase != PlaybackPhase.Idle)
        {
            if (pending.Manual || pending.Intent.Priority >= priority) BeginExit(now);
            return;
        }
        var request = pending; pending = null;
        Name = request.Intent.Name; Intensity = request.Intent.Intensity; priority = request.Intent.Priority;
        currentManual = request.Manual; expiresAt = request.ExpiresAt;
        currentTiming = timing(Name);
        StartedAt = phaseStarted = now;
        lastStartedByEmotion[Name] = now;
        Phase = currentTiming.EntryMs > 0 ? PlaybackPhase.Entry : PlaybackPhase.Main;
    }

    private void BeginExit(double now)
    {
        if (Phase == PlaybackPhase.Idle || Phase == PlaybackPhase.Exit) return;
        Phase = PlaybackPhase.Exit; phaseStarted = now;
    }
}
