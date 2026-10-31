using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Naifrog;
using Naifrog.Core;
using Naifrog.Desktop;
using Naifrog.UI;
using Naifrog.Agent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.Install();
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Naifrog-agent-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            var config = store.Current.Clone(); config.Companion.Greeting = false; config.Appearance.UseAnimationPack = false;
            store.Save(config);
            using var runtime = new PetRuntime(store, startTimers: false, configureDesktop: false);
            var pet = new PetWindow(runtime); pet.Show(); pet.ShowSettings(); pet.ShowAgent();
            var settings = app.Windows.OfType<SettingsWindow>().Single();
            Check(Descendants(settings).OfType<ListBoxItem>().Any(x => Equals(x.Content, "工程 Agent")), "settings Agent category");
            Check(!Descendants(settings).OfType<ListBoxItem>().Any(x => Equals(x.Content, "文件与整理")), "desktop organization category removed");
            Check(!pet.AllowDrop && typeof(PetRuntime).GetMethod("ReceiveAsync") is null, "pet no longer receives dropped files");
            Check(typeof(PetRuntime).GetMethod("UpdatePeers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null, "peer interaction runtime removed");
            Check(typeof(PetRuntime).Assembly.GetType("Naifrog.Files.OrganizerWindow") is null, "desktop organizer is not compiled into application");
            var window = app.Windows.OfType<AgentWindow>().Single();
            window.UpdateLayout();
            Check(!window.Topmost && window.Owner is null, "workbench is independent of the topmost pet");
            var changed = store.Current.Clone(); changed.Appearance.Topmost = true; store.Save(changed); pet.ApplySettings();
            Check(!window.Topmost && window.Owner is null, "settings updates cannot make workbench topmost");
            Check(Descendants(window).OfType<TextBox>().Any(x => AutomationProperties.GetName(x) == "工作区目录"), "native workspace picker field");
            Check(Descendants(window).OfType<TextBox>().Any(x => AutomationProperties.GetName(x) == "工程任务输入"), "native task composer");
            var permissions = Descendants(window).OfType<Expander>().Single(x => Equals(x.Header, "权限"));
            permissions.IsExpanded = true; window.UpdateLayout();
            Check(Descendants(window).OfType<ComboBox>().Any(x => AutomationProperties.GetName(x) == "终端命令策略"), "command permission control");
            Check(pet.ContextMenu!.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "工程工作台")), "pet right-click directly opens workbench");
            Check(!Descendants(window).OfType<TabItem>().Any(x => Equals(x.Header, "执行过程与命令输出")), "conversation replaces raw log tabs");
            Check(Descendants(window).OfType<Image>().Any(x => AutomationProperties.GetName(x) == "奶蛙工作台图标"), "brand uses original frog artwork");
            var view = new AgentEventView(); var fixtureWindow = new Window { Content = view, Width = 700, Height = 500 }; AgentUi.Install(fixtureWindow); fixtureWindow.Show();
            var at = DateTimeOffset.Now;
            view.Render("fixture", new[] { new AgentEvent(at, "user", "old message"), new AgentEvent(at.AddTicks(1), "assistant_stream", "streaming", "reply") }); fixtureWindow.UpdateLayout();
            view.Render("fixture", new[] { new AgentEvent(at.AddTicks(1), "assistant", "final text", "reply") }); fixtureWindow.UpdateLayout();
            Check(!Descendants(view).OfType<TextBox>().Any(x => x.Text == "old message") && Descendants(view).OfType<TextBox>().Count(x => x.Text == "final text") == 1, "event ring removal and streaming replacement do not duplicate cards");
            view.Render("fixture", new[] { new AgentEvent(at, "tool_call", "run_command\n{\"command\":\"compile\"}", "tool", "run_command"), new AgentEvent(at.AddSeconds(1), "tool_result", "run_command\n{broken", "tool", "run_command") }); fixtureWindow.UpdateLayout();
            Check(!Descendants(view).OfType<TextBlock>().Any(x => x.Text.StartsWith("已完成")), "malformed command output never claims successful completion");
            view.Render("references", new[] { new AgentEvent(at, "reference_pending", "reference text", "ref") }); fixtureWindow.UpdateLayout();
            Check(Descendants(view).OfType<TextBlock>().Any(x => x.Text == "补充引用 · 待引用"), "pending reference is visible");
            view.Render("references", new[] { new AgentEvent(at, "reference", "reference text", "ref") }); fixtureWindow.UpdateLayout();
            Check(!Descendants(view).OfType<TextBlock>().Any(x => x.Text == "补充引用 · 待引用") && Descendants(view).OfType<TextBox>().Count(x => x.Text == "reference text") == 1, "reference admission updates one card");
            Check(Descendants(window).OfType<ComboBox>().Any(x => AutomationProperties.GetName(x) == "工作台模型"), "workbench has model picker");
            var diff = new FileChangesView(); fixtureWindow.Content = diff;
            int fileCount = -1, added = -1, removed = -1;
            diff.SummaryChanged += (count, plus, minus) => { fileCount = count; added = plus; removed = minus; };
            diff.SetChanges(new[] { new AgentFileChange("code.cs", "old\nkeep\n", "new\nkeep\nextra\n", "before", "after") });
            PumpUntil(() => fileCount >= 0); fixtureWindow.UpdateLayout();
            Check(fileCount == 1 && added == 2 && removed == 1, "diff summary counts files and inserted/deleted lines");
            var colored = Descendants(diff).OfType<TextBlock>().ToArray();
            Check(colored.Any(x => x.Foreground == FileChangesView.AddedColor) && colored.Any(x => x.Foreground == FileChangesView.RemovedColor), "diff content has green additions and red removals");
            Check(Descendants(diff).OfType<ListBoxItem>().Any(x => AutomationProperties.GetName(x) == "code.cs"), "changed file name is selectable"); diff.Dispose();
            foreach (Window item in app.Windows.Cast<Window>().ToArray()) item.Close();
            Console.WriteLine("PASS: Agent native UI and settings checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static void PumpUntil(Func<bool> ready)
    {
        var frame = new System.Windows.Threading.DispatcherFrame(); var until = DateTime.UtcNow.AddSeconds(5);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > until) frame.Continue = false; }; timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        if (!ready()) throw new TimeoutException("WPF background operation did not complete");
    }
}
