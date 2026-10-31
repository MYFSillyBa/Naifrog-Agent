using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Naifrog.Agent;
using Naifrog.Core;

var directory = Path.Combine(Path.GetTempPath(), "AgentTransportChecks-" + Guid.NewGuid().ToString("N"));
var store = new SettingsStore(directory);
int failures = 0, passed = 0;
try
{
    await Check("fragmented UTF8 SSE preserves text, reasoning, tool fragments and usage", async () =>
    {
        string sse = Sse(new { choices = new[] { new { index = 0, delta = new { role = "assistant", reasoning_content = "private thought" } } } })
            + Sse(new { choices = new[] { new { index = 0, delta = new { content = "你好🐸", tool_calls = new[] { new { index = 0, id = "call_", type = "function", function = new { name = "read_", arguments = "{\"pa" } } } } } } })
            + Sse(new { choices = new[] { new { index = 0, delta = new { content = "完成", tool_calls = new[] { new { index = 0, id = "1", function = new { name = "file", arguments = "th\":\"中文.txt\"}" } } } } } } })
            + Sse(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } } })
            + Sse(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 1234, completion_tokens = 90 } }) + "data: [DONE]\n\n";
        using var client = Client(_ => Response(sse, "text/event-stream", fragment: 1));
        var result = await client.CompleteAsync(Settings(), "TEST-SECRET", new(), Tools(), default);
        Ensure(result.Text == "你好🐸完成", "text or UTF8 corrupted");
        Ensure(result.ReasoningContent == "private thought" && result.PromptTokens == 1234 && result.CompletionTokens == 90, "metadata lost");
        Ensure(result.ToolCalls.Single() == new AgentToolCall("call_1", "read_file", "{\"path\":\"中文.txt\"}"), "tool fragments lost");
    });
    await Check("JSON fallback retains reasoning and usage", async () =>
    {
        using var client = Client(_ => Response(JsonReply("summary", "secret reasoning")));
        var turn = await client.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default);
        Ensure(turn.Text == "summary" && turn.ReasoningContent == "secret reasoning" && turn.PromptTokens == 42 && turn.CompletionTokens == 8, "JSON metadata missing");
    });
    await Check("stream setting and tools payload reflect immutable request", async () =>
    {
        foreach (bool streaming in new[] { false, true })
        foreach (bool tools in new[] { false, true })
        {
            var settings = Settings(); settings.Model.Streaming = streaming;
            using var client = new AgentModelClient(store, new Handler(async (r, ct) =>
            {
                var payload = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
                Ensure(payload["stream"]!.GetValue<bool>() == streaming, "stream setting ignored");
                Ensure((payload["tools"] is not null) == tools && (payload["tool_choice"] is not null) == tools, "tool payload incorrect");
                Ensure((payload["stream_options"] is not null) == streaming, "stream usage not requested");
                settings.Model.BaseUrl = "https://changed.invalid"; settings.Model.ModelId = "changed";
                Ensure(r.RequestUri!.Host == "fixture.invalid" && payload["model"]!.GetValue<string>() == "fixture-model", "provider snapshot changed");
                return Response(JsonReply("ok"));
            }));
            await client.CompleteAsync(settings, "TEST-SECRET", new(), tools ? Tools() : new(), default);
        }
    });
    await Check("context errors typed with numeric window, ordinary 400 sanitized", async () =>
    {
        foreach (string code in new[] { "context_length_exceeded", "context_window_exceeded", "prompt_too_long" })
        {
            using var client = Client(_ => Response(JsonSerializer.Serialize(new { error = new { code, message = "TEST-SECRET", context_length = 32768 } }), status: HttpStatusCode.BadRequest));
            var error = await Failure(() => client.CompleteAsync(Settings(), "TEST-SECRET", new(), Tools(), default));
            Ensure(error is AgentContextLengthException { ProviderContextTokens: 32768 }, "context not typed or numeric limit lost");
            Ensure(!error.ToString().Contains("TEST-SECRET"), "provider error leaked");
        }
        using var ordinary = Client(_ => Response("{\"error\":{\"code\":\"invalid_api_key\",\"message\":\"TEST-SECRET unsupported tool schema\"}}", status: HttpStatusCode.BadRequest));
        var other = await Failure(() => ordinary.CompleteAsync(Settings(), "TEST-SECRET", new(), Tools(), default));
        Ensure(other is not AgentContextLengthException && !other.ToString().Contains("TEST-SECRET"), "ordinary 400 classified or leaked");
        using var message = Client(_ => Response("{\"error\":{\"message\":\"Maximum context length is 8192 tokens. TEST-SECRET\"}}", status: HttpStatusCode.BadRequest));
        Ensure(await Failure(() => message.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default)) is AgentContextLengthException { ProviderContextTokens: 8192 }, "explicit message context not recognized");
    });
    await Check("invalid or incomplete protocol fails once without exposing secret", async () =>
    {
        string Finish(string reason) => Sse(new { choices = new[] { new { index = 0, delta = new { content = "TEST-SECRET" }, finish_reason = reason } } });
        string ToolsReply(object[] calls) => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = calls } } } });
        object Call(string id, string args = "{}") => new { id, type = "function", function = new { name = "read_file", arguments = args } };
        var fixtures = new (string Body, string Type)[] {
            (Finish("stop"), "text/event-stream"),
            (Finish("length") + "data: [DONE]\n\n", "text/event-stream"),
            (Finish("content_filter") + "data: [DONE]\n\n", "text/event-stream"),
            ("data: {\"error\":{\"message\":\"TEST-SECRET\"}}\n\ndata: [DONE]\n\n", "text/event-stream"),
            ("data: [DONE]\n\n", "text/event-stream"),
            (ToolsReply(new[] { Call("a"), Call("a") }), "application/json"),
            (ToolsReply(new[] { Call("bad\nID") }), "application/json"),
            (ToolsReply(new[] { Call("a", "[]") }), "application/json"),
            (ToolsReply(Enumerable.Range(0,17).Select(i => Call("a" + i)).ToArray()), "application/json"),
            (new string('x', 4 * 1024 * 1024 + 1), "application/json")
        };
        foreach (var fixture in fixtures)
        {
            int requests = 0;
            using var client = Client(_ => { requests++; return Response(fixture.Body, fixture.Type, fragment: 7); });
            var error = await Failure(() => client.CompleteAsync(Settings(), "TEST-SECRET", new(), Tools(), default));
            Ensure(requests == 1 && error is not AgentContextLengthException && !error.ToString().Contains("TEST-SECRET"), "retried, misclassified, or leaked");
        }
    });
    await Check("body callbacks exclude reasoning and remain debounced at final flush", async () =>
    {
        string first = Sse(new { choices = new[] { new { index = 0, delta = new { content = "first", reasoning_content = "PRIVATE-REASONING" } } } });
        string last = Sse(new { choices = new[] { new { index = 0, delta = new { content = " final" }, finish_reason = "stop" } } }) + "data: [DONE]\n\n";
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream((first, 40), (last, 1))) { Headers = { ContentType = new("text/event-stream") } } });
        var updates = new List<(string Text, long At)>(); var watch = System.Diagnostics.Stopwatch.StartNew();
        var turn = await client.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default, text => updates.Add((text, watch.ElapsedMilliseconds)));
        Ensure(updates.Count == 2 && updates[0].Text == "first" && updates[^1].Text == turn.Text && updates.All(u => !u.Text.Contains("PRIVATE")), "callback not cumulative body only");
        Ensure(updates[1].At - updates[0].At >= 30, "final callback bypassed debounce");
    });
    await Check("SSE supports CR separators, BOM, comments and multiline data", async () =>
    {
        string body = "\uFEFF: keepalive\rdata: {\"choices\":[{\"index\":0,\rdata: \"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\r\rdata: [DONE]\r\r";
        using var client = Client(_ => Response(body, "text/event-stream", fragment: 1));
        Ensure((await client.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default)).Text == "ok", "SSE framing corrupted");
    });
    await Check("stream cancellation interrupts after partial body without retry", async () =>
    {
        int requests = 0;
        string partial = Sse(new { choices = new[] { new { index = 0, delta = new { content = "partial" } } } });
        using var cts = new CancellationTokenSource(100);
        using var client = Client(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream((partial, 1), ("data: [DONE]\n\n", 10000))) { Headers = { ContentType = new("text/event-stream") } } }; });
        Ensure(await Failure(() => client.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), cts.Token)) is OperationCanceledException && requests == 1, "partial stream cancellation retried/transformed");
    });
    await Check("output token reserve fits configured context window", async () =>
    {
        var settings = Settings(); settings.Agent.ContextWindowTokens = 8192; settings.Agent.MaxTokens = 16384;
        using var client = new AgentModelClient(store, new Handler(async (r, ct) =>
        {
            var payload = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            Ensure(payload["max_tokens"]!.GetValue<int>() == 4096, "output reserve exceeds context budget");
            return Response(JsonReply("ok"));
        }));
        await client.CompleteAsync(settings, "TEST-SECRET", new(), new(), default);
    });
    await Check("context classification excludes input token rate limits and rejects string window values", async () =>
    {
        using var rate = Client(_ => Response("{\"error\":{\"message\":\"Input tokens exceed the tokens per minute limit TEST-SECRET\"}}", status: HttpStatusCode.BadRequest));
        var error = await Failure(() => rate.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default));
        Ensure(error is not AgentContextLengthException && !error.ToString().Contains("TEST-SECRET"), "rate limit classified as context overflow");
        using var stringWindow = Client(_ => Response("{\"error\":{\"code\":\"context_length_exceeded\",\"context_length\":\"8192 TEST-SECRET\"}}", status: HttpStatusCode.BadRequest));
        Ensure(await Failure(() => stringWindow.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default)) is AgentContextLengthException { ProviderContextTokens: null }, "untrusted string interpreted as numeric window");
        using var tooBig = Client(_ => Response("{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"" + new string('x', 70_000) + "TEST-SECRET\"}}", status: HttpStatusCode.BadRequest, fragment: 2000));
        Ensure(await Failure(() => tooBig.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), default)) is not AgentContextLengthException, "truncated error body used for classification");
    });
    await Check("cancellation remains cancellation", async () =>
    {
        using var cts = new CancellationTokenSource();
        using var client = new AgentModelClient(store, new Handler(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Response(""); }));
        Ensure(await Failure(() => client.CompleteAsync(Settings(), "TEST-SECRET", new(), new(), cts.Token)) is OperationCanceledException, "cancellation transformed");
    });
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

