using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Naifrog.Core;
using Naifrog.Chat;

var root = Path.Combine(Path.GetTempPath(), "NaifrogChatChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    await Check("concurrent provider switches never mix endpoint model and key", async () =>
    {
        var store = NewStore("provider-concurrent");
        var providerA = store.Current.Clone();
        providerA.Model.BaseUrl = "http://localhost:9988/provider-a/v1";
        providerA.Model.ModelId = "provider-a-model";
        // Amplify the real snapshot/read interleaving without changing outgoing prompts.
        providerA.Model.ProactivePrompt = new string('p', 300_000);
        var providerB = providerA.Clone();
        providerB.Model.BaseUrl = "http://localhost:9988/provider-b/v1";
        providerB.Model.ModelId = "provider-b-model";
        store.Save(providerA, "TEST-PROVIDER-A");
        int mismatches = 0;
        int calls = 0;
        using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
        {
            calls++;
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            bool a = r.RequestUri!.AbsolutePath.Contains("provider-a");
            if (json.RootElement.GetProperty("model").GetString() != (a ? "provider-a-model" : "provider-b-model") ||
                r.Headers.Authorization?.Parameter != (a ? "TEST-PROVIDER-A" : "TEST-PROVIDER-B")) mismatches++;
            return JsonReply("OK");
        }));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await started.Task;
            for (int i = 0; i < 80; i++)
            {
                bool a = i % 2 == 0;
                store.Save(a ? providerA : providerB, a ? "TEST-PROVIDER-A" : "TEST-PROVIDER-B");
                await Task.Yield();
            }
        });
        var reader = Task.Run(async () =>
        {
            started.TrySetResult();
            for (int i = 0; i < 120; i++) await service.CompleteRawAsync("系统", "当前消息", default);
        });
        await Task.WhenAll(writer, reader);
        Ensure(calls == 120 && mismatches == 0, "provider endpoint/model/key snapshot mismatch count=" + mismatches);
    });

    await Check("failed provider save retains the old endpoint model and key in memory and on disk", async () =>
    {
        var store = NewStore("provider-failed-save");
        var settings = store.Current.Clone();
        settings.Model.BaseUrl = "http://localhost:9988/provider-a/v1";
        settings.Model.ModelId = "provider-a-model";
        store.Save(settings, "TEST-PROVIDER-A");
        var replacement = settings.Clone();
        replacement.Model.BaseUrl = "http://localhost:9988/provider-b/v1";
        replacement.Model.ModelId = "provider-b-model";
        using (var fileLock = new FileStream(Path.Combine(store.DataDirectory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Save(replacement, "TEST-PROVIDER-B"); throw new Exception("FAIL: locked settings save succeeded"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (var source in new[] { store, new SettingsStore(store.DataDirectory) })
        {
            using var service = new ChatService(source, new ScriptHandler(async (request, token) =>
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Ensure(request.RequestUri!.AbsolutePath == "/provider-a/v1/chat/completions" &&
                    payload.RootElement.GetProperty("model").GetString() == "provider-a-model" &&
                    request.Headers.Authorization?.Parameter == "TEST-PROVIDER-A", "failed save published mixed provider credentials");
                return JsonReply("OK");
            }));
            await service.CompleteRawAsync("系统", "当前消息", default);
        }
    });

    await Check("emotion analysis and reply both honor context pairs 0 and 1", async () =>
    {
        foreach (int pairs in new[] { 0, 1 })
        {
            var store = NewStore("emotion-context-" + pairs);
            var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
            var requests = new List<JsonElement>();
            using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
            {
                using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                var payload = json.RootElement.Clone(); requests.Add(payload);
                bool analysis = payload.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("仅输出一个JSON对象");
                return JsonReply(analysis ? "{\"name\":\"neutral\",\"intensity\":0,\"confidence\":1}" : "种子回复");
            }));
            await service.SendAsync("第一轮历史", null, default);
            await service.SendAsync(new string('文', 1800) + "第二轮历史", null, default);
            settings = store.Current.Clone(); settings.Emotion.AnalyzeWithModel = true; settings.Model.ContextPairs = pairs; store.Save(settings);
            requests.Clear();
            await service.SendAsync(new string('今', 4500), null, default);
            Ensure(requests.Count == 2, "analysis or reply missing");
            using var analysisInput = JsonDocument.Parse(requests[0].GetProperty("messages")[1].GetProperty("content").GetString()!);
            var recent = analysisInput.RootElement.GetProperty("recent");
            Ensure(recent.GetArrayLength() == pairs * 2, "emotion analysis exceeded configured context pairs " + pairs);
            Ensure(recent.EnumerateArray().All(m => m.GetProperty("Content").GetString()!.Length <= 1000), "analysis text not bounded");
            Ensure(analysisInput.RootElement.GetProperty("current").GetString()!.Length <= 4000, "current analysis text not bounded");
            Ensure(requests[1].GetProperty("messages").GetArrayLength() == pairs * 2 + 2, "body context limit exceeded");
        }
    });

    await Check("endpoint, custom settings, no-key local model, raw isolation", async () =>
    {
        var store = NewStore("config");
        var settings = store.Current.Clone();
        settings.Model.BaseUrl = "http://localhost:9988/v1/chat/completions/";
        settings.Model.ModelId = "my-local-custom-model";
        settings.Model.SystemPrompt = "用户自己的系统提示词";
        settings.Model.MaxTokens = 777;
        settings.Model.Streaming = false;
        store.Save(settings);
        var handler = new ScriptHandler(async (request, token) =>
        {
            Ensure(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "endpoint duplicated");
            Ensure(request.Headers.Authorization is null, "empty key must omit Bearer");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = payload.RootElement;
            Ensure(body.GetProperty("model").GetString() == "my-local-custom-model", "custom model ignored");
            Ensure(body.GetProperty("max_tokens").GetInt32() == 777, "max tokens ignored");
            Ensure(!body.GetProperty("stream").GetBoolean(), "nonstream ignored");
            Ensure(body.GetProperty("messages")[0].GetProperty("content").GetString() == "用户自己的系统提示词", "system ignored");
            return JsonReply("你好呀");
        });
        using var service = new ChatService(store, handler);
        Ensure(service.IsConfigured, "configured false");
        string delta = "";
        var result = await service.SendAsync("你好", text => delta = text, default);
        Ensure(result.Text == "你好呀" && delta == "你好呀", "JSON reply not emitted");
        Ensure(service.History.Count == 2, "successful turn missing");
        Ensure(!service.Diagnostic.Contains("你好"), "diagnostic leaked content");
    });

    await Check("SSE split UTF8, cumulative deltas, reasoning exclusion, DONE", async () =>
    {
        var store = NewStore("stream");
        var content = ": ping\r\ndata: {\"choices\":[{\"delta\":{\"reasoning_content\":\"隐藏内容\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\r\n\r\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"好🌷\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ScriptHandler((r, ct) => Task.FromResult(SseReply(new ChunkStream(Encoding.UTF8.GetBytes(content)))));
        using var service = new ChatService(store, handler);
        var deltas = new List<string>();
        var result = await service.SendAsync("测试", deltas.Add, default);
        Ensure(result.Text == "你好🌷", "SSE UTF8 reconstruction failed");
        Ensure(deltas.SequenceEqual(new[] { "你", "你好🌷" }), "deltas must be cumulative");
        Ensure(service.History.Count == 2, "SSE not saved");
    });

    await Check("cancellation after partial text never persists or retries", async () =>
    {
        var store = NewStore("cancel");
        using var cts = new CancellationTokenSource();
        int requests = 0;
        var handler = new ScriptHandler((r, token) =>
        {
            requests++;
            return Task.FromResult(SseReply(new ChunkStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"半句\"}}]}\n\ndata: [DONE]\n\n"))));
        });
        using var service = new ChatService(store, handler);
        await Expect<OperationCanceledException>(() => service.SendAsync("取消", _ => cts.Cancel(), cts.Token));
        Ensure(requests == 1 && service.History.Count == 0, "cancel saved or retried");
    });

    await Check("invalid emotion degrades, analysis prompt isolated, image only in reply", async () =>
    {
        var store = NewStore("emotion");
        var settings = store.Current.Clone();
        settings.Emotion.AnalyzeWithModel = true;
        settings.Emotion.Instruction = "特别情绪指令";
        settings.Model.Streaming = false;
        settings.Privacy.ScreenEnabled = true;
        store.Save(settings, authorizeScreenGrant: true);
        int calls = 0;
        var handler = new ScriptHandler(async (r, ct) =>
        {
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            var messages = json.RootElement.GetProperty("messages");
            calls++;
            if (calls == 1)
            {
                Ensure(messages[0].GetProperty("content").GetString()!.Contains("特别情绪指令"), "emotion instruction ignored");
                Ensure(!messages.ToString().Contains("image_url"), "image leaked into analysis");
                return JsonReply("{\"name\":\"unknown\",\"intensity\":0.8,\"confidence\":1}");
            }
            Ensure(messages[0].GetProperty("content").GetString() == settings.Model.SystemPrompt, "analysis contaminated body prompt");
            Ensure(messages[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64,AQID", "image attachment missing");
            return JsonReply("继续回答");
        });
        using var service = new ChatService(store, handler);
        var result = await service.SendAsync("看一下", null, default, new byte[] { 1, 2, 3 });
        Ensure(calls == 2 && result.Text == "继续回答" && result.Emotion?.Name == "neutral", "classification blocked reply");
        Ensure(service.History.All(m => !m.Content.Contains("data:image")), "image stored in memory");
    });

    await Check("queued image is blocked when another instance changes the authorized provider", async () =>
    {
        var store = NewStore("queued-image-provider");
        var settings = store.Current.Clone();
        settings.Privacy.ScreenEnabled = true;
        store.Save(settings, "TEST-PROVIDER-A", authorizeScreenGrant: true);
        Ensure(store.Snapshot().Settings.Privacy.ScreenEnabled, "image fixture did not grant current scope");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return JsonReply("OK");
        }));
        var first = service.CompleteRawAsync("系统", "阻塞排队", default);
        await entered.Task;
        var queuedImage = service.SendAsync("旧授权下排队的图片", null, default, new byte[] { 1, 2, 3 });
        var otherInstance = new SettingsStore(store.DataDirectory);
        var changed = otherInstance.Current.Clone();
        changed.Model.BaseUrl = "http://localhost:9988/provider-b/v1";
        changed.Model.ModelId = "provider-b-model";
        otherInstance.Save(changed, "TEST-PROVIDER-B");
        release.TrySetResult();
        await first;
        await Expect<InvalidOperationException>(() => queuedImage);
        Ensure(calls == 1 && service.History.Count == 0, "queued image reached unauthorized provider or memory");
        Ensure(!store.Snapshot().Settings.Privacy.ScreenEnabled, "provider switch did not revoke image grant");
    });

    await Check("encrypted recovery, pair truncation, context limit, export, clear", async () =>
    {
        var store = NewStore("memory");
        var settings = store.Current.Clone();
        settings.Model.Streaming = false;
        settings.Data.MaxMemoryPairs = 2;
        settings.Model.ContextPairs = 1;
        store.Save(settings);
        var seen = new List<int>();
        var handler = new ScriptHandler(async (r, ct) =>
        {
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            seen.Add(json.RootElement.GetProperty("messages").GetArrayLength());
            return JsonReply("回复私密文本");
        });
        using (var service = new ChatService(store, handler))
        {
            for (int i = 0; i < 3; i++) await service.SendAsync("私密消息" + i, null, default);
            Ensure(service.History.Count == 4 && service.History[0].Content == "私密消息1", "pair retention failed");
            Ensure(seen.SequenceEqual(new[] { 2, 4, 4 }), "context pair limit failed");
            var export = Path.Combine(store.DataDirectory, "export.txt");
            service.ExportMemory(export);
            Ensure(File.ReadAllText(export).Contains("私密消息1"), "export missing");
            var encrypted = File.ReadAllBytes(Path.Combine(store.DataDirectory, "chat-memory.dpapi"));
            Ensure(!Encoding.UTF8.GetString(encrypted).Contains("私密消息"), "memory stored plaintext");
            var recovered = ReadPersistedMemory(encrypted);
            Ensure(recovered?[0].Content == "私密消息1", "memory not DPAPI encrypted");
        }
        using var restored = new ChatService(store, new ScriptHandler((r, ct) => Task.FromResult(JsonReply("x"))));
        Ensure(restored.History.Count == 4, "encrypted reload failed");
        restored.ClearMemory();
        restored.ReloadMemory();
        Ensure(restored.History.Count == 0, "clear did not survive reload");
    });

    await Check("raw completion and connection test do not classify or retain", async () =>
    {
        var store = NewStore("raw");
        var settings = store.Current.Clone(); settings.Emotion.AnalyzeWithModel = true; store.Save(settings, "TEST-KEY-PRIVATE");
        int calls = 0;
        var handler = new ScriptHandler(async (r, ct) =>
        {
            calls++;
            Ensure(r.Headers.Authorization?.Parameter == "TEST-KEY-PRIVATE", "key header missing");
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (calls == 1) Ensure(json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == "调用者系统", "raw prompt ignored");
            return JsonReply("RAW");
        });
        using var service = new ChatService(store, handler);
        Ensure(await service.CompleteRawAsync("调用者系统", "整理请求", default) == "RAW", "raw mismatch");
        var status = await service.TestConnectionAsync(default);
        Ensure(calls == 2 && service.History.Count == 0, "raw classified or stored");
        Ensure(!status.Contains("TEST-KEY") && !service.Diagnostic.Contains("TEST-KEY"), "key leaked");
    });

    await Check("truncated and invalid SSE retain partial only and never replay", async () =>
    {
        foreach (var tail in new[] { "", "data: {broken-json}\n\n", "data: {\"error\":{\"message\":\"SECRET-SERVER-DETAIL\"}}\n\n" })
        {
            var store = NewStore("broken-" + Guid.NewGuid().ToString("N"));
            int count = 0;
            var handler = new ScriptHandler((r, ct) =>
            {
                count++;
                return Task.FromResult(SseReply(new ChunkStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"已显示\"}}]}\n\n" + tail))));
            });
            using var service = new ChatService(store, handler);
            string partial = "";
            await Expect<InvalidOperationException>(() => service.SendAsync("请求", text => partial = text, default));
            Ensure(count == 1 && partial == "已显示" && service.History.Count == 0, "partial response persisted/retried");
            Ensure(!service.Diagnostic.Contains("SECRET"), "diagnostic leaked server response");
        }
    });

    await Check("serialized requests see completed prior turn; queued cancellation makes no HTTP call", async () =>
    {
        var store = NewStore("concurrent");
        var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var seen = new List<string[]>();
        var handler = new ScriptHandler(async (r, ct) =>
        {
            int current = Interlocked.Increment(ref calls);
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            seen.Add(json.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()!).ToArray());
            if (current == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return JsonReply("回复" + current);
        });
        using var service = new ChatService(store, handler);
        var first = service.SendAsync("第一轮", null, default);
        await entered.Task;
        using var queuedCancellation = new CancellationTokenSource();
        var cancelled = service.SendAsync("不要发出", null, queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Expect<OperationCanceledException>(() => cancelled);
        var second = service.SendAsync("第二轮", null, default);
        Ensure(calls == 1, "concurrent request entered HTTP");
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Ensure(calls == 2 && seen[1].SequenceEqual(new[] { settings.Model.SystemPrompt, "第一轮", "回复1", "第二轮" }), "serialized context polluted");
        Ensure(service.History.Count == 4, "concurrent turn count wrong");
    });

    await Check("clear during in-flight reply prevents old context resurrection", async () =>
    {
        var store = NewStore("clear-flight");
        var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return JsonReply("旧会话回复"); }));
        var request = service.SendAsync("旧会话消息", null, default);
        await entered.Task;
        service.ClearMemory();
        release.TrySetResult();
        await request;
        service.ReloadMemory();
        Ensure(service.History.Count == 0, "cleared context resurrected");
    });

    await Check("cross-instance clear invalidates cached history export and in-flight memory commits", async () =>
    {
        var storeA = NewStore("cross-instance-clear");
        var settings = storeA.Current.Clone(); settings.Model.Streaming = false; storeA.Save(settings);
        using var serviceA = new ChatService(storeA, new ScriptHandler((r, ct) => Task.FromResult(JsonReply("已清空的私密回复"))));
        await serviceA.SendAsync("已清空的私密消息", null, default);
        using var exportInstance = new ChatService(new SettingsStore(storeA.DataDirectory));
        using var nextSendInstance = new ChatService(new SettingsStore(storeA.DataDirectory), new ScriptHandler(async (r, ct) =>
        {
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            Ensure(json.RootElement.GetProperty("messages").GetArrayLength() == 2, "unrefreshed instance resent externally cleared history");
            return JsonReply("取消后不保存");
        }));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var serviceB = new ChatService(new SettingsStore(storeA.DataDirectory), new ScriptHandler(async (r, ct) =>
        {
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (Interlocked.Increment(ref calls) == 1)
            {
                Ensure(json.RootElement.GetProperty("messages").GetArrayLength() == 4, "fixture did not load old conversation");
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            else Ensure(json.RootElement.GetProperty("messages").GetArrayLength() == 2, "next request resent externally cleared history");
            return JsonReply("本轮回复");
        }));
        var inflight = serviceB.SendAsync("清空前已发出的请求", null, default);
        await entered.Task;
        try
        {
            string revision = serviceB.MemoryRevision;
            serviceA.ClearMemory();
            Ensure(serviceB.MemoryRevision != revision && serviceB.MemoryRevision == serviceA.MemoryRevision, "external clear revision did not invalidate derived caches");
            Ensure(serviceB.History.Count == 0, "another instance still exposes cleared history");
            var export = Path.Combine(storeA.DataDirectory, "after-clear.txt");
            exportInstance.ExportMemory(export);
            Ensure(File.ReadAllText(export).Length == 0, "another instance exported cleared history");
        }
        finally { release.TrySetResult(); }
        await inflight;
        Ensure(serviceB.History.Count == 0, "in-flight response resurrected externally cleared memory");
        using var nextCancellation = new CancellationTokenSource();
        await Expect<OperationCanceledException>(() => nextSendInstance.SendAsync("独立实例清空后的请求", _ => nextCancellation.Cancel(), nextCancellation.Token));
        await serviceB.SendAsync("清空后的新消息", null, default);
        Ensure(serviceA.History.Count == 2 && serviceA.History[0].Content == "清空后的新消息", "instances did not share new history after clear");
        using var restarted = new ChatService(new SettingsStore(storeA.DataDirectory));
        Ensure(restarted.History.Count == 2 && restarted.History[0].Content == "清空后的新消息", "restart resurrected cleared text");
    });

    await Check("legacy encrypted arrays migrate and failed clear propagates without false disk success", async () =>
    {
        var store = NewStore("legacy-memory");
        var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
        var path = Path.Combine(store.DataDirectory, "chat-memory.dpapi");
        AtomicFile.Write(path, LocalVault.Protect(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new ChatMessage("user", "旧格式消息"), new ChatMessage("assistant", "旧格式回复")
        })));
        using var service = new ChatService(store, new ScriptHandler((r, ct) => Task.FromResult(JsonReply("新格式回复"))));
        Ensure(service.History.Count == 2 && service.MemoryRevision == "initial", "legacy encrypted history did not load");
        await service.SendAsync("迁移消息", null, default);
        Ensure(ReadPersistedMemory(File.ReadAllBytes(path))?.Count == 4, "legacy memory migration lost history");
        string revision = service.MemoryRevision;
        var oldBytes = File.ReadAllBytes(path);
        using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { service.ClearMemory(); throw new Exception("FAIL: locked clear falsely succeeded"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Ensure(oldBytes.SequenceEqual(File.ReadAllBytes(path)), "failed clear partially changed encrypted storage");
        using var other = new ChatService(new SettingsStore(store.DataDirectory));
        Ensure(other.History.Count == 4 && other.MemoryRevision == revision, "failed clear falsely published a revision");
        service.ClearMemory();
        Ensure(other.History.Count == 0 && other.MemoryRevision != revision, "successful clear did not replace failed attempt");
    });

    await Check("emotion timeout, valid label, confidence and strict JSON", async () =>
    {
        string[] classifications =
        {
            "TIMEOUT",
            "{\"name\":\"hug\",\"intensity\":0.6,\"confidence\":0.9}",
            "{\"name\":\"happy\",\"intensity\":0.9,\"confidence\":0.2}",
            "```json\n{\"name\":\"happy\",\"intensity\":0.9,\"confidence\":1}\n```",
            "{\"name\":\"happy\",\"intensity\":2,\"confidence\":1}",
            "{\"name\":\"happy\",\"intensity\":0.8,\"confidence\":1,\"extra\":true}"
        };
        foreach (var classification in classifications)
        {
            var store = NewStore("classification-" + Guid.NewGuid().ToString("N"));
            var settings = store.Current.Clone(); settings.Emotion.AnalyzeWithModel = true; settings.Model.Streaming = false; store.Save(settings);
            int calls = 0;
            using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
            {
                calls++;
                if (calls == 1)
                {
                    if (classification == "TIMEOUT") await Task.Delay(Timeout.Infinite, ct);
                    return JsonReply(classification);
                }
                return JsonReply("正文仍继续");
            }));
            var result = await service.SendAsync("消息", null, default);
            Ensure(calls == 2 && result.Text == "正文仍继续", "classification did not fall through");
            Ensure(result.Emotion?.Name == (classification.Contains("hug") ? "hug" : "neutral"), "classification validation wrong");
        }
    });

    await Check("configured main timeout, HTTP error sanitization, endpoint safety", async () =>
    {
        var store = NewStore("timeout");
        var settings = store.Current.Clone(); settings.Model.TimeoutSeconds = 5; store.Save(settings);
        int calls = 0;
        using (var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
        { calls++; await Task.Delay(Timeout.Infinite, ct); return JsonReply("unreachable"); })))
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await Expect<TimeoutException>(() => service.SendAsync("超时", null, default));
            Ensure(timer.Elapsed >= TimeSpan.FromSeconds(4.5) && timer.Elapsed < TimeSpan.FromSeconds(12), "configured timeout ignored");
            Ensure(calls == 1 && service.History.Count == 0, "timeout replayed or persisted");
        }
        using (var service = new ChatService(store, new ScriptHandler((r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("SECRET-KEY-SERVER-BODY") }))))
        {
            try { await service.SendAsync("请求", null, default); throw new Exception("HTTP error accepted"); }
            catch (InvalidOperationException e) { Ensure(!e.ToString().Contains("SECRET") && e.Message.Contains("401"), "HTTP details leaked"); }
        }
        foreach (var endpoint in new[] { "http://remote.example/v1", "https://user:secret@example.com/v1", "https://example.com/v1?key=secret" })
        {
            // Deliberately bypass SettingsStore.Save validation to exercise the service's own boundary.
            store.Current.Model.BaseUrl = endpoint;
            using var service = new ChatService(store, new ScriptHandler((r, ct) => throw new Exception("unsafe endpoint sent")));
            await Expect<ArgumentException>(() => service.SendAsync("请求", null, default));
        }
    });

    await Check("disabling chat memory stops using and storing previous conversation", async () =>
    {
        var store = NewStore("memory-disabled");
        var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
        int calls = 0;
        using var service = new ChatService(store, new ScriptHandler(async (r, ct) =>
        {
            calls++;
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (calls == 2) Ensure(json.RootElement.GetProperty("messages").GetArrayLength() == 2, "disabled memory still sent prior text");
            return JsonReply("回复");
        }));
        await service.SendAsync("以前的私密消息", null, default);
        var persisted = File.ReadAllBytes(Path.Combine(store.DataDirectory, "chat-memory.dpapi"));
        settings = store.Current.Clone(); settings.Data.RememberChat = false; store.Save(settings);
        await service.SendAsync("这轮不记忆", null, default);
        Ensure(persisted.SequenceEqual(File.ReadAllBytes(Path.Combine(store.DataDirectory, "chat-memory.dpapi"))), "disabled memory persisted new turn");
        service.ReloadMemory();
        Ensure(service.History.Count == 0, "disabled memory reloaded history");
    });

    await Check("clearing while memory is disabled removes encrypted history permanently", async () =>
    {
        var store = NewStore("clear-disabled");
        var settings = store.Current.Clone(); settings.Model.Streaming = false; store.Save(settings);
        using var service = new ChatService(store, new ScriptHandler((r, ct) => Task.FromResult(JsonReply("私密回复"))));
        await service.SendAsync("私密消息", null, default);
        settings = store.Current.Clone(); settings.Data.RememberChat = false; store.Save(settings);
        service.ClearMemory();
        var persisted = ReadPersistedMemory(File.ReadAllBytes(Path.Combine(store.DataDirectory, "chat-memory.dpapi")));
        Ensure(persisted is { Count: 0 }, "clear left private history on disk");
        settings = store.Current.Clone(); settings.Data.RememberChat = true; store.Save(settings);
        service.ReloadMemory();
        Ensure(service.History.Count == 0, "re-enabling memory restored cleared private text");
        using var restored = new ChatService(new SettingsStore(store.DataDirectory));
        Ensure(restored.History.Count == 0, "restart restored cleared private text");
    });
}
finally
{
    Directory.Delete(root, true);
}
Console.WriteLine($"PASS: {passed} chat checks");

