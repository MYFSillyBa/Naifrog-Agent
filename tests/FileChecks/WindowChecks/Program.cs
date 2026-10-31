using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Naifrog.Core;
using Naifrog.Files;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "NaifrogWindowChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        Exception? failure = null;
        try
        {
            var source = Path.Combine(sandbox, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "阅读笔记.txt"), "The UI must never send this content.");
            var target = Path.Combine(sandbox, "target");
            var store = new SettingsStore(Path.Combine(sandbox, "data"));
            var settings = store.Current.Clone();
            settings.Files.OrganizerSource = source; settings.Files.OrganizerTarget = target; settings.Files.UseAiClassification = true;
            settings.Model.BaseUrl = "http://localhost:9999"; settings.Model.ModelId = "test-only";
            store.Save(settings);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            async Task<string> Completion(string system, string user, CancellationToken token)
            {
                calls++;
                using var payload = JsonDocument.Parse(user);
                if (payload.RootElement.EnumerateArray().Any(x => x.EnumerateObject().Any(p => p.Name is not ("id" or "name"))))
                    throw new Exception("AI request contained fields other than ID/name.");
                if (user.Contains(source, StringComparison.Ordinal) || user.Contains("must never send", StringComparison.Ordinal))
                    throw new Exception("AI request leaked a full path or file contents.");
                requested.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return "[]";
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new OrganizerWindow(store, Completion);
            window.Show();
            app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(60);
                    var buttons = Descendants(window).OfType<Button>().ToArray();
                    var scan = buttons.Single(x => Equals(x.Content, "扫描并预览"));
                    var ai = buttons.Single(x => Equals(x.Content, "AI 细化"));
                    var grid = Descendants(window).OfType<DataGrid>().Single();
                    if (grid.Items.Count != 0 || calls != 0 || Directory.Exists(target)) throw new Exception("Window opening caused work before user action.");
                    scan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => scan.IsEnabled && grid.Items.Count == 1);
                    if (calls != 0 || Directory.Exists(target)) throw new Exception("Scan called AI or wrote target files.");
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "organizer-preview.png"))) encoder.Save(output);
                    if (window.Height > SystemParameters.WorkArea.Height || window.MinHeight > SystemParameters.WorkArea.Height)
                        throw new Exception("Organizer window exceeds the available work area.");
                    window.MinHeight = 0; window.Height = 540;
                    await Task.Delay(60);
                    window.UpdateLayout();
                    var undo = buttons.Single(x => Equals(x.Content, "撤销所选日志"));
                    undo.BringIntoView();
                    await Task.Delay(40);
                    var undoBounds = undo.TransformToAncestor(window).TransformBounds(new Rect(undo.RenderSize));
                    if (undoBounds.Top < 0 || undoBounds.Bottom > window.ActualHeight - 12)
                        throw new Exception("Undo controls are inaccessible at a reduced screen height.");
                    var execute = buttons.Single(x => Equals(x.Content, "执行所选预览"));
                    var tableBounds = grid.TransformToAncestor(window).TransformBounds(new Rect(grid.RenderSize));
                    var executeBounds = execute.TransformToAncestor(window).TransformBounds(new Rect(execute.RenderSize));
                    if (tableBounds.Bottom > executeBounds.Top)
                        throw new Exception("Preview overlaps execution controls at a reduced screen height.");
                    if (!ai.IsEnabled) throw new Exception("AI refinement not available for configured local model.");
                    ai.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await requested.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    window.Close();
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    await Task.Delay(60);
                    if (calls != 1 || Directory.Exists(target)) throw new Exception("Closing during AI caused additional work.");
                    Console.WriteLine("PASS window opens without scanning or writing; scan stays read-only; footer remains accessible at 540px; AI sends names only and is cancelled on close.");
                }
                catch (Exception error) { failure = error; if (window.IsVisible) window.Close(); }
                finally { app.Shutdown(); }
            });
            app.Run();
            if (failure is not null) throw failure;
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(sandbox).StartsWith("NaifrogWindowChecks-", StringComparison.Ordinal)) Directory.Delete(sandbox, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Window did not reach expected state."); await Task.Delay(20); }
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
