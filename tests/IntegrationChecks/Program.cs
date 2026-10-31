using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Naifrog;
using Naifrog.Core;
using Naifrog.Desktop;
using Naifrog.UI;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Ui.Install();
        string root = Path.Combine(Path.GetTempPath(), "Naifrog-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run(CheckAll(root));
            Console.WriteLine($"PASS: {passed} integration checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); Directory.Delete(root, true); }
    }
    private static async Task CheckAll(string root)
    {
        var store = new SettingsStore(Path.Combine(root, "profile"));
        var settings = store.Current.Clone();
        settings.Model.BaseUrl = "http://localhost:9999/v1"; settings.Model.ModelId = "fake-model";
        settings.Appearance.UseAnimationPack = false; settings.Appearance.RememberPosition = false;
        settings.Emotion.MicroMotion = false; settings.Emotion.AnalyzeWithModel = true; settings.Emotion.CooldownSeconds = 6;
        settings.Companion.Greeting = false; store.Save(settings, "integration-secret");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        var userPayloads = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            if (hold) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            string body = await request.Content!.ReadAsStringAsync(token);
            using var json = JsonDocument.Parse(body);
            var lastContent = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content");
            if (lastContent.ValueKind == JsonValueKind.String) userPayloads.Add(lastContent.GetString()!);
            bool classify = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("confidence", StringComparison.Ordinal);
            string text = classify ? "{\"name\":\"hug\",\"intensity\":0.8,\"confidence\":0.95}" : "奶蛙收到啦。";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json") };
        });
        using var runtime = new PetRuntime(store, handler, false, false);
        var pet = new PetWindow(runtime); pet.Show(); await Task.Delay(100);
        try
        {
            var area = SystemParameters.WorkArea;
            Check(Math.Abs(pet.Left - (area.Right-pet.Width-32)) < 1 && Math.Abs(pet.Top-(area.Bottom-pet.Height-8)) < 1, "fresh profile starts at work-area bottom right");
            var result = await runtime.SendAsync("请安慰我", null, CancellationToken.None);
            await Task.Delay(150);
            Check(result.Emotion?.Name == "hug" && runtime.Animator?.CurrentEmotion == "hug", "fast model result survives waiting-pose cleanup");
            var next = store.Current.Clone(); next.Emotion.AnalyzeWithModel = false; store.Save(next);
            hold = true;
            var pending = runtime.SendAsync("普通设置保存不打断聊天", null, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            next = store.Current.Clone(); next.Appearance.Opacity = .9; store.Save(next);
            release.SetResult();
            Check((await pending).Text == "奶蛙收到啦。", "appearance settings do not cancel plain chat");
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            next = store.Current.Clone(); next.Privacy.ScreenEnabled = true; store.Save(next);
            byte[] png = File.ReadAllBytes(Path.Combine(FindProjectRoot(), "src/Naifrog/Assets/naifrog.png"));
            var withImage = runtime.SendAsync("仅注入图片，不截取屏幕", null, CancellationToken.None, png);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            next = store.Current.Clone(); next.Privacy.ScreenEnabled = false; store.Save(next);
            bool cancelled = false;
            try { await withImage; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !runtime.IsBusy, "revoked screen grant cancels an in-flight image request");
            hold = false;
            bool denied = false;
            try { ScreenCapture.Capture(new PrivacySettings()); } catch (InvalidOperationException) { denied = true; }
            Check(denied, "screen capture refuses missing session grant before capture");
            runtime.ToggleFocus(); Check(runtime.FocusEnd is not null, "focus starts");
            runtime.ToggleFocus(); Check(runtime.FocusEnd is null, "focus stops");
            runtime.TogglePause(); Check(runtime.Paused, "automatic actions can pause"); runtime.TogglePause();
            next = store.Current.Clone(); next.Companion.ProactiveUseModel = true; store.Save(next);
            await runtime.ProactiveNowAsync();
            runtime.ClearMemory();
            await runtime.ProactiveNowAsync();
            Check(!userPayloads.Last().Contains("奶蛙收到啦。", StringComparison.Ordinal), "clearing memory also clears proactive context");
            using (var external = new Naifrog.Chat.ChatService(new SettingsStore(store.DataDirectory), new Handler((_, _) => throw new Exception("No external request expected")))) external.ClearMemory();
            await runtime.ProactiveNowAsync();
            Check(!userPayloads.Last().Contains("奶蛙收到啦。", StringComparison.Ordinal), "another instance clearing memory invalidates proactive derived cache");
            next = store.Current.Clone(); next.Model.ContextPairs = 0; store.Save(next);
            await runtime.ProactiveNowAsync();
            Check(!userPayloads.Last().Contains("奶蛙收到啦。", StringComparison.Ordinal), "zero context excludes previous proactive messages");
            next = store.Current.Clone(); next.Data.RememberChat = false; store.Save(next);
            await runtime.ProactiveNowAsync();
            Check(!userPayloads.Last().Contains("奶蛙收到啦。", StringComparison.Ordinal), "disabled memory excludes proactive history");
            var original = store.Current.Clone(); var changedElsewhere = original.Clone(); var editedDraft = original.Clone();
            changedElsewhere.Files.OrganizerSource = Path.Combine(root, "external-default"); editedDraft.Appearance.Height = 410;
            var merged = SettingsWindow.MergeChanges(changedElsewhere, original, editedDraft);
            Check(merged.Files.OrganizerSource == changedElsewhere.Files.OrganizerSource && merged.Appearance.Height == 410, "settings draft preserves newer unrelated organizer defaults");
            using var varCheck = new TemporaryWindow(new SettingsWindow(runtime));
            varCheck.Window.Show(); await Task.Delay(70);
            var nodes = Descendants(varCheck.Window).ToArray();
            var navigation = nodes.OfType<ListBox>().Single();
            Check(navigation.Items.Count == 8, "settings has eight organized sections after removing desktop organization");
            navigation.SelectedIndex = 2; await Task.Delay(50);
            nodes = Descendants(varCheck.Window).ToArray();
            foreach (string label in new[] { "API 地址", "模型名称 / Model ID", "API Key", "聊天系统提示词" })
                Check(nodes.Any(n => AutomationProperties.GetName(n) == label), "model configuration contains " + label);
            var systemInput = nodes.OfType<TextBox>().Single(n => AutomationProperties.GetName(n) == "聊天系统提示词");
            systemInput.Text = "界面自定义提示词保存检查";
            nodes.OfType<Button>().Single(b => Equals(b.Content, "保存并应用")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(50);
            nodes = Descendants(varCheck.Window).ToArray();
            Check(store.Current.Model.SystemPrompt == "界面自定义提示词保存检查" && nodes.OfType<TextBlock>().Any(n => n.Text == "已保存并应用。") && nodes.Any(n => AutomationProperties.GetName(n) == "聊天系统提示词"), "settings save refreshes pages without reparenting controls");
            Check(PetRuntime.IsQuiet(new DateTime(2026, 10, 29, 1, 0, 0), 23, 8) && !PetRuntime.IsQuiet(new DateTime(2026, 10, 29, 12, 0, 0), 23, 8), "overnight quiet period wraps midnight");
            Check(!new SettingsStore(store.DataDirectory).Current.Privacy.ScreenEnabled, "screen authorization does not survive restart");
            string preferences = File.ReadAllText(Path.Combine(store.DataDirectory, "settings.json"));
            Check(!preferences.Contains("integration-secret"), "preferences do not contain plaintext credentials");
        }
        finally { pet.Close(); }
    }
    private static string FindProjectRoot()
    { var dir = new DirectoryInfo(Environment.CurrentDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src/Naifrog/Naifrog.csproj"))) dir = dir.Parent; return dir?.FullName ?? throw new Exception("Cannot find project"); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Run(Task task)
    {
        var frame = new DispatcherFrame(); task.ContinueWith(_ => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private sealed class TemporaryWindow : IDisposable
    { public Window Window { get; } public TemporaryWindow(Window window) { Window = window; } public void Dispose() => Window.Close(); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