SettingsStore NewStore(string name)
{
    var store = new SettingsStore(Path.Combine(root, name));
    var settings = store.Current.Clone();
    settings.Model.BaseUrl = "http://127.0.0.1:9988/v1";
    settings.Model.ModelId = "configured-by-user";
    store.Save(settings);
    return store;
}
async Task Check(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS: " + name); }
static void Ensure(bool value, string reason) { if (!value) throw new Exception("FAIL: " + reason); }
static List<ChatMessage>? ReadPersistedMemory(byte[] encrypted)
{
    var bytes = LocalVault.Unprotect(encrypted);
    try
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<ChatMessage>>()
            : document.RootElement.GetProperty("Messages").Deserialize<List<ChatMessage>>();
    }
    finally { Array.Clear(bytes); }
}
static async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("FAIL: expected " + typeof(T).Name);
}
static HttpResponseMessage JsonReply(string text) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json")
};
static HttpResponseMessage SseReply(Stream stream) => new(HttpStatusCode.OK)
{
    Content = new StreamContent(stream) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") } }
};

sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
}
sealed class ChunkStream(byte[] content) : Stream
{
    private int position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position == content.Length) return 0;
        int length = Math.Min(1, count);
        Array.Copy(content, position, buffer, offset, length); position += length; return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position == content.Length) return ValueTask.FromResult(0);
        buffer.Span[0] = content[position++]; return ValueTask.FromResult(1);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
