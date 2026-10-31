using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Naifrog.Core;

namespace Naifrog.Agent;

/// <summary>Scoped text operations. Terminal execution is separately authorized and is not a sandbox.</summary>
public sealed class WorkspaceTools
{
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxOutputChars = 32000;
    private const string Missing = "missing";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly bool allowEdits;
    private readonly Func<string, string, CancellationToken, Task<bool>> approveCommand;
    private int commandTimeoutSeconds;
    private readonly string journalPath;
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object stateGate = new();
    private readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Snapshot(string Path, byte[]? Before, byte[]? After);

    public WorkspaceTools(string workspace, string journalDirectory, bool allowEdits,
        Func<string, string, CancellationToken, Task<bool>> approveCommand, int commandTimeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("Workspace is required.");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("Workspace directory does not exist.");
        RejectReparseAncestors(Root);
        this.allowEdits = allowEdits;
        this.approveCommand = approveCommand ?? throw new ArgumentNullException(nameof(approveCommand));
        this.commandTimeoutSeconds = Math.Clamp(commandTimeoutSeconds, 1, 1800);
        journalPath = Path.Combine(Path.GetFullPath(journalDirectory), "workspace-" + Guid.NewGuid().ToString("N") + ".dpapi");
    }

    public string Root { get; }
    public int CommandTimeoutSeconds { get => commandTimeoutSeconds; set => commandTimeoutSeconds = Math.Clamp(value, 1, 1800); }
    public IReadOnlyList<AgentFileChange> Changes
    {
        get
        {
            lock (stateGate) return snapshots.Values.Select(s => new AgentFileChange(s.Path,
                Preview(s.Before), Preview(s.After), Hash(s.Before), Hash(s.After))).ToArray();
        }
    }

    public JsonArray Definitions => new(
        Definition("list_files", "List workspace entries; links omitted. Maximum 1000 entries.",
            Props(("path", "string", "Relative directory, default '.'"), ("recursive", "boolean", "Recurse, default false"))),
        Definition("read_file", "Read UTF-8 text, maximum 1 MiB. sha256 includes BOM. Excerpts are bounded.",
            Props(("path", "string", "Relative file"), ("start_line", "integer", "First line, one-based, default 1"), ("max_lines", "integer", "Default 400, maximum 2000")), "path"),
        Definition("search_text", "Find literal text in UTF-8 files. Skips unsupported encoding, binary, oversized files and links. Bounded traversal.",
            Props(("query", "string", "Nonempty literal text"), ("path", "string", "Relative file or directory, default '.'"), ("case_sensitive", "boolean", "Default false"), ("max_results", "integer", "Default 100, maximum 200")), "query"),
        Definition("write_file", "Create/overwrite UTF-8 text, preserving existing BOM. expected_hash must equal latest sha256, or 'missing' to create. Creates parents.",
            Props(("path", "string", "Relative file"), ("content", "string", "Complete new text"), ("expected_hash", "string", "Latest sha256 or 'missing'")), "path", "content", "expected_hash"),
        Definition("replace_text", "Replace one unique exact occurrence, preserving BOM. Empty/ambiguous old_text rejected. Reread on hash conflict.",
            Props(("path", "string", "Relative file"), ("old_text", "string", "Unique exact text"), ("new_text", "string", "Replacement"), ("expected_hash", "string", "Latest sha256")), "path", "old_text", "new_text", "expected_hash"),
        Definition("delete_file", "Delete UTF-8 text after latest sha256 check. Snapshot retained for conservative undo.",
            Props(("path", "string", "Relative file"), ("expected_hash", "string", "Latest sha256")), "path", "expected_hash"),
        Definition("run_command", "Run PowerShell after user authorization. NOT sandboxed: may access outside workspace. A UTF-8 output setup prefix is included in the approval. Output bounded; process tree killed on timeout/cancel. Append 'exit $LASTEXITCODE' after native build/test tools to preserve their exact exit code.",
            Props(("command", "string", "Exact PowerShell command shown to user"), ("working_directory", "string", "Relative subdirectory, default '.'")), "command"),
        Definition("git_status", "Read Git status using git.exe, without shell or fsmonitor.",
            Props(("working_directory", "string", "Relative subdirectory, default '.'"))),
        Definition("git_diff", "Read Git diff with external diff/textconv disabled. Terminal changes are not included in undo snapshots.",
            Props(("working_directory", "string", "Relative subdirectory, default '.'"), ("path", "string", "Optional relative file"), ("staged", "boolean", "Staged diff, default false"))));

