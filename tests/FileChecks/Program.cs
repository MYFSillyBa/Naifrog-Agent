using Naifrog.Core;
using Naifrog.Files;
using System.Text.Json;

var sandbox = Path.Combine(Path.GetTempPath(), "NaifrogFileChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var passed = 0;
try
{
    async Task Check(string name, Func<string, Task> test)
    {
        var directory = Path.Combine(sandbox, (++passed).ToString());
        Directory.CreateDirectory(directory);
        await test(directory);
        Console.WriteLine("PASS " + name);
    }
    static void Assert(bool condition, string detail)
    { if (!condition) throw new Exception(detail); }
    static string FileIn(string root, string name, string content = "original file")
    { Directory.CreateDirectory(root); var path = Path.Combine(root, name); File.WriteAllText(path, content); return path; }
    static SettingsStore Store(string root) => new(Path.Combine(root, "data"));
    static OrganizerJournal ReadJournal(string path) => JsonSerializer.Deserialize<OrganizerJournal>(
        JsonSerializer.Deserialize<JournalEnvelope>(File.ReadAllText(path))!.Payload)!;
    static void WriteCrashSnapshot(string path, OrganizerJournal journal)
    {
        // Model a durable pre-crash checkpoint using only this sandbox's own signing key.
        var key = LocalVault.Unprotect(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "journal-key.dpapi")));
        var payload = JsonSerializer.Serialize(journal);
        var signature = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(payload)));
        File.WriteAllText(path, JsonSerializer.Serialize(new JournalEnvelope { Payload = payload, Signature = signature }));
    }
    static async Task<(OrganizerEngine Engine, OrganizerItem Item, string Source, string Target)> Setup(string root)
    {
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        FileIn(source, "note.txt");
        var engine = new OrganizerEngine(Store(root));
        var scan = await engine.ScanAsync(source, CancellationToken.None);
        return (engine, scan.Items.Single(), source, target);
    }
    await Check("copy keeps source and protects existing destination; undo only its own copy", async root =>
    {
        var (engine, item, source, target) = await Setup(root);
        item.Category = "文档";
        var existing = FileIn(Path.Combine(target, "文档"), "note.txt", "keep me");
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        Assert(result.Completed == 1 && File.ReadAllText(existing) == "keep me", result.Summary);
        Assert(File.Exists(Path.Combine(source, "note.txt")), "copy deleted source");
        Assert(Directory.GetFiles(Path.Combine(target, "文档")).Length == 2, "collision was overwritten");
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && Directory.GetFiles(Path.Combine(target, "文档")).Length == 1, undo.Summary);
    });
    await Check("move verifies contents and undo restores source", async root =>
    {
        var (engine, item, source, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Move, CancellationToken.None);
        Assert(result.Completed == 1 && !File.Exists(item.SourcePath), result.Summary);
        Assert(Directory.Exists(source), "source folder was removed");
        var recovered = new OrganizerEngine(Store(root));
        var undo = await recovered.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && File.ReadAllText(item.SourcePath) == "original file", undo.Summary);
    });
    await Check("undo refuses modified destination", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        var destination = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Single();
        File.WriteAllText(destination, "user edit");
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Failed == 1 && File.ReadAllText(destination) == "user edit", undo.Summary);
    });
    await Check("move undo refuses occupied source", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Move, CancellationToken.None);
        File.WriteAllText(item.SourcePath, "new source");
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Failed == 1 && File.ReadAllText(item.SourcePath) == "new source", undo.Summary);
        Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 1, "undo lost original contents");
    });
    await Check("cancel preserves completed journal and supports undo", async root =>
    {
        var (engine, _, source, target) = await Setup(root);
        FileIn(source, "second.txt");
        var items = (await engine.ScanAsync(source, CancellationToken.None)).Items;
        using var cancellation = new CancellationTokenSource();
        var result = await engine.ExecuteAsync(items, target, OrganizationMode.Copy, cancellation.Token,
            progress => { if (progress.Completed == 1) cancellation.Cancel(); });
        Assert(result.Cancelled && result.Completed == 1, result.Summary);
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && items.All(x => File.Exists(x.SourcePath)), undo.Summary);
    });
    await Check("pre-cancel produces no destination writes", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Move, cancellation.Token);
        Assert(result.Cancelled && File.Exists(item.SourcePath) && !Directory.Exists(target), result.Summary);
    });
    await Check("directories refused for copy, allowed as references, reference undo checks hash", async root =>
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "folder"));
        var engine = new OrganizerEngine(Store(root));
        var item = (await engine.ScanAsync(source, CancellationToken.None)).Items.Single();
        var target = Path.Combine(root, "target");
        var copy = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        Assert(copy.Failed == 1 && Directory.Exists(item.SourcePath), copy.Summary);
        var reference = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Reference, CancellationToken.None);
        Assert(reference.Completed == 1, reference.Summary);
        var index = Directory.GetFiles(target, "*.json", SearchOption.AllDirectories).Single();
        Assert(File.ReadAllText(index).Contains("folder"), "reference lacks original path");
        File.WriteAllText(index, "user edit");
        Assert((await engine.UndoAsync(reference.JournalPath!, CancellationToken.None)).Failed == 1, "modified reference removed");
        Assert(File.Exists(index), "modified reference was deleted");
    });
    await Check("symbolic links and reparse ancestors refused", async root =>
    {
        var (engine, item, source, target) = await Setup(root);
        var link = Path.Combine(source, "alias.txt");
        // Junction creation does not need the symbolic-link privilege on Windows.
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        NativeTestFiles.Junction(Path.Combine(source, "linked-folder"), outside);
        var scan = await engine.ScanAsync(source, CancellationToken.None);
        Assert(scan.Items.Count == 1 && scan.Skipped > 0, "reparse link appeared as regular item");
        var targetLink = Path.Combine(root, "target-link"); Directory.CreateDirectory(target);
        NativeTestFiles.Junction(targetLink, target);
        var result = await engine.ExecuteAsync(new[] { item }, targetLink, OrganizationMode.Copy, CancellationToken.None);
        Assert(result.Failed == 1 && Directory.GetFiles(target).Length == 0, result.Summary);
    });
    await Check("AI rejects unknown IDs and traversal categories atomically", async root =>
    {
        var (_, item, _, _) = await Setup(root); var original = item.Category;
        var invalid = JsonSerializer.Serialize(new[] { new { id = item.Id, category = "../escape" } });
        try { OrganizerEngine.ApplyAiCategories(new[] { item }, invalid); throw new Exception("unsafe category accepted"); }
        catch (ArgumentException) { }
        Assert(item.Category == original, "AI validation partially changed preview");
        try { OrganizerEngine.ApplyAiCategories(new[] { item }, "[{\"id\":\"unknown\",\"category\":\"文档\"}]"); throw new Exception("unknown id accepted"); }
        catch (ArgumentException) { }
        OrganizerEngine.ApplyAiCategories(new[] { item }, JsonSerializer.Serialize(new[] { new { id = item.Id, category = "新文档" } }));
        Assert(item.Category == "新文档", "valid AI classification rejected");
    });
    await Check("receiving defaults to copy with collisions and move journal", async root =>
    {
        var store = Store(root); var source = FileIn(Path.Combine(root, "source"), "note.txt");
        var service = new FileOrganizerService(store);
        await service.ReceiveAsync(new[] { source }, CancellationToken.None);
        await service.ReceiveAsync(new[] { source }, CancellationToken.None);
        var received = Path.Combine(store.DataDirectory, "ReceivedFiles");
        Assert(File.Exists(source) && Directory.GetFiles(received).Length == 2, "receiving overwrote or deleted");
        var settings = store.Current.Clone(); settings.Files.ReceiveMode = "Move"; store.Save(settings);
        await service.ReceiveAsync(new[] { source }, CancellationToken.None);
        Assert(!File.Exists(source) && Directory.GetFiles(received).Length == 3, "receive move failed");
        Assert(new OrganizerEngine(store).ListJournals().Count == 3, "receive journals missing");
    });
    await Check("corrupt and external journals fail closed", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        File.WriteAllText(result.JournalPath!, "{ broken");
        try { await engine.UndoAsync(result.JournalPath!, CancellationToken.None); throw new Exception("corrupt journal accepted"); }
        catch (InvalidDataException) { }
        var external = FileIn(root, "external.json", "{}");
        try { await engine.UndoAsync(external, CancellationToken.None); throw new Exception("external journal accepted"); }
        catch (InvalidDataException) { }
        Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 1, "invalid recovery touched files");
    });
    await Check("valid JSON journal tampering is refused", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        var text = File.ReadAllText(result.JournalPath!);
        File.WriteAllText(result.JournalPath!, text.Replace("Completed", "Prepared", StringComparison.Ordinal));
        try { await engine.UndoAsync(result.JournalPath!, CancellationToken.None); throw new Exception("unsigned modification accepted"); }
        catch (InvalidDataException) { }
        Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 1, "tampered log removed user file");
    });
    await Check("incomplete move with original still present removes only verified duplicate", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        var journal = ReadJournal(result.JournalPath!); journal.Mode = OrganizationMode.Move; journal.Finished = false;
        journal.Entries.Single().State = "DestinationCommitted";
        WriteCrashSnapshot(result.JournalPath!, journal);
        var undo = await new OrganizerEngine(Store(root)).UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && File.ReadAllText(item.SourcePath) == "original file", undo.Summary);
        Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 0, "duplicate remained");
    });
    await Check("same-content replacement identity is protected during crash recovery", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        var journal = ReadJournal(result.JournalPath!); var entry = journal.Entries.Single();
        var replacement = FileIn(root, "replacement.txt");
        File.Delete(entry.Destination); File.Move(replacement, entry.Destination);
        entry.State = "Prepared"; journal.Finished = false; WriteCrashSnapshot(result.JournalPath!, journal);
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Failed == 1 && File.ReadAllText(entry.Destination) == "original file", undo.Summary);
    });
    await Check("unverified partial staging is preserved for manual recovery", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Copy, CancellationToken.None);
        var journal = ReadJournal(result.JournalPath!); var entry = journal.Entries.Single();
        File.Move(entry.Destination, entry.Staging); File.WriteAllText(entry.Staging, "partial");
        entry.State = "Intended"; entry.DestinationIdentity = ""; journal.Finished = false;
        WriteCrashSnapshot(result.JournalPath!, journal);
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Failed == 1 && File.ReadAllText(entry.Staging) == "partial" && File.Exists(item.SourcePath), undo.Summary);
    });
    await Check("interrupted undo resumes verified restoration staging", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Move, CancellationToken.None);
        var journal = ReadJournal(result.JournalPath!); var entry = journal.Entries.Single();
        entry.RestoreStaging = Path.Combine(entry.SourceRoot, ".naifrog-undo-resume.partial");
        File.Copy(entry.Destination, entry.RestoreStaging);
        using (var restored = FileSafety.OpenLocked(entry.RestoreStaging, false)) entry.RestoreIdentity = FileSafety.Identity(restored);
        entry.State = "UndoPrepared"; WriteCrashSnapshot(result.JournalPath!, journal);
        var undo = await new OrganizerEngine(Store(root)).UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && File.ReadAllText(item.SourcePath) == "original file", undo.Summary);
        Assert(!File.Exists(entry.Destination) && !File.Exists(entry.RestoreStaging), "undo did not finish its existing staging file");
    });
    await Check("interrupted undo preserves unverified restoration staging", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Move, CancellationToken.None);
        var journal = ReadJournal(result.JournalPath!); var entry = journal.Entries.Single();
        entry.RestoreStaging = FileIn(entry.SourceRoot, ".naifrog-undo-interrupted.partial", "partial");
        entry.State = "UndoPreparing"; WriteCrashSnapshot(result.JournalPath!, journal);
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Failed == 1 && File.ReadAllText(entry.RestoreStaging) == "partial" && File.Exists(entry.Destination), undo.Summary);
        Assert(!File.Exists(item.SourcePath), "unverified partial contents restored");
    });
    await Check("shortcut creation and undo preserve original", async root =>
    {
        var (engine, item, _, target) = await Setup(root);
        var result = await engine.ExecuteAsync(new[] { item }, target, OrganizationMode.Shortcut, CancellationToken.None);
        Assert(result.Completed == 1 && Directory.GetFiles(target, "*.lnk", SearchOption.AllDirectories).Length == 1, result.Summary);
        var undo = await engine.UndoAsync(result.JournalPath!, CancellationToken.None);
        Assert(undo.Completed == 1 && File.Exists(item.SourcePath), undo.Summary);
    });
    Console.WriteLine($"All {passed} file checks passed in temporary directories.");
}
finally
{
    // Only the uniquely named directory created by this check is ever removed.
    if (Path.GetFileName(sandbox).StartsWith("NaifrogFileChecks-", StringComparison.Ordinal) &&
        Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
    {
        NativeTestFiles.RemoveCreatedJunctions(sandbox);
        Directory.Delete(sandbox, true);
    }
}
