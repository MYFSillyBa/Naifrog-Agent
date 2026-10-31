using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Naifrog.Agent;
using Naifrog.Core;

var root = Path.Combine(Path.GetTempPath(), "NaifrogAgentChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    await Check("tools protocol preserves call IDs, content, endpoint and explicit snapshot", async () =>
    {
        var store = NewStore("protocol");
        var snapshot = store.Snapshot();
        using var model = new AgentModelClient(store, new ScriptHandler(async (r, ct) =>
        {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            Ensure(r.RequestUri!.AbsolutePath == "/v1/chat/completions", "wrong endpoint");
            Ensure(r.Headers.Authorization?.Parameter == "TEST-SECRET", "wrong snapshot credential");
            Ensure(body["model"]!.GetValue<string>() == "test-model", "wrong model");
            Ensure(body["messages"]![1]!["tool_call_id"]!.GetValue<string>() == "earlier-id", "lost tool result ID");
            return Reply("working", new AgentToolCall("call-1", "read_file", "{\"path\":\"x.txt\"}"));
        }));
        var messages = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = "previous" }, new JsonObject { ["role"] = "tool", ["tool_call_id"] = "earlier-id", ["content"] = "result" });
        var turn = await model.CompleteAsync(snapshot.Settings, snapshot.ApiKey, messages, new JsonArray(), default);
        Ensure(turn.Text == "working" && turn.ToolCalls.Single().Id == "call-1", "lost assistant content or ID");
    });

    await Check("reject malformed, duplicate, excessive, truncated and unsupported tool replies without leaking secrets", async () =>
    {
        var store = NewStore("invalid");
        string[] payloads = {
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}",
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"a\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}},{\"id\":\"a\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]}}]}",
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"a\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"invalid\"}}]}}]}",
            "{\"error\":{\"message\":\"TEST-SECRET tools unsupported\"}}",
            new string('x', 4 * 1024 * 1024 + 1)
        };
        foreach (string payload in payloads)
        {
            using var client = new AgentModelClient(store, new ScriptHandler((_, _) => Task.FromResult(Raw(payload))));
            var error = await Failure(() => client.CompleteAsync(store.Current, store.ApiKey, new(), new(), default));
            Ensure(!error.ToString().Contains("TEST-SECRET"), "credential in exception");
        }
        using var unsupported = new AgentModelClient(store, new ScriptHandler((_, _) => Task.FromResult(Raw("TEST-SECRET tools unsupported", HttpStatusCode.BadRequest))));
        var unsupportedError = await Failure(() => unsupported.CompleteAsync(store.Current, store.ApiKey, new(), new(), default));
        Ensure(unsupportedError.Message.Contains("工具") && !unsupportedError.ToString().Contains("TEST-SECRET"), "unhelpful unsupported diagnostic");
        using var many = new AgentModelClient(store, new ScriptHandler((_, _) => Task.FromResult(Reply("", Enumerable.Range(0, 17).Select(i => new AgentToolCall("c" + i, "read_file", "{}")).ToArray()))));
        await Failure(() => many.CompleteAsync(store.Current, store.ApiKey, new(), new(), default));
    });

    await Check("session executes real tools, feeds failures back for repair, continues and encrypts history", async () =>
    {
        var store = NewStore("loop");
        string workspace = Path.Combine(root, "loop-workspace"); Directory.CreateDirectory(workspace);
        int round = 0;
        using var session = new AgentSession(store, workspace, true, (_, _, _) => Task.FromResult(true), new ScriptHandler(async (r, ct) =>
        {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            var messages = body["messages"]!.AsArray();
            round++;
            if (round == 1) return Reply("create", new AgentToolCall("write-1", "write_file", "{\"path\":\"code.txt\",\"content\":\"public class {\",\"expected_hash\":\"missing\"}"));
            if (round == 2)
            {
                Ensure(messages.Last()!["role"]!.GetValue<string>() == "tool" && messages.Last()!["tool_call_id"]!.GetValue<string>() == "write-1", "missing first tool result");
                return Reply("read", new AgentToolCall("read-2", "read_file", "{\"path\":\"code.txt\"}"));
            }
            if (round == 3) return Reply("command", new AgentToolCall("build-3", "run_command", "{\"command\":\"Write-Output TEST-SECRET; try { Add-Type -TypeDefinition (Get-Content code.txt -Raw) -ErrorAction Stop } catch { Write-Output $_; exit 7 }\"}"));
            if (round == 4)
            {
                Ensure(messages.Last()!["tool_call_id"]!.GetValue<string>() == "build-3", "missing build result");
                Ensure(JsonNode.Parse(messages.Last()!["content"]!.GetValue<string>())!["exit_code"]!.GetValue<int>() == 7, "compiler failure not fed back to model");
                return Reply("repair", new AgentToolCall("fix-4", "write_file", System.Text.Json.JsonSerializer.Serialize(new { path = "code.txt", content = "public class Repaired {}", expected_hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("public class {"))) })));
            }
            if (round == 5) return Reply("rebuild", new AgentToolCall("build-5", "run_command", "{\"command\":\"Add-Type -TypeDefinition (Get-Content code.txt -Raw) -ErrorAction Stop; Write-Output compiled\"}"));
            if (round == 6)
            {
                Ensure(JsonNode.Parse(messages.Last()!["content"]!.GetValue<string>())!["exit_code"]!.GetValue<int>() == 0, "repaired code did not compile");
                return Reply("done TEST-SECRET");
            }
            Ensure(messages.Any(m => m?["role"]?.GetValue<string>() == "assistant" && (m?["content"]?.GetValue<string>() ?? "").Contains("done")), "continuation lost assistant history");
            return Reply("continued");
        }));
        string result = await session.RunAsync("change file", default);
        Ensure(session.Status == "已完成" && !session.IsRunning, "task did not complete: " + result);
        Ensure(File.ReadAllText(Path.Combine(workspace, "code.txt")) == "public class Repaired {}", "real model tool edits did not execute");
        Ensure(!result.Contains("TEST-SECRET") && session.Events.All(e => !e.Text.Contains("TEST-SECRET")), "key leaked in visible task history");
        Ensure(await session.RunAsync("continue", default) == "continued" && round == 7, "continuation failed");
        string path = Path.Combine(store.DataDirectory, "AgentTasks", session.Id, "record.dpapi");
        byte[] encrypted = File.ReadAllBytes(path);
        Ensure(!Encoding.UTF8.GetString(encrypted).Contains("change file"), "history stored as plaintext");
        string history = Encoding.UTF8.GetString(LocalVault.Unprotect(encrypted));
        Ensure(history.Contains("continued") && !history.Contains("TEST-SECRET"), "encrypted history absent or contains credential");
    });

    await Check("legacy round and character limits do not stop current task", async () =>
    {
        var store = NewStore("limits"); var settings = store.Current.Clone(); settings.Agent.MaxRounds = 1; settings.Agent.MaxContextChars = 16000; store.Save(settings);
        string workspace = Path.Combine(root, "limits-workspace"); Directory.CreateDirectory(workspace);
        int calls = 0;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(++calls <= 42 ? Reply("", new AgentToolCall("read-" + calls, "read_file", "{\"path\":\"missing.txt\"}")) : Reply("completed"))));
        Ensure(await session.RunAsync("read", default) == "completed" && calls == 43, "legacy round limit still stops task");
        Ensure(await session.RunAsync(new string('p', 17000), default) == "completed", "legacy character limit still stops task");
        Ensure(session.Events.All(e => e.Kind != "request" || e.Text == "正在思考"), "numeric rounds displayed");
    });

    await Check("cancellation releases overlapping workspace lock and caps distinct active tasks", async () =>
    {
        var store = NewStore("parallel");
        string workspace = Path.Combine(root, "parallel-workspace"); Directory.CreateDirectory(workspace); Directory.CreateDirectory(Path.Combine(workspace, "child"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }));
        Task<string> active = first.RunAsync("wait", default); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var overlap = new AgentSession(store, Path.Combine(workspace, "child"), false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(Reply("ok"))));
        string rejected = await overlap.RunAsync("overlap", default); Ensure(rejected.Contains("工作区") && !overlap.IsRunning, "overlapping workspaces ran");
        first.Cancel(); await active; Ensure(first.Status == "已停止", "cancel status wrong");
        Ensure(await overlap.RunAsync("retry", default) == "ok", "workspace lock leaked");
        var sessions = new List<AgentSession>(); var running = new List<Task<string>>(); int entered = 0;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                string dir = Path.Combine(root, "distinct-" + i); Directory.CreateDirectory(dir);
                var s = new AgentSession(store, dir, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (_, ct) => { Interlocked.Increment(ref entered); await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }));
                sessions.Add(s); running.Add(s.RunAsync("wait", default));
            }
            var until = DateTime.UtcNow.AddSeconds(10); while (Volatile.Read(ref entered) < 3 && DateTime.UtcNow < until) await Task.Delay(10);
            Ensure(entered == 3, "three distinct sessions not running");
            string fourthDir = Path.Combine(root, "fourth"); Directory.CreateDirectory(fourthDir);
            using var fourth = new AgentSession(store, fourthDir, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(Reply("no"))));
            Ensure((await fourth.RunAsync("fourth", default)).Contains("3"), "fourth active task allowed");
        }
        finally { foreach (var s in sessions) s.Cancel(); await Task.WhenAll(running); foreach (var s in sessions) s.Dispose(); }
    });

    await Check("per-run provider snapshot stays fixed while continuation adopts updated settings", async () =>
    {
        var store = NewStore("snapshot"); string workspace = Path.Combine(root, "snapshot-workspace"); Directory.CreateDirectory(workspace);
        int requests = 0;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (r, ct) =>
        {
            requests++; var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            if (requests < 3)
            {
                Ensure(r.RequestUri!.AbsolutePath == "/v1/chat/completions" && r.Headers.Authorization!.Parameter == "TEST-SECRET" && body["model"]!.GetValue<string>() == "test-model", "provider changed within one run");
                if (requests == 1)
                {
                    var changed = store.Current.Clone(); changed.Model.BaseUrl = "http://localhost:9988/new/v1"; changed.Model.ModelId = "new-model"; changed.Agent.SystemPrompt = "new-prompt"; store.Save(changed, "NEW-SECRET");
                    return Reply("read", new AgentToolCall("r", "read_file", "{\"path\":\"absent\"}"));
                }
            }
            else Ensure(r.RequestUri!.AbsolutePath == "/new/v1/chat/completions" && r.Headers.Authorization!.Parameter == "NEW-SECRET" && body["messages"]![0]!["content"]!.GetValue<string>().Contains("new-prompt"), "continuation retained stale provider or system prompt");
            return Reply("ok");
        }));
        await session.RunAsync("first", default); await session.RunAsync("second", default); Ensure(requests == 3, "unexpected request count");
    });

    await Check("cancel during approval preserves tool result sequence for continuation", async () =>
    {
        var store = NewStore("approval-cancel"); string workspace = Path.Combine(root, "approval-cancel-workspace"); Directory.CreateDirectory(workspace);
        var approval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
        using var session = new AgentSession(store, workspace, false, async (_, _, ct) => { approval.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return true; }, new ScriptHandler(async (r, ct) =>
        {
            requests++;
            if (requests == 1) return Reply("", new AgentToolCall("a", "run_command", "{\"command\":\"Write-Output should-not-run\"}"), new AgentToolCall("b", "read_file", "{\"path\":\"absent\"}"));
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            var messages = body["messages"]!.AsArray();
            var toolResults = messages.Where(m => m?["role"]?.GetValue<string>() == "tool").ToArray();
            Ensure(toolResults.Length == 2 && toolResults[0]!["tool_call_id"]!.GetValue<string>() == "a" && toolResults[1]!["tool_call_id"]!.GetValue<string>() == "b", "cancel left dangling tool calls");
            return Reply("resumed");
        }));
        Task<string> run = session.RunAsync("wait for approval", default); await approval.Task.WaitAsync(TimeSpan.FromSeconds(10)); session.Cancel(); await run;
        Ensure(session.Status == "已停止", "approval cancellation not stopped");
        Ensure(await session.RunAsync("resume", default) == "resumed", "could not continue cancelled batch");
    });

    await Check("HTTP error and length truncation never become successful task completion", async () =>
    {
        foreach (bool truncated in new[] { false, true })
        {
            var store = NewStore("session-errors-" + truncated); string workspace = Path.Combine(root, "session-errors-workspace-" + truncated); Directory.CreateDirectory(workspace);
            using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(truncated ? Raw("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"done TEST-SECRET\"}}]}") : Raw("TEST-SECRET", HttpStatusCode.Unauthorized))));
            string result = await session.RunAsync("do task", default); Ensure(session.Status == "失败" && !result.Contains("TEST-SECRET"), "error became success or leaked credential");
        }
    });

    await Check("large tool outputs compact repeatedly and finish all tools preserving goals and full encrypted history", async () =>
    {
        var store = NewStore("tool-overflow"); var settings = store.Current.Clone(); settings.Agent.MaxContextChars = 16000; settings.Agent.ContextWindowTokens = 8192; store.Save(settings);
        string workspace = Path.Combine(root, "tool-overflow-workspace"); Directory.CreateDirectory(workspace); File.WriteAllText(Path.Combine(workspace, "large.txt"), new string('a', 24000));
        int requests = 0, summaries = 0;
        using var session = new AgentSession(store, workspace, true, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (r, ct) =>
        {
            var payload = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            if (payload["tools"] is not JsonArray tools || tools.Count == 0)
            {
                summaries++; return summaries % 2 == 1 ? Raw("summary unavailable", HttpStatusCode.ServiceUnavailable) : Reply("已读取资料；继续完成所有目标，不要重复已执行的写入。");
            }
            string wire = payload["messages"]!.ToJsonString();
            if (++requests == 1) return Reply("ready for next instruction");
            Ensure(wire.Contains("first-goal") && wire.Contains("second-instruction"), "compaction lost unresolved goals or instructions");
            var messages = payload["messages"]!.AsArray();
            foreach (var m in messages.Where(m => m?["tool_calls"] is JsonArray))
                foreach (var call in m!["tool_calls"]!.AsArray())
                    Ensure(messages.Any(x => x?["tool_call_id"]?.GetValue<string>() == call!["id"]!.GetValue<string>()), "compaction left dangling calls");
            if (requests == 2) return Reply("", new AgentToolCall("large-2", "read_file", "{\"path\":\"large.txt\"}"), new AgentToolCall("following-write", "write_file", "{\"path\":\"after-large.txt\",\"content\":\"executed\",\"expected_hash\":\"missing\"}"));
            if (requests <= 7) return Reply("", new AgentToolCall("large-" + requests, "read_file", "{\"path\":\"large.txt\"}"));
            return Reply("verified");
        }));
        Ensure(await session.RunAsync("first-goal", default) == "ready for next instruction", "initial goal failed");
        Ensure(await session.RunAsync("second-instruction", default) == "verified", "large output stopped unfinished task");
        Ensure(File.ReadAllText(Path.Combine(workspace, "after-large.txt")) == "executed", "large output skipped remaining tool calls");
        Ensure(summaries >= 2 && session.Events.Count(e => e.Kind == "compaction") >= 2, "no repeated compaction");
        string record = Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(Path.Combine(store.DataDirectory, "AgentTasks", session.Id, "record.dpapi"))));
        Ensure(record.Contains(new string('a', 24000)), "complete output lost from encrypted history");
    });

    await Check("provider context rejection retries only the request and shrinks progressively", async () =>
    {
        var store = NewStore("service-context"); string workspace = Path.Combine(root, "service-context-workspace"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "large.txt"), new string('b', 24000));
        int requests = 0; var sizes = new List<int>();
        using var session = new AgentSession(store, workspace, true, (_, _, _) => Task.FromResult(true), new ScriptHandler(async (r, ct) =>
        {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            if (body["tools"] is not JsonArray tools || tools.Count == 0) return Reply("已经执行一次命令和读取；继续原目标。");
            if (++requests == 1) return Reply("", new AgentToolCall("once", "run_command", "{\"command\":\"Add-Content once.txt executed\"}"), new AgentToolCall("large", "read_file", "{\"path\":\"large.txt\"}"));
            sizes.Add(body["messages"]!.ToJsonString().Length);
            if (requests <= 3) return Raw("{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"maximum context length exceeded\"}}", HttpStatusCode.BadRequest);
            return Reply("completed");
        }));
        Ensure(await session.RunAsync("finish objective", default) == "completed", "provider context rejection not recovered");
        Ensure(File.ReadAllLines(Path.Combine(workspace, "once.txt")).Length == 1, "context retry repeated an executed command");
        Ensure(requests == 4 && sizes[1] < sizes[0] && sizes[2] < sizes[1], "context retries did not progressively shrink");
    });

    await Check("drive-root workspace overlaps a nested workspace", async () =>
    {
        var store = NewStore("drive-root"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var drive = new AgentSession(store, Path.GetPathRoot(root)!, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }));
        var running = drive.RunAsync("wait", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var nested = new AgentSession(store, root, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(Reply("wrong"))));
            Ensure((await nested.RunAsync("nested", default)).Contains("工作区"), "drive root failed to lock nested workspace");
        }
        finally { drive.Cancel(); await running; }
    });

    await Check("session cancellation terminates a real command process", async () =>
    {
        var store = NewStore("command-cancel"); string workspace = Path.Combine(root, "command-cancel-workspace"); Directory.CreateDirectory(workspace);
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(true), new ScriptHandler((_, _) => Task.FromResult(Reply("", new AgentToolCall("wait", "run_command", "{\"command\":\"Set-Content child.pid $PID; Start-Sleep -Seconds 60\"}")))));
        var running = session.RunAsync("wait", default);
        string pidFile = Path.Combine(workspace, "child.pid"); var until = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(pidFile) && DateTime.UtcNow < until) await Task.Delay(20);
        Ensure(File.Exists(pidFile), "command never started");
        int pid = int.Parse(File.ReadAllText(pidFile).Trim()); session.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(session.Status == "已停止", "command cancellation failed");
        bool alive;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
        Ensure(!alive, "command process survived session cancellation");
    });

    await Check("escaped credential is removed from tool JSON and encrypted transcript", async () =>
    {
        var store = NewStore("escaped-secret"); const string credential = "secret-\"quoted\\key"; store.Save(store.Current.Clone(), credential);
        string workspace = Path.Combine(root, "escaped-secret-workspace"); Directory.CreateDirectory(workspace); File.WriteAllText(Path.Combine(workspace, "secret.txt"), credential);
        int round = 0;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (r, ct) =>
        {
            if (++round == 1) return Reply("reading", new AgentToolCall("read-secret", "read_file", "{\"path\":\"secret.txt\"}"));
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!; string toolOutput = body["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
            Ensure(!JsonNode.Parse(toolOutput)!.ToJsonString().Contains(System.Text.Json.JsonSerializer.Serialize(credential)[1..^1]), "escaped credential retained in parsed tool output");
            return Reply("redacted " + credential);
        }));
        Ensure(!(await session.RunAsync("inspect", default)).Contains(credential), "credential in model reply");
        string record = Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(Path.Combine(store.DataDirectory, "AgentTasks", session.Id, "record.dpapi"))));
        var parsed = JsonNode.Parse(record)!;
        Ensure(parsed["Messages"]!.AsArray().All(m => !(m?["content"]?.GetValue<string>() ?? "").Contains(credential)), "credential in durable message values");
    });

    await Check("noisy failed command retains valid result JSON and stable identities in timeline", async () =>
    {
        var store = NewStore("noisy-cjk"); string workspace = Path.Combine(root, "noisy-cjk-workspace"); Directory.CreateDirectory(workspace);
        int requests = 0;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(true), new ScriptHandler((_, _) => Task.FromResult(++requests == 1
            ? Reply("", new AgentToolCall("noisy", "run_command", "{\"command\":\"Write-Output ('中' * 9000); [Console]::Error.Write(('錯' * 9000)); exit 7\"}")) : Reply("failure acknowledged"))));
        await session.RunAsync("run noisy command", default);
        var result = session.Events.Single(e => e.Kind == "tool_result");
        string json = result.Text[(result.Text.IndexOf('\n') + 1)..];
        Ensure(JsonNode.Parse(json)!["exit_code"]!.GetValue<int>() == 7, "large failed command result malformed or exit code lost");
        Ensure(session.Events.All(e => !string.IsNullOrWhiteSpace(e.Id)), "event missing stable identity");
        Ensure(result.Id == session.Events.Single(e => e.Kind == "tool_call").Id, "tool result identity changed");
    });

    await Check("compaction summary and subsequent context preserve earlier workspace rules, failed commands and changed paths", async () =>
    {
        var context = new AgentContext(); var settings = new AgentSettings(); var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "follow applicable workspace conventions" },
            new JsonObject { ["role"] = "user", ["content"] = "finish original objective; preserve user files" });
        AddBatch(messages, "rules", "read_file", "{\"path\":\"AGENTS.md\"}", "{\"path\":\"AGENTS.md\",\"content\":\"KNOWN_AGENTS_RULE: use existing storage layer, never add raw SQL.\"}");
        AddBatch(messages, "build", "run_command", "{\"command\":\"dotnet build src/Storage.csproj\"}", "{\"exit_code\":7,\"stdout\":\"EARLY_BUILD_ERROR: missing StorageAdapter\"}");
        AddBatch(messages, "changed", "write_file", "{\"path\":\"src/StorageAdapter.cs\",\"content\":\"placeholder\"}", "{\"path\":\"src/StorageAdapter.cs\",\"sha256\":\"abc\"}");
        for (int i = 0; i < 18; i++) AddBatch(messages, "read-" + i, "read_file", "{\"path\":\"noise-" + i + ".txt\"}", new string('z', 1800));
        bool sawSummary = false;
        var compact = await context.CompactAsync(messages, new(), settings, (input, _) =>
        {
            string text = input.ToJsonString();
            Ensure(text.Contains("KNOWN_AGENTS_RULE") && text.Contains("dotnet build src/Storage.csproj") && text.Contains("EARLY_BUILD_ERROR") && text.Contains("src/StorageAdapter.cs"), "summary omitted earlier rules, command, error or changed file");
            Ensure(text.Length < 30000, "summary input not bounded"); sawSummary = true;
            return Task.FromResult("intentionally incomplete model summary");
        }, 1, null, default);
        for (int i = 0; i < 18; i++) AddBatch(compact, "later-" + i, "read_file", "{\"path\":\"later.txt\"}", new string('q', 1800));
        compact = await context.CompactAsync(compact, new(), settings, (_, _) => throw new InvalidOperationException("summary unavailable"), 1, null, default);
        string retained = compact.ToJsonString();
        Ensure(sawSummary && retained.Contains("KNOWN_AGENTS_RULE") && retained.Contains("EARLY_BUILD_ERROR") && retained.Contains("src/StorageAdapter.cs"), "repeated compaction lost pinned conventions or salient ledger");
    });

    await Check("all retained tool assistant batches keep provider reasoning metadata private", async () =>
    {
        var context = new AgentContext(); var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "system" }, new JsonObject { ["role"] = "user", ["content"] = "goal" });
        for (int i = 0; i < 10; i++)
        {
            AddBatch(messages, "reason-" + i, "read_file", "{\"path\":\"a.txt\"}", new string('r', 1400));
            messages[messages.Count - 2]!["reasoning_content"] = "PRIVATE_REASONING_" + i;
        }
        var compact = await context.CompactAsync(messages, new(), new AgentSettings(), (input, _) =>
        {
            Ensure(!input.ToJsonString().Contains("PRIVATE_REASONING"), "private reasoning entered summary"); return Task.FromResult("summary");
        }, 0, null, default);
        var batches = compact.Where(m => m?["tool_calls"] is JsonArray).ToArray();
        Ensure(batches.Length >= 2 && batches.All(m => m?["reasoning_content"] is not null), "retained earlier tool batch lost required reasoning metadata");
    });

    await Check("queued references wait for HTTP, retain FIFO and finish accepted final-boundary input", async () =>
    {
        var store = NewStore("reference-http"); string workspace = Path.Combine(root, "reference-http-workspace"); Directory.CreateDirectory(workspace);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0; bool cancelled = false; AgentSession? current = null;
        using var session = current = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (r, ct) =>
        {
            var payload = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            if (++requests == 1)
            {
                using var registration = ct.Register(() => cancelled = true);
                entered.TrySetResult(); await release.Task.WaitAsync(ct); return Reply("early final");
            }
            var users = payload["messages"]!.AsArray().Where(m => m?["role"]?.GetValue<string>() == "user").Select(m => m!["content"]!.GetValue<string>()).ToArray();
            Ensure(users[0] == "original objective" && users.Length == 3 && users[1].Contains("first reference") && users[2].Contains("second reference"), "reference FIFO or original objective lost");
            Ensure(users[1] != "first reference TEST-SECRET" && users[1].Contains("引用") && !users[1].Contains("TEST-SECRET"), "reference not marked/redacted");
            Ensure(Pending(current!) == 0 && current!.Events.Count(e => e.Kind == "reference") == 2, "admission status not updated");
            return Reply("references processed");
        }));
        Ensure(!Queue(session, "idle"), "idle reference accepted");
        var run = session.RunAsync("original objective", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(Queue(session, "first reference TEST-SECRET") && Queue(session, "second reference"), "busy reference rejected");
        var ids = session.Events.Where(e => e.Kind == "reference_pending").Select(e => e.Id).ToArray();
        Ensure(ids.Length == 2 && Pending(session) == 2 && !cancelled && requests == 1, "queue interrupted current HTTP or missing immediate event");
        release.SetResult(); Ensure(await run == "references processed" && requests == 2 && !cancelled, "final reply discarded accepted references");
        Ensure(session.Events.Where(e => e.Kind == "reference").Select(e => e.Id).SequenceEqual(ids), "reference identities changed");
        Ensure(!Queue(session, "closed draft"), "closed task accepted reference");
    });

    await Check("references wait for the entire approved tool batch before entering context", async () =>
    {
        var store = NewStore("reference-tools"); string workspace = Path.Combine(root, "reference-tools-workspace"); Directory.CreateDirectory(workspace);
        var approval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
        using var session = new AgentSession(store, workspace, true, async (_, _, ct) => { approval.SetResult(); return await release.Task.WaitAsync(ct); }, new ScriptHandler(async (r, ct) =>
        {
            if (++requests == 1) return Reply("batch", new AgentToolCall("cmd", "run_command", "{\"command\":\"Set-Content approved.txt done\"}"), new AgentToolCall("read", "read_file", "{\"path\":\"approved.txt\"}"));
            var messages = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!["messages"]!.AsArray();
            int call = messages.ToList().FindIndex(m => m?["tool_calls"] is JsonArray);
            Ensure(messages[call + 1]!["tool_call_id"]!.GetValue<string>() == "cmd" && messages[call + 2]!["tool_call_id"]!.GetValue<string>() == "read" && messages[call + 3]!["role"]!.GetValue<string>() == "user", "reference split tool batch");
            Ensure(messages[call + 2]!["content"]!.GetValue<string>().Contains("done"), "remaining tool was skipped");
            return Reply("batch complete");
        }));
        var run = session.RunAsync("original tools objective", default); await approval.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(Queue(session, "batch reference") && Pending(session) == 1 && requests == 1, "approval interrupted by reference");
        release.SetResult(true); Ensure(await run == "batch complete", "batch did not continue");
    });

    await Check("stopped pending references are encrypted and precede the continuation prompt", async () =>
    {
        var store = NewStore("reference-stop"); string workspace = Path.Combine(root, "reference-stop-workspace"); Directory.CreateDirectory(workspace);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (r, ct) =>
        {
            if (++requests == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }
            var users = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!["messages"]!.AsArray().Where(m => m?["role"]?.GetValue<string>() == "user").Select(m => m!["content"]!.GetValue<string>()).ToArray();
            Ensure(users.Length == 3 && users[0] == "original" && users[1].Contains("durable reference") && users[2] == "continue", "old reference reordered after new prompt");
            return Reply("continued references");
        }));
        var run = session.RunAsync("original", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(Queue(session, "durable reference TEST-SECRET"), "reference rejected"); session.Cancel(); await run;
        Ensure(Pending(session) == 1 && !Queue(session, "stopped"), "stopped queue lost or acceptance open");
        byte[] encrypted = File.ReadAllBytes(Path.Combine(store.DataDirectory, "AgentTasks", session.Id, "record.dpapi"));
        Ensure(!Encoding.UTF8.GetString(encrypted).Contains("durable reference"), "pending reference plaintext on disk");
        var record = JsonNode.Parse(Encoding.UTF8.GetString(LocalVault.Unprotect(encrypted)))!;
        Ensure(record["PendingReferences"]!.AsArray().Count == 1 && record["PendingReferences"]![0]!["Text"]!.GetValue<string>().Contains("durable reference") && !record.ToJsonString().Contains("TEST-SECRET"), "pending reference missing or not redacted in durable record");
        Ensure(await session.RunAsync("continue", default) == "continued references" && Pending(session) == 0, "continuation failed");
    });

    await Check("reference queue is isolated, bounded, validates input and rejects disposed sessions", async () =>
    {
        var store = NewStore("reference-bounds"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string workspace = Path.Combine(root, "reference-bounds-workspace"); Directory.CreateDirectory(workspace);
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }));
        string otherWorkspace = Path.Combine(root, "reference-other-workspace"); Directory.CreateDirectory(otherWorkspace);
        using var other = new AgentSession(store, otherWorkspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(Reply("other"))));
        Ensure((await Failure(() => session.RunAsync(new string('x', 32001), default))) is ArgumentException && !session.IsRunning, "oversize run input accepted");
        var run = session.RunAsync("bounds", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(!Queue(session, " ") && !Queue(session, new string('x', 32001)) && !Queue(other, "isolation"), "invalid or cross-task input accepted");
        for (int i = 0; i < 4; i++) Ensure(Queue(session, new string((char)('a' + i), 32000)), "valid queue capacity rejected");
        Ensure(!Queue(session, "overflow") && Pending(session) == 4 && Pending(other) == 0, "character capacity or session isolation failed");
        session.Cancel(); await run;
        session.Dispose(); Ensure(!Queue(session, "disposed"), "disposed session accepted input");
        using var countSession = new AgentSession(store, otherWorkspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply("never"); }));
        var countRun = countSession.RunAsync("count", default);
        for (int i = 0; i < 20; i++) Ensure(Queue(countSession, "short " + i), "count capacity rejected early");
        Ensure(!Queue(countSession, "twenty first") && Pending(countSession) == 20, "message capacity failed"); countSession.Cancel(); await countRun;
    });

    await Check("final event acceptance is resolved before task closure and closed drafts remain rejected", async () =>
    {
        var store = NewStore("reference-final"); string workspace = Path.Combine(root, "reference-final-workspace"); Directory.CreateDirectory(workspace);
        int requests = 0; bool offered = false, accepted = false;
        using var session = new AgentSession(store, workspace, false, (_, _, _) => Task.FromResult(false), new ScriptHandler((_, _) => Task.FromResult(Reply(++requests == 1 ? "boundary final" : "processed boundary"))));
        session.Changed += () => { if (!offered && session.Events.Any(e => e.Kind == "assistant" && e.Text == "boundary final")) { offered = true; accepted = Queue(session, "boundary reference"); } };
        string result = await session.RunAsync("boundary original", default);
        Ensure(offered && (accepted ? result == "processed boundary" && requests == 2 : result == "boundary final" && requests == 1), "accepted final-boundary input was lost");
        Ensure(!Queue(session, "keep draft"), "completed window still accepts input");
    });

    Console.WriteLine($"PASS {passed} agent checks");
}
finally { Directory.Delete(root, true); }