    private static JsonObject Props(params (string Name, string Type, string Description)[] fields)
    {
        var result = new JsonObject();
        foreach (var f in fields) result[f.Name] = new JsonObject { ["type"] = f.Type, ["description"] = f.Description };
        return result;
    }
    private static JsonObject Definition(string name, string description, JsonObject properties, params string[] required)
        => new() { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["additionalProperties"] = false } } };

    public async Task<string> ExecuteAsync(AgentToolCall call, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(call.Arguments);
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
            return call.Name switch
            {
                "list_files" => ListFiles(args, ct),
                "read_file" => ReadFile(args),
                "search_text" => SearchText(args, ct),
                "write_file" or "replace_text" or "delete_file" => await MutateAsync(call.Name, args, ct).ConfigureAwait(false),
                "run_command" => await RunCommandAsync(args, ct).ConfigureAwait(false),
                "git_status" or "git_diff" => await GitAsync(call.Name, args, ct).ConfigureAwait(false),
                _ => Error("Unknown tool: " + call.Name)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private string ListFiles(JsonElement args, CancellationToken ct)
    {
        string directory = Resolve(Text(args, "path", "."));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Directory not found.");
        var files = new List<object>();
        bool truncated = false;
        foreach (var entry in Walk(directory, Flag(args, "recursive"), ct))
        {
            if (files.Count == 1000) { truncated = true; break; }
            files.Add(new { path = Relative(entry), type = Directory.Exists(entry) ? "directory" : "file" });
        }
        return Json(new { entries = files, truncated });
    }

    private string ReadFile(JsonElement args)
    {
        string path = Resolve(Text(args, "path"));
        byte[] bytes = ReadBytes(path);
        string text = Decode(bytes);
        int start = Math.Clamp(Number(args, "start_line", 1), 1, int.MaxValue);
        int limit = Math.Clamp(Number(args, "max_lines", 400), 1, 2000);
        var lines = text.Split('\n');
        string content = start == 1 && lines.Length <= limit && text.Length <= MaxOutputChars
            ? text : string.Join("\n", lines.Skip(start - 1).Take(limit));
        bool truncated = start > 1 || lines.Length > limit || content.Length > MaxOutputChars;
        if (content.Length > MaxOutputChars) content = content[..MaxOutputChars];
        return Json(new { path = Relative(path), content, sha256 = Hash(bytes), start_line = start, total_lines = lines.Length, truncated });
    }

    private string SearchText(JsonElement args, CancellationToken ct)
    {
        string query = Text(args, "query");
        if (query.Length == 0 || query.Length > 4000) throw new ArgumentException("query must have 1 to 4000 characters.");
        string path = Resolve(Text(args, "path", "."));
        int limit = Math.Clamp(Number(args, "max_results", 100), 1, 200);
        var comparison = Flag(args, "case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<object>();
        int examined = 0, skipped = 0, outputChars = 0;
        bool truncated = false;
        IEnumerable<string> candidates = Directory.Exists(path) ? Walk(path, true, ct) : new[] { path };
        foreach (string file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(file)) continue;
            if (++examined > 2000) { truncated = true; break; }
            string content;
            try { content = Decode(ReadBytes(Resolve(Relative(file)))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; continue; }
            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, comparison)) continue;
                if (matches.Count >= limit || outputChars >= MaxOutputChars) { truncated = true; break; }
                string excerpt = lines[i].TrimEnd('\r');
                if (excerpt.Length > 400) excerpt = excerpt[..400];
                outputChars += excerpt.Length;
                matches.Add(new { path = Relative(file), line = i + 1, text = excerpt });
            }
            if (truncated) break;
        }
        return Json(new { matches, examined_files = examined, skipped_files = skipped, truncated });
    }

    private async Task<string> MutateAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!allowEdits) throw new UnauthorizedAccessException("File editing is disabled for this task.");
        await mutations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            string path = Resolve(Text(args, "path"));
            if (Directory.Exists(path)) throw new IOException("Path is a directory.");
            byte[]? before = File.Exists(path) ? ReadBytes(path) : null;
            if (before is not null) _ = Decode(before);
            string expected = Text(args, "expected_hash");
            RequireHash(before, expected);
            byte[]? after;
            if (name == "delete_file")
            {
                if (before is null) throw new FileNotFoundException("File not found.");
                after = null;
            }
            else
            {
                string content;
                if (name == "replace_text")
                {
                    if (before is null) throw new FileNotFoundException("File not found.");
                    string old = Text(args, "old_text"), replacement = Text(args, "new_text");
                    if (old.Length == 0) throw new ArgumentException("old_text cannot be empty.");
                    string current = Decode(before);
                    int index = current.IndexOf(old, StringComparison.Ordinal);
                    if (index < 0 || current.IndexOf(old, index + 1, StringComparison.Ordinal) >= 0)
                        throw new ArgumentException("old_text must match exactly once.");
                    content = current[..index] + replacement + current[(index + old.Length)..];
                }
                else content = Text(args, "content");
                if (content.Contains('\0')) throw new ArgumentException("Binary content is not supported.");
                after = Encode(content, before);
                _ = Decode(after);
                if (after.Length > MaxFileBytes) throw new IOException("Text exceeds the 1 MiB limit.");
            }
            string relative = Relative(path);
            Snapshot next;
            lock (stateGate) next = new Snapshot(relative, snapshots.TryGetValue(relative, out var previous) ? previous.Before : before, after);
            PersistJournal(next);
            bool touched = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                ApplyBytes(path, after, expected, () => touched = true);
                lock (stateGate) snapshots[relative] = next;
            }
            catch
            {
                // Preserve the original bytes even if a disk error interrupted an in-place write.
                // The actual partial result permits conservative undo instead of losing its journal.
                if (touched)
                {
                    byte[]? actual = next.After;
                    try { actual = File.Exists(path) ? ReadBytes(path) : null; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    lock (stateGate) snapshots[relative] = next with { After = actual };
                }
                PersistJournal(); throw;
            }
            return Json(new { path = relative, sha256 = Hash(after), deleted = after is null });
        }
        finally { mutations.Release(); }
    }

    public async Task<string> UndoAsync(CancellationToken ct)
    {
        await mutations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var restored = new List<string>();
            var conflicts = new List<object>();
            Snapshot[] pending;
            lock (stateGate) pending = snapshots.Values.Reverse().ToArray();
            foreach (var snapshot in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string path = Resolve(snapshot.Path);
                    ApplyBytes(path, snapshot.Before, Hash(snapshot.After));
                    lock (stateGate) snapshots.Remove(snapshot.Path);
                    restored.Add(snapshot.Path);
                    PersistJournal();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { conflicts.Add(new { path = snapshot.Path, error = ex.Message }); }
            }
            return Json(new { restored, conflicts });
        }
        finally { mutations.Release(); }
    }

    private void PersistJournal(Snapshot? replacement = null)
    {
        List<Snapshot> records;
        lock (stateGate) records = snapshots.Values.Where(s => replacement is null || !StringComparer.OrdinalIgnoreCase.Equals(s.Path, replacement.Path)).ToList();
        if (replacement is not null) records.Add(replacement);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, workspace = Root, files = records });
        AtomicFile.Write(journalPath, LocalVault.Protect(payload));
    }

    private void ApplyBytes(string path, byte[]? desired, string expected, Action? mutationStarting = null)
    {
        Resolve(Relative(path));
        if (Directory.Exists(path)) throw new IOException("Path is a directory.");
        if (!File.Exists(path))
        {
            RequireHash(null, expected);
            if (desired is null) return;
            string parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            Resolve(Relative(path));
            using var created = WorkspaceFileAccess.Open(path, Root, FileAccess.ReadWrite, createNew: true);
            mutationStarting?.Invoke();
            created.Write(desired); created.Flush(true);
            return;
        }
        using (var current = WorkspaceFileAccess.Open(path, Root, FileAccess.ReadWrite, delete: desired is null))
        {
            byte[] bytes = ReadBounded(current);
            RequireHash(bytes, expected);
            if (desired is not null)
            {
                mutationStarting?.Invoke();
                current.Position = 0; current.Write(desired); current.SetLength(desired.Length); current.Flush(true);
                return;
            }
            mutationStarting?.Invoke(); WorkspaceFileAccess.Delete(current);
        }
    }

    private async Task<string> RunCommandAsync(JsonElement args, CancellationToken ct)
    {
        string command = Text(args, "command");
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32000) throw new ArgumentException("Command must have 1 to 32000 characters.");
        command = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding;\n" + command;
        string cwd = Resolve(Text(args, "working_directory", "."));
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Working directory does not exist.");
        if (!await approveCommand(command, cwd, ct).ConfigureAwait(false)) return Error("Command execution denied by user.");
        ct.ThrowIfCancellationRequested();
        Resolve(Relative(cwd));
        return await ExecuteProcessAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command }, cwd, ct).ConfigureAwait(false);
    }

    private Task<string> GitAsync(string name, JsonElement args, CancellationToken ct)
    {
        string cwd = Resolve(Text(args, "working_directory", "."));
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Working directory does not exist.");
        var arguments = new List<string> { "--no-pager", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "diff.external=", "-c", "color.ui=false" };
        if (name == "git_status") arguments.AddRange(new[] { "status", "--porcelain=v1", "--untracked-files=normal", "--no-renames", "--", Root });
        else
        {
            arguments.AddRange(new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-renames" });
            if (Flag(args, "staged")) arguments.Add("--cached");
            arguments.Add("--");
            if (args.TryGetProperty("path", out var selected)) arguments.Add(Resolve(selected.GetString() ?? throw new ArgumentException("path must be text.")));
            else arguments.Add(Root);
        }
        return ExecuteProcessAsync("git.exe", arguments, cwd, ct);
    }

    private async Task<string> ExecuteProcessAsync(string executable, IEnumerable<string> arguments, string cwd, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        ct.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Could not start process.");
        // Cancellation callbacks run synchronously, including when the WPF application is exiting.
        // Do not depend on an async continuation getting scheduled before process termination.
        using var killOnCancel = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        process.StandardInput.Close();
        Task<(string Text, bool Truncated)> output = DrainAsync(process.StandardOutput);
        Task<(string Text, bool Truncated)> error = DrainAsync(process.StandardError);
        bool timedOut = false;
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { await DrainAfterKillAsync(output, error).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
        }
        timedOut |= timeout.IsCancellationRequested;
        ct.ThrowIfCancellationRequested();
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            process.StandardOutput.Dispose(); process.StandardError.Dispose();
            return Json(new { exit_code = process.ExitCode, stdout = "", stderr = "Output pipes remained open after process exit.", timed_out = timedOut, output_truncated = true, working_directory = cwd });
        }
        var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
        return Json(new { exit_code = process.ExitCode, stdout = stdout.Text, stderr = stderr.Text, timed_out = timedOut,
            output_truncated = stdout.Truncated || stderr.Truncated, working_directory = cwd });
    }

    private static async Task DrainAfterKillAsync(Task<(string Text, bool Truncated)> output, Task<(string Text, bool Truncated)> error)
    { try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { } }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            int keep = Math.Min(count, MaxOutputChars - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        return (result.ToString(), truncated);
    }

    private string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("Use a workspace-relative path.");
        string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        foreach (string component in normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == ".." || component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Unsafe path component.");
            string stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Windows device names are not allowed.");
        }
        string full = Path.GetFullPath(Path.Combine(Root, normalized));
        string prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes workspace.");
        RejectReparseAncestors(full);
        return full;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Links and reparse points are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private IEnumerable<string> Walk(string directory, bool recursive, CancellationToken ct)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        int visited = 0;
        while (pending.Count > 0 && visited < 10000)
        {
            var item = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 10000) yield break;
                string safe;
                try { safe = Resolve(Relative(entry)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                yield return safe;
                if (recursive && item.Depth < 32 && Directory.Exists(safe)) pending.Push((safe, item.Depth + 1));
            }
        }
    }

    private byte[] ReadBytes(string path)
    {
        using var stream = WorkspaceFileAccess.Open(path, Root, FileAccess.Read);
        return ReadBounded(stream);
    }
    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length > MaxFileBytes) throw new IOException("File exceeds the 1 MiB text limit.");
        byte[] result = new byte[(int)stream.Length]; stream.ReadExactly(result); return result;
    }
    private static string Decode(byte[] bytes)
    {
        int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
        string text;
        try { text = Utf8.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new ArgumentException("Unsupported encoding: only valid UTF-8 text (with optional BOM) is accepted."); }
        if (text.Any(c => c == '\0' || c < 32 && c is not '\r' and not '\n' and not '\t' and not '\f')) throw new ArgumentException("Binary files are not supported.");
        return text;
    }
    private static byte[] Encode(string text, byte[]? original)
    {
        byte[] bytes = Utf8.GetBytes(text);
        if (original is not null && original.Length >= 3 && original[0] == 239 && original[1] == 187 && original[2] == 191)
            return new byte[] { 239, 187, 191 }.Concat(bytes).ToArray();
        return bytes;
    }
    private static string? Preview(byte[]? bytes)
    {
        if (bytes is null) return null;
        try { return Decode(bytes); }
        catch (ArgumentException) { return "[Partial write contains unsupported text encoding; original bytes remain in encrypted journal.]"; }
    }
    private static string Hash(byte[]? bytes) => bytes is null ? Missing : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void RequireHash(byte[]? bytes, string expected)
    { if (!Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Hash conflict: file changed; read it again before editing or undoing."); }
    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    private static string Text(JsonElement args, string name, string? fallback = null)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback ?? throw new ArgumentException("Missing argument: " + name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException(name + " must be text.");
    }
    private static bool Flag(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.GetBoolean();
    private static int Number(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string Error(string message) => Json(new { error = message });
}
