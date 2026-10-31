using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Naifrog.Core;

namespace Naifrog.Animation;

public sealed class PetAnimator : IDisposable
{
    private readonly Image target;
    private readonly Func<AppearanceSettings> appearance;
    private readonly Func<EmotionSettings> options;
    private readonly AnimationLibrary library;
    private readonly EmotionScheduler scheduler;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer;
    private readonly ImageSource? original;
    private readonly Transform previousTransform;
    private readonly Point previousOrigin;
    private readonly ScaleTransform scale = new(1, 1);
    private readonly RotateTransform rotate = new(0);
    private readonly TranslateTransform translate = new(0, 0);
    private AnimationClip? shownClip;
    private IReadOnlyList<BitmapSource>? shownFrames;
    private string? attemptedClip;
    private bool disposed;
    private bool unloaded;
    private bool usedFallback;
    private bool fastTimer;
    private string status = "";
    internal bool FastTimerRunning => timer.IsEnabled && fastTimer;

    public PetAnimator(Image target, Func<AppearanceSettings> appearance, Func<EmotionSettings> options, string assetsRoot)
    {
        target.Dispatcher.VerifyAccess();
        this.target = target; this.appearance = appearance; this.options = options;
        original = LoadOriginal(target.Source);
        previousTransform = target.RenderTransform; previousOrigin = target.RenderTransformOrigin;
        var transforms = new TransformGroup();
        transforms.Children.Add(previousTransform); transforms.Children.Add(scale); transforms.Children.Add(rotate); transforms.Children.Add(translate);
        target.RenderTransform = transforms; target.RenderTransformOrigin = new Point(.5, .965);
        library = new AnimationLibrary(assetsRoot);
        scheduler = new EmotionScheduler(() => clock.Elapsed.TotalMilliseconds, options, GetTiming);
        timer = new DispatcherTimer(DispatcherPriority.Render, target.Dispatcher);
        timer.Tick += OnTick;
        target.Loaded += OnLoaded; target.Unloaded += OnUnloaded;
        Refresh();
    }

    public string CurrentEmotion => scheduler.Name;
    public string Status => status;
    public event Action? StateChanged;

    public void SetEmotion(EmotionIntent intent, bool manual = false) => OnUi(() =>
    {
        scheduler.Submit(intent, manual); Refresh();
    });

    public void SetBlocked(bool blocked) => OnUi(() =>
    {
        scheduler.SetBlocked(blocked); Refresh();
    });

    public void ApplySettings() => OnUi(() =>
    {
        attemptedClip = null; shownFrames = null; shownClip = null;
        scheduler.Tick(); Refresh();
    });

    private PlaybackTiming GetTiming(string name)
    {
        if (!appearance().UseAnimationPack) return new PlaybackTiming(0, 0, 0, true);
        var main = library.TryGetInfo(name);
        if (main == null) return new PlaybackTiming(0, 0, 0, true);
        var entry = main.TransitionIn == null ? null : library.TryGetInfo(main.TransitionIn);
        var exit = main.TransitionOut == null ? null : library.TryGetInfo(main.TransitionOut);
        return new PlaybackTiming(entry?.DurationMs ?? 0, main.DurationMs, exit?.DurationMs ?? 0, main.Loop);
    }

    private void OnTick(object? sender, EventArgs e) { if (!disposed) { scheduler.Tick(); Refresh(); } }
    private void OnLoaded(object sender, RoutedEventArgs e) { unloaded = false; Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) { unloaded = true; timer.Stop(); }

    private void Refresh()
    {
        if (disposed) return;
        double now = clock.Elapsed.TotalMilliseconds;
        string name = scheduler.Name;
        string? requested = null;
        if (appearance().UseAnimationPack)
        {
            if (name == "neutral") requested = "idle";
            else
            {
                var main = library.TryGetInfo(name);
                requested = scheduler.Phase switch
                {
                    PlaybackPhase.Entry => main?.TransitionIn,
                    PlaybackPhase.Exit => main?.TransitionOut,
                    _ => name
                };
            }
        }
        if (requested != attemptedClip)
        {
            attemptedClip = requested;
            // Release the old sequence before decoding the next bounded clip.
            shownFrames = null;
            shownClip = requested == null ? null : library.TryGetInfo(requested);
            if (shownClip != null) shownFrames = library.GetFrames(shownClip);
        }
        usedFallback = shownClip == null || shownFrames == null;
        if (usedFallback)
        {
            if (!ReferenceEquals(target.Source, original)) target.Source = original;
        }
        else
        {
            double elapsed = Math.Max(0, now - (name == "neutral" ? 0 : scheduler.PhaseStartedAt));
            int frame = (int)(elapsed / 1000 * shownClip!.Fps);
            // Entry and exit always hold their final frame; only main/idle may loop.
            frame = shownClip.Loop && (scheduler.Phase is PlaybackPhase.Main or PlaybackPhase.Idle)
                ? frame % shownFrames!.Count : Math.Min(frame, shownFrames!.Count - 1);
            var source = shownFrames[frame];
            if (!ReferenceEquals(target.Source, source)) target.Source = source;
        }
        ApplyMotion(now);
        string label = EmotionCatalog.Find(name)?.Label ?? "待机";
        string phase = scheduler.Phase switch { PlaybackPhase.Entry => " · 入场", PlaybackPhase.Exit => " · 退场", _ => "" };
        string rendering = usedFallback ? " · 原始立绘" : " · 动画素材";
        string nextStatus = label + phase + rendering + (scheduler.IsBlocked ? " · 操作中" : "");
        if (status != nextStatus) { status = nextStatus; StateChanged?.Invoke(); }
        UpdateTimer();
    }

