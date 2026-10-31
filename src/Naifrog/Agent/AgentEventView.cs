using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Naifrog.Agent;

/// <summary>Native conversation projection. Full wire records stay in encrypted history.</summary>
internal sealed class AgentEventView : ScrollViewer
{
    private readonly StackPanel lane = new() { MaxWidth = 900, Margin = new Thickness(28, 24, 28, 24), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, MessageCard> messages = new();
    private readonly Dictionary<string, ToolCard> tools = new();
    private string session = "";
    private readonly TextBlock empty = AgentUi.Text("从一个具体任务开始\n\n选择工程目录，描述你想完成的修改。奶蛙会展示读取、命令和文件改动。", 15, AgentUi.Muted);
    public AgentEventView()
    {
        Content = lane; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        empty.Margin = new Thickness(16, 80, 16, 0); lane.Children.Add(empty);
    }
    public void Render(string id, IReadOnlyList<AgentEvent> events)
    {
        bool follow = ScrollableHeight - VerticalOffset < 45;
        if (session != id) { session = id; messages.Clear(); tools.Clear(); lane.Children.Clear(); follow = true; }
        var liveMessages = new HashSet<string>(); var liveTools = new HashSet<string>();
        string? lastTool = null;
        bool visible = events.Any(e => e.Kind is "user" or "reference_pending" or "reference" or "assistant" or "assistant_stream" or "tool_call" or "tool_result" or "error" or "undo");
        if (visible) lane.Children.Remove(empty); else if (!lane.Children.Contains(empty)) lane.Children.Add(empty);
        for (int i = 0; i < events.Count; i++)
        {
            var entry = events[i];
            string key = entry.Id ?? entry.At.UtcTicks.ToString() + ":" + entry.Kind;
            switch (entry.Kind)
            {
                case "user": case "reference_pending": case "reference": case "assistant": case "assistant_stream": case "error": case "undo":
                    bool reference = entry.Kind is "reference_pending" or "reference";
                    string messageKey = (reference ? "reference:" : entry.Kind is "assistant_stream" or "assistant" ? "assistant:" : entry.Kind + ":") + key;
                    liveMessages.Add(messageKey);
                    if (!messages.TryGetValue(messageKey, out var card)) { card = new MessageCard(entry.Kind == "user" || reference, entry.Kind == "error"); messages.Add(messageKey, card); lane.Children.Add(card); }
                    card.SetReference(reference, entry.Kind == "reference_pending"); card.Update(entry.Text, entry.Kind == "assistant_stream"); break;
                case "tool_call":
                    string toolKey = entry.Id ?? "legacy:" + entry.At.UtcTicks;
                    liveTools.Add(toolKey);
                    lastTool = toolKey;
                    if (!tools.ContainsKey(toolKey)) { var tool = new ToolCard(entry); tools.Add(toolKey, tool); lane.Children.Add(tool); }
                    break;
                case "tool_result":
                    string? resultKey = entry.Id ?? lastTool;
                    if (resultKey is not null && tools.TryGetValue(resultKey, out var found)) { liveTools.Add(resultKey); found.Complete(entry); }
                    break;
            }
        }
        foreach (string key in messages.Keys.Where(k => !liveMessages.Contains(k)).ToArray()) { lane.Children.Remove(messages[key]); messages.Remove(key); }
        foreach (string key in tools.Keys.Where(k => !liveTools.Contains(k)).ToArray()) { lane.Children.Remove(tools[key]); tools.Remove(key); }
        if (follow) Dispatcher.BeginInvoke(() => ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private sealed class MessageCard : Border
    {
        private readonly StackPanel body = new();
        private string previous = ""; private bool wasStreaming;
        private readonly TextBlock label;
        public MessageCard(bool user, bool error)
        {
            Margin = new Thickness(user ? 60 : 0, 0, 0, 22); Padding = new Thickness(user ? 16 : 0, user ? 12 : 0, user ? 16 : 0, user ? 12 : 0);
            Background = user ? AgentUi.Surface : Brushes.Transparent; CornerRadius = new CornerRadius(12); Child = body;
            label = AgentUi.Text(user ? "你" : error ? "任务提示" : "奶蛙", 11, error ? AgentUi.Color("#B34D44") : AgentUi.Muted); label.Margin = new Thickness(0, 0, 0, 7); body.Children.Add(label);
        }
        public void SetReference(bool reference, bool pending)
        {
            if (!reference) return;
            label.Text = pending ? "补充引用 · 待引用" : "补充引用 · 已加入上下文";
            label.Foreground = AgentUi.Blue; Background = AgentUi.Color("#EFF4FE"); BorderBrush = AgentUi.Color("#8DACEF"); BorderThickness = new Thickness(3, 0, 0, 0);
        }
        public void Update(string text, bool streaming)
        {
            if (text == previous && streaming == wasStreaming) return; previous = text; wasStreaming = streaming;
            while (body.Children.Count > 1) body.Children.RemoveAt(1);
            if (streaming)
            { body.Children.Add(Selectable(text)); return; }
            var chunks = text.Replace("\r\n", "\n").Split("```");
            for (int i = 0; i < chunks.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(chunks[i])) continue;
                if (i % 2 == 0) body.Children.Add(Selectable(chunks[i].Trim('\n')));
                else
                {
                    string code = chunks[i]; int newline = code.IndexOf('\n');
                    if (newline >= 0 && newline < 25) code = code[(newline + 1)..];
                    var block = AgentUi.Code(code.TrimEnd('\n')); block.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(block);
                }
            }
        }
        private static TextBox Selectable(string text) => new() { Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, FontSize = 14, AcceptsReturn = true, IsTabStop = false };
    }
    private sealed class ToolCard : Border
    {
        private readonly Expander expander = new();
        private readonly TextBlock title = AgentUi.Text("", 12), state = AgentUi.Text("正在执行", 11, AgentUi.Muted);
        private readonly TextBox output = AgentUi.Code();
        private readonly DateTimeOffset started;
        private string previous = "";
        public ToolCard(AgentEvent entry)
        {
            started = entry.At; Margin = new Thickness(0, 0, 0, 10); Background = AgentUi.Surface; BorderBrush = AgentUi.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(9); Padding = new Thickness(10, 9, 10, 9); Child = expander;
            var header = new StackPanel(); header.Children.Add(title); state.Margin = new Thickness(0, 4, 0, 0); header.Children.Add(state); expander.Header = header;
            int split = entry.Text.IndexOf('\n'); string name = entry.ToolName ?? (split < 0 ? entry.Text : entry.Text[..split]); string args = split < 0 ? "{}" : entry.Text[(split + 1)..];
            var content = new StackPanel(); expander.Content = content;
            try
            {
                using var json = JsonDocument.Parse(args); var root = json.RootElement;
                string Detail(string key) => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                string detail = name == "run_command" ? Detail("command") : name == "search_text" ? Detail("query") : Detail("path");
                string label = name switch { "run_command" => "执行命令", "read_file" => "读取文件", "list_files" => "浏览目录", "search_text" => "搜索工程", "git_status" => "查看 Git 状态", "git_diff" => "查看 Git 差异", "write_file" => "写入文件", "replace_text" => "修改文件", "delete_file" => "删除文件", _ => name };
                string first = detail.Split('\n')[0]; title.Text = label + (first.Length == 0 ? "" : "  ·  " + (first.Length > 85 ? first[..85] + "…" : first));
                if (name == "run_command")
                {
                    var command = AgentUi.Code(detail, 170); content.Children.Add(command);
                    var directory = AgentUi.Text("工作目录：" + (Detail("working_directory") is { Length: > 0 } cwd ? cwd : "."), 11, AgentUi.Muted); directory.Margin = new Thickness(10, 4, 0, 8); content.Children.Add(directory);
                }
            }
            catch (JsonException) { title.Text = name; }
            AutomationProperties.SetName(expander, title.Text);
            content.Children.Add(output);
        }
        public void Complete(AgentEvent entry)
        {
            if (previous == entry.Text) return; previous = entry.Text;
            int split = entry.Text.IndexOf('\n'); string jsonText = split < 0 ? entry.Text : entry.Text[(split + 1)..];
            bool failed = false, valid = true; string suffix = "";
            try
            {
                using var doc = JsonDocument.Parse(jsonText); var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error)) { failed = true; output.Text = error.ToString(); }
                else if (root.TryGetProperty("exit_code", out var exit))
                {
                    failed = exit.GetInt32() != 0 || root.TryGetProperty("timed_out", out var timeout) && timeout.GetBoolean();
                    suffix = " · 退出码 " + exit; output.Text = (root.TryGetProperty("stdout", out var stdout) ? stdout.GetString() : "") + (root.TryGetProperty("stderr", out var stderr) && !string.IsNullOrEmpty(stderr.GetString()) ? "\n" + stderr.GetString() : "");
                    if (root.TryGetProperty("output_truncated", out var truncated) && truncated.GetBoolean()) output.AppendText("\n（输出已截断）");
                }
                else if (root.TryGetProperty("content", out var content)) output.Text = content.GetString() + (root.TryGetProperty("truncated", out var trunc) && trunc.GetBoolean() ? "\n（分段内容，可继续读取）" : "");
                else if (root.TryGetProperty("entries", out var entries)) output.Text = string.Join("\n", entries.EnumerateArray().Select(e => e.TryGetProperty("path", out var path) ? path.GetString() : e.ToString()));
                else if (root.TryGetProperty("matches", out var matches)) output.Text = string.Join("\n", matches.EnumerateArray().Select(e => $"{e.GetProperty("path")}:{e.GetProperty("line")}  {e.GetProperty("text")}"));
                else if (root.TryGetProperty("path", out var path)) output.Text = "已更新 " + path.GetString();
                else output.Text = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { valid = false; output.Text = jsonText; }
            state.Text = (!valid ? "结果无法解析" : failed ? "执行失败" : "已完成") + suffix + $" · {Math.Max(0, (entry.At - started).TotalSeconds):0.0} 秒";
            state.Foreground = failed || !valid ? AgentUi.Color("#B34D44") : AgentUi.Muted;
        }
    }
}