SettingsStore NewStore(string name)
{
    var store = new SettingsStore(Path.Combine(root, name));
    var settings = store.Current.Clone(); settings.Model.BaseUrl = "http://localhost:9988/v1/"; settings.Model.ModelId = "test-model";
    store.Save(settings, "TEST-SECRET"); return store;
}
async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
static void Ensure(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
static bool Queue(AgentSession session, string text)
{
    var method = typeof(AgentSession).GetMethod("TryQueueReference", new[] { typeof(string) });
    Ensure(method is not null, "TryQueueReference API missing"); return (bool)method!.Invoke(session, new object[] { text })!;
}
static int Pending(AgentSession session)
{
    var property = typeof(AgentSession).GetProperty("PendingReferenceCount");
    Ensure(property is not null, "PendingReferenceCount API missing"); return (int)property!.GetValue(session)!;
}
static void AddBatch(JsonArray messages, string id, string name, string arguments, string output)
{
    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "inspect then continue", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }) });
    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output });
}
static async Task<Exception> Failure(Func<Task> action)
{
    try { await action(); } catch (Exception e) { return e; }
    throw new Exception("FAIL: expected failure");
}
static HttpResponseMessage Raw(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
static HttpResponseMessage Reply(string text, params AgentToolCall[] calls)
{
    var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
    if (calls.Length > 0) message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments } }).ToArray());
    return Raw(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = calls.Length == 0 ? "stop" : "tool_calls", ["message"] = message }) }.ToJsonString());
}
sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