AppSettings Settings() => new() { Model = new() { BaseUrl = "https://fixture.invalid/v1", ModelId = "fixture-model", Streaming = true } };
JsonArray Tools() => new(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file" } });
AgentModelClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(store, new Handler((r, _) => Task.FromResult(reply(r))));
async Task Check(string name, Func<Task> test) { try { await test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
static void Ensure(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<Exception> Failure(Func<Task> test) { try { await test(); } catch (Exception e) { return e; } throw new Exception("expected failure"); }
static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
static string JsonReply(string text, string? reasoning = null) => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = text, reasoning_content = reasoning } } }, usage = new { prompt_tokens = 42, completion_tokens = 8 } });
static HttpResponseMessage Response(string body, string type = "application/json", HttpStatusCode status = HttpStatusCode.OK, int fragment = 0) => new(status) { Content = fragment == 0 ? new StringContent(body, Encoding.UTF8, type) : new StreamContent(new FragmentStream(Encoding.UTF8.GetBytes(body), fragment)) { Headers = { ContentType = new(type) } } };
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request, ct); }
sealed class FragmentStream(byte[] bytes, int fragment) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => base.ReadAsync(buffer, offset, Math.Min(count, fragment), ct);
}

sealed class PacedStream(params (string Body, int Delay)[] chunks) : Stream
{
    int index;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (index == chunks.Length) return 0;
        var chunk = chunks[index++]; await Task.Delay(chunk.Delay, ct);
        var bytes = Encoding.UTF8.GetBytes(chunk.Body); bytes.CopyTo(buffer); return bytes.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}


