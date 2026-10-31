using Naifrog.Animation;
using Naifrog.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static void Main(string[] args)
    {
        double now = 0;
        var options = new EmotionSettings { CooldownSeconds = 6 };
        var scheduler = new EmotionScheduler(() => now, () => options, _ => new PlaybackTiming(100, 1000, 200, true));
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        double cooldownNow = 0;
        var cooldownOptions = new EmotionSettings { CooldownSeconds = 6 };
        var crossEmotionScheduler = new EmotionScheduler(() => cooldownNow, () => cooldownOptions,
            _ => new PlaybackTiming(0, 1000, 200, true));
        crossEmotionScheduler.Submit(new("ponder", .8, 1, 20000));
        cooldownNow = 1000; crossEmotionScheduler.Submit(new("happy", .9, 1, 6000));
        Check(crossEmotionScheduler.Phase == PlaybackPhase.Exit, "different emotion bypasses unrelated cooldown using safe exit");
        cooldownNow = 1201; crossEmotionScheduler.Tick();
        Check(crossEmotionScheduler.Name == "happy", "response emotion starts after pondering exit without unrelated delay");
        cooldownNow = 0;
        var sameEmotionScheduler = new EmotionScheduler(() => cooldownNow, () => cooldownOptions,
            _ => new PlaybackTiming(0, 1000, 200, true));
        sameEmotionScheduler.Submit(new("happy", .9, 1, 500));
        cooldownNow = 701; sameEmotionScheduler.Tick();
        sameEmotionScheduler.Submit(new("happy", .9, 1, 10000));
        Check(sameEmotionScheduler.Name == "neutral", "same emotion still waits for its own cooldown after completion");
        cooldownNow = 5999; sameEmotionScheduler.Tick();
        Check(sameEmotionScheduler.Name == "neutral", "same emotion cannot restart before cooldown boundary");
        cooldownNow = 6000; sameEmotionScheduler.Tick();
        Check(sameEmotionScheduler.Name == "happy", "same emotion restarts at its own cooldown boundary");
        scheduler.Submit(new("happy", .8, 1, 2000));
        Check(scheduler.Name == "happy" && scheduler.Phase == PlaybackPhase.Entry, "entry begins immediately");
        now = 150; scheduler.Tick();
        Check(scheduler.Phase == PlaybackPhase.Main, "elapsed time enters main");
        var start = scheduler.StartedAt;
        scheduler.Submit(new("happy", .9, 1, 3000));
        Check(scheduler.StartedAt == start && scheduler.Intensity > .8, "duplicate merges without restarting");
        scheduler.Submit(new("sad", .8, 1, 10000));
        Check(scheduler.Name == "happy", "lower priority emotion waits for current action");
        now = 3200; scheduler.Tick();
        Check(scheduler.Phase == PlaybackPhase.Exit, "duration starts exit");
        now = 3500; scheduler.Tick();
        Check(scheduler.Name == "sad", "different pending emotion starts after current exit");
        now = 6001; scheduler.Tick();
        Check(scheduler.Name == "sad", "pending action retains its received deadline");
        scheduler.Submit(new("greet", .8, 1, 4000), true);
        now = 6250; scheduler.Tick();
        Check(scheduler.Name == "greet", "manual bypasses cooldown after safe exit");

        now = 20000; scheduler.Tick(); scheduler.SetBlocked(true);
        scheduler.Submit(new("happy", .8, 1, 1000), true);
        Check(scheduler.Name == "neutral", "blocked state prevents start");
        now = 22000; scheduler.SetBlocked(false);
        Check(scheduler.Name == "neutral", "expired blocked intent is discarded");
        scheduler.Submit(new("happy", .3, 1, 1000), true);
        Check(scheduler.Name == "smile", "weak happy selects smile");
        scheduler.SetBlocked(true);
        scheduler.Submit(new("organize", 1, 1, 10000), true);
        Check(scheduler.Name == "smile" && scheduler.Phase != PlaybackPhase.Exit, "blocked state prevents preemption");
        scheduler.SetBlocked(false); now += 250; scheduler.Tick();
        Check(scheduler.Name == "organize", "unblocked pending uses transition");
        scheduler.Submit(new("sad", .9, .1, 1000)); now += 250; scheduler.Tick();
        Check(scheduler.Name == "neutral", "low confidence returns idle");
        scheduler.Submit(new("happy", double.NaN, 1), true);
        Check(scheduler.Name == "neutral", "nonfinite values rejected");
        scheduler.Submit(new("unknown", 1, 1), true);
        Check(scheduler.Name == "neutral", "unknown emotion rejected");
        var finiteScheduler = new EmotionScheduler(() => now, () => options, _ => new PlaybackTiming(100, 1000, 200, false));
        finiteScheduler.Submit(new("happy", .9, 1, 60000), true);
        now += 1301; finiteScheduler.Tick();
        Check(finiteScheduler.Name == "neutral", "nonloop clip finishes before request duration");
        finiteScheduler.Submit(new("organize", 1, 1, 20000), true);
        options.CooldownSeconds = 0;
        finiteScheduler.Submit(new("happy", .9, 1, 10000));
        Check(finiteScheduler.Name == "organize" && finiteScheduler.Phase != PlaybackPhase.Exit, "lower priority cannot preempt scene");
        now += 1301; finiteScheduler.Tick();
        Check(finiteScheduler.Name == "happy", "lower priority waits for scene exit");
        now += 30000; finiteScheduler.Tick();
        Check(finiteScheduler.Name == "neutral", "delayed tick crosses all completed phases");
        var exitingScheduler = new EmotionScheduler(() => now, () => options, _ => new PlaybackTiming(100, 1000, 200, true));
        exitingScheduler.Submit(new("happy", .9, 1, 500), true);
        now += 501; exitingScheduler.Tick();
        exitingScheduler.Submit(new("happy", 1, 1, 6000), true);
        now += 250; exitingScheduler.Tick();
        Check(exitingScheduler.Name == "neutral", "duplicate during exit does not replay emotion");
        double disabledNow = 0;
        var disabledOptions = new EmotionSettings { Enabled = false, CooldownSeconds = 0 };
        var disabledScheduler = new EmotionScheduler(() => disabledNow, () => disabledOptions,
            _ => new PlaybackTiming(0, 1000, 200, true));
        disabledScheduler.Submit(new("happy", .8, 1, 1000), true);
        disabledScheduler.Submit(new("sad", .9, 1, 10000));
        Check(disabledScheduler.Name == "happy" && disabledScheduler.Phase == PlaybackPhase.Main,
            "disabled automatic request preserves manual preview");
        disabledNow = 500;
        disabledScheduler.Submit(new("happy", 1, 1, 10000));
        disabledNow = 1001; disabledScheduler.Tick();
        Check(disabledScheduler.Phase == PlaybackPhase.Exit && disabledScheduler.Intensity == .8,
            "disabled duplicate does not merge or extend manual preview");
        disabledNow = 1202; disabledScheduler.Tick();
        disabledScheduler.Submit(new("happy", .8, 1, 10000), true);
        disabledScheduler.SetBlocked(true);
        disabledScheduler.Submit(new("greet", .8, 1, 10000), true);
        disabledScheduler.Submit(new("neutral"));
        disabledScheduler.SetBlocked(false);
        disabledNow += 201; disabledScheduler.Tick();
        Check(disabledScheduler.Name == "greet", "disabled automatic neutral preserves pending manual action");
        disabledOptions.Enabled = true;
        var disablingScheduler = new EmotionScheduler(() => disabledNow, () => disabledOptions,
            _ => new PlaybackTiming(0, 1000, 200, true));
        disablingScheduler.Submit(new("happy", .8, 1, 10000));
        disabledOptions.Enabled = false; disablingScheduler.Tick();
        Check(disablingScheduler.Phase == PlaybackPhase.Exit, "disabling automatic emotion still retires active automatic action");
        disabledNow += 201; disablingScheduler.Tick();
        Check(disablingScheduler.Name == "neutral", "disabled automatic action completes its exit");
        var directory = Path.Combine(Path.GetTempPath(), "NaifrogAnimationChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] MakePng()
            {
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 1,2,3,255, 4,5,6,255, 7,8,9,255, 10,11,12,255 }, 8);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
            }
            var bytes = MakePng();
            void WriteClip(string name, string frameFile = "frame.png", string? hash = null, int width = 2)
            {
                var folder = Path.Combine(directory, name); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "frame.png"), bytes);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
                { name, emotion = "happy", fps = 25, frameCount = 1, loop = true, priority = 30,
                  canvas = new { width, height = 2 }, groundY = 2,
                  frames = new[] { new { file = frameFile, sha256 = hash ?? Convert.ToHexString(SHA256.HashData(bytes)) } } }));
            }
            WriteClip("valid"); WriteClip("traversal", "../valid/frame.png"); WriteClip("hash", hash: new string('0', 64)); WriteClip("dimensions", width: 5000);
            var library = new AnimationLibrary(directory);
            var clip = library.TryGetInfo("valid");
            Check(clip != null && clip.DurationMs == 40, "valid manifest timing");
            var frames = library.GetFrames(clip!);
            Check(frames is { Count: 1 } && frames[0].IsFrozen, "valid PNG decodes frozen");
            Check(ReferenceEquals(frames, library.GetFrames(clip!)), "clip cache avoids repeat decoding");
            Check(library.TryGetInfo("traversal") == null, "frame traversal rejected");
            Check(library.TryGetInfo("../valid") == null, "clip traversal rejected");
            Check(library.GetFrames(library.TryGetInfo("hash")!) == null, "incorrect frame hash rejected");
            Check(library.TryGetInfo("dimensions") == null, "oversized canvas rejected before decode");
            Check(library.TryGetInfo("absent") == null, "missing pack handled");
            Check(EmotionCatalog.Entries.Count(e => e.Automatic) == 26 && EmotionCatalog.Entries.Count == 36, "complete catalog IDs");
            var image = new System.Windows.Controls.Image { Source = frames![0] };
            var appearance = new AppearanceSettings { UseAnimationPack = false };
            var animationOptions = new EmotionSettings { MicroMotion = false };
            using (var animator = new PetAnimator(image, () => appearance, () => animationOptions, directory))
            {
                var original = image.Source;
                Check(image.Source is BitmapSource originalBitmap && originalBitmap.PixelWidth > 2, "disabled pack loads original resource artwork");
                Check(!animator.FastTimerRunning, "static idle stops fast timer");
                animator.SetEmotion(new("happy", .8), true);
                Check(animator.CurrentEmotion == "happy", "public animator routes intent");
                Check(ReferenceEquals(image.Source, original), "emotion micro action preserves original when pack disabled");
                Check(!animator.FastTimerRunning, "static active request uses slow expiry timer");
                animationOptions.MicroMotion = true; animator.ApplySettings();
                Check(animator.FastTimerRunning, "micro motion enables fast timer");
                appearance.UseAnimationPack = true; animator.ApplySettings();
                Check(image.Source != null, "missing requested clip retains artwork");
            }
            Check(image.RenderTransform == System.Windows.Media.Transform.Identity, "dispose restores image transform");
        }
        finally { Directory.Delete(directory, true); }
        if (args.Length == 2 && args[0] == "--pack")
        {
            using var pack = new AnimationLibrary(args[1]);
            int clipCount = 0, frameCount = 0;
            foreach (var folder in Directory.GetDirectories(args[1]).OrderBy(p => p))
            {
                if (!File.Exists(Path.Combine(folder, "manifest.json"))) continue;
                string name = Path.GetFileName(folder);
                var info = pack.TryGetInfo(name);
                Check(info != null, "pack metadata " + name + " " + pack.LastError);
                var decoded = pack.GetFrames(info!);
                Check(decoded?.Count == info!.Frames.Count, "pack decode " + name + " " + pack.LastError);
                Check(pack.DecodedCacheBytes <= 192L * 1024 * 1024, "pack cache bounded " + name);
                clipCount++; frameCount += decoded!.Count;
            }
            Console.WriteLine($"Pack verified: {clipCount} clips, {frameCount} frames.");
            var packImage = new System.Windows.Controls.Image();
            var packAppearance = new AppearanceSettings();
            using var animator = new PetAnimator(packImage, () => packAppearance, () => new EmotionSettings { MicroMotion = false }, args[1]);
            Check(animator.CurrentEmotion == "neutral" && packImage.Source is BitmapSource { IsFrozen: true }, "real pack idle initializes frozen");
            Check(animator.FastTimerRunning, "real idle sequence keeps playback timer");
            animator.SetEmotion(new("happy", .9), true);
            Check(animator.CurrentEmotion == "happy" && animator.Status.Contains("入场"), "real pack starts entry clip");
            packAppearance.UseAnimationPack = false; animator.ApplySettings();
            Check(animator.Status.Contains("原始立绘") && !animator.FastTimerRunning, "real pack switch disables sequence playback");
        }
        Console.WriteLine($"Animation checks: {checks} passed.");
    }
}
