using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Naifrog.Agent;

internal sealed class FileChangesView : Grid, IDisposable
{
    public static readonly Brush AddedColor = AgentUi.Color("#16803C"), RemovedColor = AgentUi.Color("#C23C38");
    private readonly TextBlock summary = AgentUi.Text("尚无文件改动", 13), caption = AgentUi.Text("选择文件查看差异", 12, AgentUi.Muted);
    private readonly ListBox files = new() { BorderThickness = new Thickness(0), MaxHeight = 170 }, lines = new() { BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
    private readonly Button undo;
    private string fingerprint = "";
    private int generation;
    private CancellationTokenSource? calculation;
    private bool disposed;
    public event Action? UndoRequested;
    public event Action<int, int, int>? SummaryChanged;
    public bool CanUndo { set => undo.IsEnabled = value; }

    public FileChangesView()
    {
        Background = Brushes.White; Margin = new Thickness(10, 0, 10, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summary.Margin = new Thickness(8, 14, 8, 12); Children.Add(summary); Grid.SetRow(files, 1); Children.Add(files);
        caption.Margin = new Thickness(8, 12, 8, 10); Grid.SetRow(caption, 2); Children.Add(caption); Grid.SetRow(lines, 3); Children.Add(lines);
        var row = new FrameworkElementFactory(typeof(TextBlock)); row.SetBinding(TextBlock.TextProperty, new Binding(nameof(DiffRow.Text))); row.SetBinding(TextBlock.BackgroundProperty, new Binding(nameof(DiffRow.Background))); row.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(DiffRow.Foreground))); row.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas, Microsoft YaHei UI")); row.SetValue(TextBlock.FontSizeProperty, 12d); row.SetValue(TextBlock.PaddingProperty, new Thickness(6, 2, 6, 2)); lines.ItemTemplate = new DataTemplate { VisualTree = row };
        VirtualizingPanel.SetIsVirtualizing(lines, true); VirtualizingPanel.SetVirtualizationMode(lines, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(lines, true);
        AutomationProperties.SetName(files, "改动文件列表"); AutomationProperties.SetName(lines, "文件差异内容");
        var footer = new StackPanel { Margin = new Thickness(8, 10, 8, 12) }; footer.Children.Add(AgentUi.Text("文件工具修改 · Ctrl+C 复制选中差异行", 10, AgentUi.Muted)); undo = AgentUi.Button("撤销文件改动", () => UndoRequested?.Invoke()); undo.Margin = new Thickness(0, 8, 0, 0); footer.Children.Add(undo); Grid.SetRow(footer, 4); Children.Add(footer);
        files.SelectionChanged += (_, _) =>
        {
            if ((files.SelectedItem as ListBoxItem)?.Tag is not Entry entry) { lines.ItemsSource = null; return; }
            caption.Text = entry.Change.Path + (entry.Diff.Simplified ? " · 大段差异按替换展示" : "");
            lines.ItemsSource = entry.Diff.Lines.Select(l => new DiffRow($"{l.OldLine,5} {l.NewLine,5} {(l.Kind == DiffKind.Added ? '+' : l.Kind == DiffKind.Removed ? '-' : ' ')} {l.Text}", l.Kind == DiffKind.Added ? AddedColor : l.Kind == DiffKind.Removed ? RemovedColor : AgentUi.Ink, l.Kind == DiffKind.Added ? AgentUi.Color("#EAF7ED") : l.Kind == DiffKind.Removed ? AgentUi.Color("#FFF0F0") : Brushes.White)).ToArray();
        };
        lines.PreviewKeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && lines.SelectedItems.Count > 0) { Clipboard.SetText(string.Join(Environment.NewLine, lines.SelectedItems.Cast<DiffRow>().Select(l => l.Text))); e.Handled = true; } };
    }

    public async void SetChanges(IReadOnlyList<AgentFileChange> changes)
    {
        string signature = string.Join("\n", changes.Select(c => c.Path + ":" + c.BeforeHash + ":" + c.AfterHash));
        if (disposed || fingerprint == signature) return; fingerprint = signature;
        calculation?.Cancel(); calculation?.Dispose(); var source = calculation = new(); var token = source.Token; int version = ++generation;
        string? selected = ((files.SelectedItem as ListBoxItem)?.Tag as Entry)?.Change.Path;
        try
        {
            var entries = await Task.Run(() => changes.Select(c => new Entry(c, LineDiff.Compare(c.Before, c.After, token))).Where(e => e.Diff.Added > 0 || e.Diff.Removed > 0 || e.Change.Before != e.Change.After).ToArray(), token);
            await Dispatcher.InvokeAsync(() =>
            {
            if (disposed || version != generation) return;
            files.Items.Clear(); int added = entries.Sum(e => e.Diff.Added), removed = entries.Sum(e => e.Diff.Removed);
            summary.Inlines.Clear(); summary.Inlines.Add($"更改了 {entries.Length} 个文件   "); summary.Inlines.Add(new System.Windows.Documents.Run($"+{added}") { Foreground = AddedColor }); summary.Inlines.Add("  "); summary.Inlines.Add(new System.Windows.Documents.Run($"−{removed}") { Foreground = RemovedColor });
            foreach (var entry in entries)
            {
                var label = AgentUi.Text(entry.Change.Path, 12); label.Inlines.Add(new System.Windows.Documents.Run($"   +{entry.Diff.Added}") { Foreground = AddedColor }); label.Inlines.Add(new System.Windows.Documents.Run($"  −{entry.Diff.Removed}") { Foreground = RemovedColor });
                var item = new ListBoxItem { Content = label, Tag = entry, ToolTip = entry.Change.Path, Padding = new Thickness(8, 7, 8, 7) }; AutomationProperties.SetName(item, entry.Change.Path); files.Items.Add(item);
                if (entry.Change.Path == selected) files.SelectedItem = item;
            }
            if (files.SelectedItem is null && files.Items.Count > 0) files.SelectedIndex = 0;
            if (entries.Length == 0) caption.Text = "尚无文件改动";
            SummaryChanged?.Invoke(entries.Length, added, removed);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!disposed && !Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(() => { if (!disposed && version == generation) { caption.Text = "无法生成差异，请查看任务记录。"; fingerprint = ""; } }); }
    }
    private sealed record Entry(AgentFileChange Change, LineDiffResult Diff);
    private sealed record DiffRow(string Text, Brush Foreground, Brush Background);
    public void Dispose() { disposed = true; calculation?.Cancel(); calculation?.Dispose(); }
}
