using Naifrog.Agent;
int passed = 0;
void Check(string? before, string? after, int added, int removed)
{
    var result = LineDiff.Compare(before, after);
    if (result.Added != added || result.Removed != removed) throw new Exception($"Expected +{added} -{removed}, got +{result.Added} -{result.Removed}");
    string[] original = result.Lines.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text).ToArray();
    string[] updated = result.Lines.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text).ToArray();
    static string Normal(string? s) => (s ?? "").Replace("\r\n", "\n").TrimEnd('\n');
    if (string.Join("\n", original).TrimEnd('\n') != Normal(before) || string.Join("\n", updated).TrimEnd('\n') != Normal(after)) throw new Exception("Diff cannot reconstruct inputs");
    passed++;
}
Check(null, "a\nb\n", 2, 0); Check("a\nb\n", null, 0, 2); Check("", "", 0, 0);
Check("a\nb\nc\n", "a\nB\nc\n", 1, 1); Check("a\nb", "a\nx\nb", 1, 0); Check("a\nx\nb", "a\nb", 0, 1);
Check("a\r\nb\r\n", "a\nb\n", 0, 0); Check("a\n\nb\n", "a\nb\n", 0, 1);
Check("猫\n蛙\n", "猫\n奶蛙\n", 1, 1); Check("a\nb\na", "b\na\nb", 1, 1);
var many = string.Join("\n", Enumerable.Range(0, 20000)); Check(many, many.Replace("\n10000\n", "\nchanged\n"), 1, 1);
Check(string.Join("\n", Enumerable.Repeat("old", 3000)), string.Join("\n", Enumerable.Repeat("new", 3000)), 3000, 3000);
Console.WriteLine($"PASS {passed} diff checks");
