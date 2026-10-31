using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Naifrog.Agent;
using Naifrog.Core;

var home = Path.Combine(Path.GetTempPath(), "naifrog-workspace-checks-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(home, "workspace");
Directory.CreateDirectory(root);
var journal = Path.Combine(home, "journal");
var approved = new List<(string Command, string Cwd)>();
var tools = new WorkspaceTools(root, journal, true, (command, cwd, ct) => { approved.Add((command, cwd)); return Task.FromResult(true); }, 1);
int count = 0;
void Check(bool good, string label) { if (!good) throw new Exception("FAIL " + label); count++; Console.WriteLine("PASS " + label); }
async Task<JsonObject> Call(string name, object args, WorkspaceTools? subject = null, CancellationToken ct = default)
    => JsonNode.Parse(await (subject ?? tools).ExecuteAsync(new("test", name, JsonSerializer.Serialize(args)), ct))!.AsObject();
bool Error(JsonObject value) => value["error"] is not null;
async Task Git(string directory, params string[] arguments)
{
    var info = new ProcessStartInfo("git.exe") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); await stdout;
    if (process.ExitCode != 0) throw new Exception(await stderr);
}
try
{
    Check(tools.Definitions.Count == 9, "all nine tool schemas");
    var initial = await Call("write_file", new { path = "hello.txt", content = "hello\nworld\n", expected_hash = "missing" });
    Check(!Error(initial) && File.ReadAllText(Path.Combine(root, "hello.txt")) == "hello\nworld\n", "create text with missing precondition");
    var read = await Call("read_file", new { path = "hello.txt" });
    Check(read["content"]!.GetValue<string>() == "hello\nworld\n" && read["sha256"]!.GetValue<string>().Length == 64, "read text and sha256");
    string hash = read["sha256"]!.GetValue<string>();
    string volumeRoot = Path.GetPathRoot(root)!;
    var volumeTools = new WorkspaceTools(volumeRoot, Path.Combine(home, "volume-journal"), false, (_, _, _) => Task.FromResult(false));
    Check((await Call("read_file", new { path = Path.GetRelativePath(volumeRoot, Path.Combine(root, "hello.txt")) }, volumeTools))["content"]!.GetValue<string>() == "hello\nworld\n", "volume root containment accepts temporary fixture");
    Check(Error(await Call("write_file", new { path = "hello.txt", content = "lost", expected_hash = "missing" })), "reject stale create");
    Check(!Error(await Call("replace_text", new { path = "hello.txt", old_text = "world", new_text = "friend", expected_hash = hash })), "exact replacement");
    Check(Error(await Call("delete_file", new { path = "hello.txt", expected_hash = hash })), "reject stale delete");
    Check((await Call("search_text", new { query = "friend" })).ToJsonString().Contains("hello.txt"), "search actual workspace text");
    Check((await Call("list_files", new { path = "." })).ToJsonString().Contains("hello.txt"), "list actual workspace");
    Check(Error(await Call("read_file", new { path = "../outside.txt" })), "reject parent escape");
    Check(Error(await Call("write_file", new { path = "hello.txt:secret", content = "bad", expected_hash = "missing" })), "reject ADS");
    Check(Error(await Call("write_file", new { path = "NUL.txt", content = "bad", expected_hash = "missing" })), "reject device filename");
    Check(Error(await Call("read_file", new { path = "\\\\?\\C:\\Windows\\win.ini" })), "reject device path");
    string outside = Path.Combine(home, "outside"); Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "external.txt"), "outside content");
    string hardlink = Path.Combine(root, "hardlink.txt");
    if (!NativeLinks.CreateHardLink(hardlink, Path.Combine(outside, "external.txt"), IntPtr.Zero)) throw new Exception("Could not create hardlink fixture.");
    Check(Error(await Call("read_file", new { path = "hardlink.txt" })), "reject hardlinks to protect workspace boundary");
    string junction = Path.Combine(root, "junction");
    using (var linkProcess = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", junction, outside } })!)
    { await linkProcess.WaitForExitAsync(); Check(linkProcess.ExitCode == 0, "create junction fixture"); }
    Check(Error(await Call("read_file", new { path = "junction/external.txt" })), "reject junction traversal");
    Check(!(await Call("list_files", new { recursive = true })).ToJsonString().Contains("external.txt"), "listing does not follow junction");
    Directory.Delete(junction); File.Delete(hardlink);
    var denied = new WorkspaceTools(root, Path.Combine(home, "denied"), false, (_, _, _) => Task.FromResult(false), 1);
    Check(Error(await Call("write_file", new { path = "denied.txt", content = "bad", expected_hash = "missing" }, denied)), "edit permission enforced");
    Check(Error(await Call("run_command", new { command = "'must not run'" }, denied)), "command denied by callback");
    var bom = Path.Combine(root, "bom.txt"); File.WriteAllText(bom, "café", new UTF8Encoding(true));
    string bomHash = (await Call("read_file", new { path = "bom.txt" }))["sha256"]!.GetValue<string>();
    Check(!Error(await Call("write_file", new { path = "bom.txt", content = "你好", expected_hash = bomHash })) && File.ReadAllBytes(bom).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "preserve UTF8 BOM");
    File.WriteAllBytes(Path.Combine(root, "legacy.txt"), new byte[] { 0x63, 0x61, 0x66, 0xe9 });
    Check(Error(await Call("read_file", new { path = "legacy.txt" })), "reject unsupported encoding");
    File.WriteAllBytes(Path.Combine(root, "binary.bin"), new byte[] { 0, 1, 2, 3 });
    Check(Error(await Call("read_file", new { path = "binary.bin" })), "reject binary");
    Check(Error(await Call("write_file", new { path = "control.txt", content = "control\u0001", expected_hash = "missing" })), "reject binary control write");
    File.WriteAllBytes(Path.Combine(root, "large.txt"), new byte[1024 * 1024 + 1]);
    Check(Error(await Call("read_file", new { path = "large.txt" })), "reject oversized read");
    var duplicate = await Call("write_file", new { path = "duplicate.txt", content = "one one", expected_hash = "missing" });
    Check(Error(await Call("replace_text", new { path = "duplicate.txt", old_text = "one", new_text = "two", expected_hash = duplicate["sha256"]!.GetValue<string>() })), "reject ambiguous replacement");
    var execute = await Call("run_command", new { command = "[Console]::Out.Write('out'); [Console]::Error.Write('err'); exit 7" });
    Check(execute["exit_code"]!.GetValue<int>() == 7 && execute["stdout"]!.GetValue<string>().Contains("out") && execute["stderr"]!.GetValue<string>().Contains("err"), "real command output and exit");
    Check(approved.Last().Cwd == root && approved.Last().Command.Contains("exit 7"), "approval receives exact command and cwd");
    var chinese = await Call("run_command", new { command = "[Console]::Write('中文输出'); [Console]::Error.Write('错误信息'); exit 0" });
    Check(chinese["stdout"]!.GetValue<string>() == "中文输出" && chinese["stderr"]!.GetValue<string>() == "错误信息", "UTF8 Chinese stdout and stderr roundtrip");
    string deletedPath = Path.Combine(root, "delete-me.txt"); File.WriteAllText(deletedPath, "restore me");
    string deleteHash = (await Call("read_file", new { path = "delete-me.txt" }))["sha256"]!.GetValue<string>();
    Check(!Error(await Call("delete_file", new { path = "delete-me.txt", expected_hash = deleteHash })) && !File.Exists(deletedPath), "delete file through matching handle");
    var timeout = await Call("run_command", new { command = "Start-Sleep -Seconds 20" });
    Check(timeout["timed_out"]!.GetValue<bool>(), "real command timeout");
    using (var cts = new CancellationTokenSource(500))
    {
        bool cancelled = false; try { await Call("run_command", new { command = "Start-Sleep -Seconds 20" }, ct: cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "real command cancellation propagates");
    }
    var treeTools = new WorkspaceTools(root, Path.Combine(home, "tree-journal"), false, (_, _, _) => Task.FromResult(true), 10);
    using (var cts = new CancellationTokenSource())
    {
        string pidFile = Path.Combine(root, "child.pid");
        string pidLiteral = pidFile.Replace("'", "''");
        var running = Call("run_command", new { command = "$child = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -PassThru; [IO.File]::WriteAllText('" + pidLiteral + "', [string]$child.Id); Start-Sleep -Seconds 30" }, treeTools, cts.Token);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        Check(File.Exists(pidFile), "child process fixture started");
        int childPid = int.Parse(File.ReadAllText(pidFile));
        using var child = Process.GetProcessById(childPid);
        cts.Cancel();
        Check(child.WaitForExit(2000), "synchronous cancellation kills actual child tree");
        bool cancelled = false; try { await running; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "tree cancellation propagates after child termination");
    }
    var output = await Call("run_command", new { command = "[Console]::Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000)); exit 0" });
    Check(output["stdout"]!.GetValue<string>().Length <= 32000 && output["stderr"]!.GetValue<string>().Length <= 32000 && output["exit_code"]!.GetValue<int>() == 0, "bounded output drains both streams");
    var gitError = await Call("git_status", new { });
    Check(gitError["exit_code"]!.GetValue<int>() != 0, "git status real failure outside repository");
    string repository = Path.Combine(home, "repository"), project = Path.Combine(repository, "projectA"), sibling = Path.Combine(repository, "projectB");
    Directory.CreateDirectory(project); Directory.CreateDirectory(sibling);
    File.WriteAllText(Path.Combine(project, "inside.txt"), "original inside");
    File.WriteAllText(Path.Combine(sibling, "outside.txt"), "original outside");
    await Git(repository, "init", "--quiet"); await Git(repository, "add", ".");
    await Git(repository, "-c", "user.name=WorkspaceChecks", "-c", "user.email=tests@example.invalid", "commit", "--quiet", "-m", "fixture");
    File.WriteAllText(Path.Combine(project, "inside.txt"), "changed inside");
    File.WriteAllText(Path.Combine(sibling, "outside.txt"), "OUTSIDE_WORKSPACE_SECRET");
    var nested = new WorkspaceTools(project, Path.Combine(home, "nested-journal"), false, (_, _, _) => Task.FromResult(false));
    var nestedDiff = await Call("git_diff", new { }, nested);
    Check(nestedDiff["stdout"]!.GetValue<string>().Contains("changed inside") && !nestedDiff.ToJsonString().Contains("OUTSIDE_WORKSPACE_SECRET"), "git diff stays inside nested workspace");
    Check(!(await Call("git_status", new { }, nested)).ToJsonString().Contains("outside.txt"), "git status stays inside nested workspace");
    await Git(repository, "config", "core.fsmonitor", "this-program-must-never-run");
    await Git(repository, "config", "diff.external", "this-program-must-never-run");
    await Git(repository, "config", "diff.checks.textconv", "this-program-must-never-run");
    File.WriteAllText(Path.Combine(repository, ".gitattributes"), "*.txt diff=checks\n");
    Check((await Call("git_status", new { }, nested))["exit_code"]!.GetValue<int>() == 0, "Git fsmonitor configuration disabled");
    var safeDiff = await Call("git_diff", new { }, nested);
    Check(safeDiff["exit_code"]!.GetValue<int>() == 0 && safeDiff["stdout"]!.GetValue<string>().Contains("changed inside"), "Git external diff and textconv disabled");
    Check(Directory.GetFiles(journal).Length > 0 && !Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(journal)[0])).Contains("friend"), "encrypted journal on disk");
    var journalText = Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(Directory.GetFiles(journal)[0])));
    Check(journalText.Contains("hello.txt"), "journal decrypts using local vault");
    File.WriteAllText(Path.Combine(root, "hello.txt"), "external edit");
    await tools.UndoAsync(default);
    Check(File.ReadAllText(Path.Combine(root, "hello.txt")) == "external edit", "undo preserves external edits");
    Check(!File.Exists(Path.Combine(root, "duplicate.txt")) && File.ReadAllText(bom) == "café", "undo restores original files and removes creates");
    Check(tools.Changes.Count == 1 && tools.Changes[0].Path == "hello.txt", "undo retains conflict for review");
    Check(File.ReadAllText(deletedPath) == "restore me", "undo restores deleted text");
    Console.WriteLine($"WorkspaceChecks: {count} passed");
}
finally
{
    if (Directory.Exists(home))
    {
        foreach (string file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(home, true);
    }
}
static class NativeLinks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newName, string existing, IntPtr security);
}
