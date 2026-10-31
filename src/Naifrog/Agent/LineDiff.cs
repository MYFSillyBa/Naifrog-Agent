using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Naifrog.Agent;

public enum DiffKind { Context, Added, Removed }
public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine);
public sealed record LineDiffResult(IReadOnlyList<DiffLine> Lines, int Added, int Removed, bool Simplified);

public static class LineDiff
{
    public static LineDiffResult Compare(string? before, string? after, CancellationToken token = default)
    {
        static string[] Split(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return lines[^1].Length == 0 ? lines[..^1] : lines;
        }
        var a = Split(before); var b = Split(after); var output = new List<DiffLine>();
        int prefix = 0, suffix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        for (int i = 0; i < prefix; i++) output.Add(new(DiffKind.Context, a[i], i + 1, i + 1));
        int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
        bool simplified = (long)n * m > 2_000_000;
        int old = prefix, next = prefix;
        if (!simplified)
        {
            var lengths = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                for (int j = m - 1; j >= 0; j--) lengths[i, j] = a[prefix + i] == b[prefix + j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                token.ThrowIfCancellationRequested();
                if (x < n && y < m && a[old] == b[next]) { output.Add(new(DiffKind.Context, a[old], ++old, ++next)); x++; y++; }
                else if (x < n && (y == m || lengths[x + 1, y] >= lengths[x, y + 1])) { output.Add(new(DiffKind.Removed, a[old], ++old, null)); x++; }
                else { output.Add(new(DiffKind.Added, b[next], null, ++next)); y++; }
            }
        }
        else
        {
            // Bound memory for large rewritten blocks. Counts correspond to the displayed replacement.
            for (int i = 0; i < n; i++) { token.ThrowIfCancellationRequested(); output.Add(new(DiffKind.Removed, a[old], ++old, null)); }
            for (int i = 0; i < m; i++) { token.ThrowIfCancellationRequested(); output.Add(new(DiffKind.Added, b[next], null, ++next)); }
        }
        for (int i = 0; i < suffix; i++) output.Add(new(DiffKind.Context, a[old], ++old, ++next));
        return new(output.AsReadOnly(), output.Count(l => l.Kind == DiffKind.Added), output.Count(l => l.Kind == DiffKind.Removed), simplified);
    }
}