    private void ApplyMotion(double now)
    {
        double t = now / 1000;
        double strength = double.IsFinite(options().Strength) ? Math.Clamp(options().Strength, .1, 1) : .6;
        bool active = scheduler.Phase != PlaybackPhase.Idle;
        bool motion = options().MicroMotion && !scheduler.IsBlocked;
        double x = 0, y = 0, angle = 0, sx = 1, sy = 1;
        if (motion)
        {
            double intensity = strength * (active ? scheduler.Intensity : .35);
            double breath = Math.Sin(t * 2.2);
            sx += .003 * breath * strength; sy += .006 * breath * strength;
            string gesture = active ? EmotionCatalog.Find(scheduler.Name)?.Gesture ?? "breathe" : "breathe";
            switch (gesture)
            {
                case "bounce": y -= Math.Abs(Math.Sin(t * 5)) * 6 * intensity; sy += Math.Sin(t * 10) * .012 * intensity; break;
                case "sway": angle = Math.Sin(t * 2.8) * 2.0 * intensity; break;
                case "tilt": angle = Math.Sin(t * 1.6) * 2.7 * intensity; break;
                case "tremble": x = Math.Sin(t * 26) * 1.8 * intensity; break;
                case "shake": angle = Math.Sin(t * 8) * 1.8 * intensity; break;
                case "nod": sy += Math.Sin(t * 5) * .018 * intensity; break;
                case "bow": sy -= (1 + Math.Sin(t * 2)) * .018 * intensity; sx += (1 + Math.Sin(t * 2)) * .009 * intensity; break;
                case "squat": sy -= (1 + Math.Sin(t * 4)) * .025 * intensity; sx += (1 + Math.Sin(t * 4)) * .012 * intensity; break;
                case "sigh": sy -= (1 + Math.Sin(t * 1.5)) * .009 * intensity; break;
                case "rise": sy += .018 * intensity; break;
                case "recoil": sy -= Math.Abs(Math.Sin(t * 3)) * .02 * intensity; break;
            }
            if (active)
            {
                double age = now - scheduler.StartedAt;
                if (age is >= 0 and < 300)
                {
                    double settle = Math.Sin(age / 300 * Math.PI) * .015 * intensity;
                    sy -= settle; sx += settle * .5;
                }
            }
        }
        scale.ScaleX = Math.Clamp(sx, .94, 1.06); scale.ScaleY = Math.Clamp(sy, .92, 1.06);
        rotate.Angle = Math.Clamp(angle, -3, 3); translate.X = Math.Clamp(x, -3, 3);
        double groundFraction = !usedFallback && shownClip != null ? shownClip.GroundY / shownClip.Height : .965;
        target.RenderTransformOrigin = new Point(.5, groundFraction);
        // Align the clip's declared ground to the same normalized foot baseline.
        double height = target.ActualHeight;
        if (!usedFallback && shownClip != null && target.ActualWidth > 0)
            height = Math.Min(height, target.ActualWidth * shownClip.Height / shownClip.Width);
        double alignment = Math.Clamp((.965 - groundFraction) * height, -8, 8);
        translate.Y = Math.Clamp(y + alignment, -10, 10);
    }

    private void UpdateTimer()
    {
        if (disposed) return;
        bool micro = options().MicroMotion && !scheduler.IsBlocked;
        bool sequence = shownFrames is { Count: > 1 } &&
            (scheduler.Phase != PlaybackPhase.Idle || shownClip?.Loop == true);
        fastTimer = micro || sequence;
        bool needed = fastTimer || scheduler.HasWork;
        TimeSpan interval = TimeSpan.FromMilliseconds(fastTimer ? 40 : 250);
        if (timer.Interval != interval) timer.Interval = interval;
        if (needed && !unloaded) timer.Start(); else timer.Stop();
    }

    private void OnUi(Action action)
    {
        if (disposed || target.Dispatcher.HasShutdownStarted) return;
        if (target.Dispatcher.CheckAccess()) action();
        else target.Dispatcher.BeginInvoke(new Action(() => { if (!disposed) action(); }));
    }

    private static ImageSource? LoadOriginal(ImageSource? existing)
    {
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri("pack://application:,,,/Assets/naifrog.png", UriKind.Absolute);
            bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return existing; }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!target.Dispatcher.CheckAccess())
        {
            if (!target.Dispatcher.HasShutdownStarted) target.Dispatcher.Invoke(Dispose);
            return;
        }
        disposed = true; timer.Stop(); timer.Tick -= OnTick;
        target.Loaded -= OnLoaded; target.Unloaded -= OnUnloaded;
        target.RenderTransform = previousTransform; target.RenderTransformOrigin = previousOrigin;
        target.Source = original;
        shownFrames = null; shownClip = null;
        library.Dispose(); clock.Stop(); StateChanged = null;
    }
}
