using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Naifrog.Core;

namespace Naifrog.Agent;

/// <summary>Bounded JSON/SSE chat transport; each request uses the supplied provider snapshot.</summary>
public sealed class AgentModelClient : IDisposable
{
    private const int ResponseByteLimit = 4 * 1024 * 1024;
    private const int FieldLimit = 128_000;
    private readonly HttpClient client;

    public AgentModelClient(SettingsStore store, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, true)
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AgentTurn> CompleteAsync(AppSettings settings, string apiKey, JsonArray messages, JsonArray tools,
        CancellationToken token, Action<string>? onDelta = null)
    {
        // Clone all mutable request inputs before the first await.
        var snapshot = settings.Clone();
        var payload = new JsonObject
        {
            ["model"] = snapshot.Model.ModelId?.Trim(), ["messages"] = messages.DeepClone(),
            ["stream"] = snapshot.Model.Streaming,
            ["max_tokens"] = Math.Min(snapshot.Agent.MaxTokens, Math.Max(512, snapshot.Agent.ContextWindowTokens / 2))
        };
        if (tools.Count > 0) { payload["tools"] = tools.DeepClone(); payload["tool_choice"] = "auto"; }
        if (snapshot.Model.Streaming) payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(snapshot.Model.TimeoutSeconds, 5, 180)));
        try
        {
            if (string.IsNullOrWhiteSpace(snapshot.Model.ModelId)) throw new InvalidOperationException("请先配置工程 Agent 使用的模型名称。");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(snapshot.Model.BaseUrl));
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(snapshot.Model.Streaming ? "text/event-stream" : "application/json"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The body is used only for classification; provider text is never put in exceptions.
                byte[] errorBody = await ReadBoundedAsync(response.Content, 64 * 1024, true, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 400 or 413 or 422 && TryContextError(errorBody, out int? window))
                    throw new AgentContextLengthException(window);
                throw new InvalidOperationException($"工程模型服务返回 HTTP {(int)response.StatusCode}。请检查地址、权限和模型是否支持 chat/completions 的工具调用（tools/function）。");
            }
            if (response.Content.Headers.ContentLength is long length && length > ResponseByteLimit)
                throw new InvalidDataException("模型响应超过 4 MiB 上限。");
            AgentTurn turn;
            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                turn = await ReadEventsAsync(response.Content, onDelta, deadline.Token).ConfigureAwait(false);
            else
            {
                using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, ResponseByteLimit, false, deadline.Token).ConfigureAwait(false));
                turn = Parse(document.RootElement);
                if (turn.Text.Length > 0) onDelta?.Invoke(turn.Text);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return turn;
        }
        catch (AgentContextLengthException) { throw; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("工程模型请求超时；可继续任务重试。"); }
        catch (OperationCanceledException) { throw new OperationCanceledException(token); }
        catch (Exception e) when (e is HttpRequestException || e is IOException && e is not InvalidDataException)
        { throw new InvalidOperationException("工程模型网络请求失败。请检查网络、服务地址和证书；已执行的工具不会自动重试。"); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        { throw new InvalidOperationException("模型未返回有效 JSON/UTF-8 工具协议；请使用支持 chat/completions tools 的模型。"); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            // Local messages only, with an additional exact-key guard. Never retain an unsafe inner exception.
            string message = e is ArgumentException ? "模型连接配置或工具协议无效。API 地址须使用 HTTPS，本机服务可使用 HTTP。" : e.Message;
            throw new InvalidOperationException(Redact(message, apiKey));
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, bool truncate, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - output.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit)
            {
                if (truncate) return Array.Empty<byte>(); // Incomplete JSON cannot safely classify an error.
                throw new InvalidDataException("模型响应超过 4 MiB 上限。");
            }
            output.Write(buffer, 0, count);
        }
    }

    private static async Task<AgentTurn> ReadEventsAsync(HttpContent content, Action<string>? onDelta, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[8192]; var chars = new char[8192];
        var pending = new StringBuilder(); var data = new StringBuilder();
        var state = new StreamingTurn(onDelta);
        int total = 0; bool done = false, firstCharacter = true, previousCr = false;
        void Line(string line)
        {
            if (done) return;
            if (line.EndsWith('\r')) line = line[..^1];
            if (line.Length == 0)
            {
                if (data.Length == 0) return;
                string value = data.ToString(); data.Clear();
                if (value == "[DONE]") { done = true; return; }
                using var json = JsonDocument.Parse(value);
                state.Add(json.RootElement);
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
        }
        while (!done)
        {
            int count = await input.ReadAsync(bytes, token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > ResponseByteLimit) throw new InvalidDataException("模型响应超过 4 MiB 上限。");
            int decoded = decoder.GetChars(bytes, 0, count, chars, 0, false);
            for (int i = 0; i < decoded; i++)
            {
                char c = chars[i];
                if (firstCharacter) { firstCharacter = false; if (c == '\uFEFF') continue; }
                if (c == '\n' && previousCr) { previousCr = false; continue; }
                previousCr = c == '\r';
                if (c is '\r' or '\n') { Line(pending.ToString()); pending.Clear(); }
                else pending.Append(c);
            }
        }
        // A missing [DONE] is an interrupted stream, even when a finish fragment was received.
        if (!done) { decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true); throw new InvalidDataException("模型流式回复未完整结束；已执行的工具不会自动重试。"); }
        token.ThrowIfCancellationRequested();
        var turn = state.Complete();
        await state.FlushAsync(token).ConfigureAwait(false);
        return turn;
    }

    private sealed class StreamingTurn(Action<string>? onDelta)
    {
        private readonly StringBuilder text = new(), reasoning = new();
        private readonly SortedDictionary<int, StreamTool> calls = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long emittedAt;
        private string emitted = "";
        private string? finish;
        private int? promptTokens, completionTokens;
        public void Add(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) throw new InvalidDataException("模型流式回复包含服务错误。");
            ReadUsage(root, ref promptTokens, ref completionTokens);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("模型流式回复缺少 choices。");
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型流式 choice 无效。");
                if (choice.TryGetProperty("index", out var index) && (!index.TryGetInt32(out int n) || n != 0)) throw new InvalidDataException("模型流式 choice 索引不兼容。");
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型流式增量无效。");
                    if (finish is not null && delta.EnumerateObject().Any()) throw new InvalidDataException("模型完成状态之后仍有回复增量。");
                    if (delta.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")) throw new InvalidDataException("模型返回了无效的 assistant 角色。");
                    if (delta.TryGetProperty("function_call", out _)) throw new InvalidDataException("模型使用旧版 function_call 协议。");
                    Append(delta, "content", text, FieldLimit); Append(delta, "reasoning_content", reasoning, ResponseByteLimit);
                    if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null)
                    {
                        if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 16) throw new InvalidDataException("每轮最多允许 16 个工具调用。");
                        foreach (var tool in tools.EnumerateArray())
                        {
                            if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("index", out var toolIndex) || !toolIndex.TryGetInt32(out int i) || i < 0 || i >= 16)
                                throw new InvalidDataException("模型工具调用索引无效。");
                            if (!calls.TryGetValue(i, out var call)) calls[i] = call = new();
                            Append(tool, "id", call.Id, 200);
                            if (tool.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "function")) throw new InvalidDataException("模型工具调用类型不兼容。");
                            if (tool.TryGetProperty("type", out _)) call.HasType = true;
                            if (tool.TryGetProperty("function", out var function))
                            {
                                if (function.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型工具调用 function 无效。");
                                Append(function, "name", call.Name, 100); Append(function, "arguments", call.Arguments, FieldLimit);
                            }
                        }
                    }
                    if (clock.ElapsedMilliseconds - emittedAt >= 34) Emit();
                }
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                {
                    if (finish is not null || reason.ValueKind != JsonValueKind.String) throw new InvalidDataException("模型完成状态无效。");
                    finish = reason.GetString();
                }
            }
        }
        public AgentTurn Complete()
        {
            if (calls.Keys.Where((value, index) => value != index).Any()) throw new InvalidDataException("模型工具调用索引不连续。");
            var tools = new JsonArray();
            foreach (var call in calls.Values)
                tools.Add(new JsonObject { ["id"] = call.Id.ToString(), ["type"] = call.HasType ? "function" : null,
                    ["function"] = new JsonObject { ["name"] = call.Name.ToString(), ["arguments"] = call.Arguments.ToString() } });
            var root = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString(), ["reasoning_content"] = reasoning.ToString(), ["tool_calls"] = tools } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens } };
            using var json = JsonDocument.Parse(root.ToJsonString());
            var turn = Parse(json.RootElement);
            return turn;
        }
        public async Task FlushAsync(CancellationToken token)
        {
            if (onDelta is null || text.Length == 0 || text.ToString() == emitted) return;
            if (emitted.Length > 0)
            {
                int remaining = (int)(34 - (clock.ElapsedMilliseconds - emittedAt));
                if (remaining > 0) await Task.Delay(remaining, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            Emit();
        }
        private void Emit()
        {
            if (onDelta is null || text.Length == 0 || text.ToString() == emitted) return;
            emitted = text.ToString(); emittedAt = clock.ElapsedMilliseconds; onDelta(emitted);
        }
    }
    private sealed class StreamTool
    {
        public readonly StringBuilder Id = new(), Name = new(), Arguments = new();
        public bool HasType;
    }
    private static void Append(JsonElement element, string name, StringBuilder target, int limit)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("模型流式字段类型无效。");
        string fragment = value.GetString()!;
        if (target.Length + fragment.Length > limit) throw new InvalidDataException("模型回复字段过长。");
        target.Append(fragment);
    }

    private static AgentTurn Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException("模型未返回 choices；请检查模型是否支持工具调用（tools/function）。");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("模型缺少完成状态，无法确认本轮完整结束。");
        string reason = finish.GetString()!;
        if (reason == "length") throw new InvalidDataException("模型回复因长度上限被截断；任务未完成，请提高回复上限后继续。");
        if (reason != "stop" && reason != "tool_calls") throw new InvalidDataException("模型未正常完成回复，可能触发内容过滤或使用了不兼容的工具协议。");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("模型没有返回 assistant 消息。");
        if (message.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"))
            throw new InvalidDataException("模型返回了无效的 assistant 角色。");
        if (message.TryGetProperty("function_call", out _)) throw new InvalidDataException("模型使用旧版 function_call 协议；请选择支持 tools/tool_calls 的模型。");
        string text = OptionalString(message, "content", FieldLimit) ?? "";
        string? reasoning = OptionalString(message, "reasoning_content", ResponseByteLimit);
        var calls = new List<AgentToolCall>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null)
        {
            if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 16) throw new InvalidDataException("每轮最多允许 16 个工具调用。");
            foreach (var call in tools.EnumerateArray())
            {
                string id = RequiredString(call, "id", 200);
                if (!SafeIdentifier(id) || !ids.Add(id) || RequiredString(call, "type", 20) != "function" || !call.TryGetProperty("function", out var function))
                    throw new InvalidDataException("模型工具调用包含无效或重复 ID 或不兼容类型。");
                string name = RequiredString(function, "name", 100);
                if (!SafeIdentifier(name)) throw new InvalidDataException("模型工具名称无效。");
                string arguments = RequiredString(function, "arguments", FieldLimit);
                using var parsed = JsonDocument.Parse(arguments);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("工具参数必须是 JSON 对象。");
                calls.Add(new(id, name, arguments));
            }
        }
        if (calls.Count == 0 && (reason == "tool_calls" || string.IsNullOrWhiteSpace(text)))
            throw new InvalidDataException("模型没有返回正文或工具调用，任务未完成。");
        if (calls.Count > 0 && reason != "tool_calls") throw new InvalidDataException("模型工具调用与完成状态不一致。");
        int? prompt = null, completion = null; ReadUsage(root, ref prompt, ref completion);
        return new(text, calls.AsReadOnly(), reasoning, prompt, completion);
    }
    private static bool SafeIdentifier(string text) => text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    private static string? OptionalString(JsonElement element, string name, int limit)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > limit) throw new InvalidDataException("模型回复字段类型无效或字段过长。");
        return value.GetString();
    }
    private static string RequiredString(JsonElement element, string name, int limit)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型工具调用缺少有效字段。");
        string? value = OptionalString(element, name, limit);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("模型工具调用缺少有效字段或字段过长。");
        return value;
    }
    private static void ReadUsage(JsonElement root, ref int? prompt, ref int? completion)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pn) && pn >= 0) prompt = pn;
        if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int cn) && cn >= 0) completion = cn;
    }
    private static bool TryContextError(byte[] bytes, out int? window)
    {
        window = null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            string code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            string type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            string message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.ToLowerInvariant() : "";
            bool Known(string s) => s.Equals("context_length_exceeded", StringComparison.OrdinalIgnoreCase) || s.Equals("context_window_exceeded", StringComparison.OrdinalIgnoreCase) || s.Equals("prompt_too_long", StringComparison.OrdinalIgnoreCase);
            bool explicitMessage = message.Contains("maximum context length") || message.Contains("context length exceeded") ||
                message.Contains("exceeds the context window") || message.Contains("context window exceeded") || message.Contains("prompt is too long");
            if (!Known(code) && !Known(type) && !explicitMessage) return false;
            foreach (string field in new[] { "context_length", "context_window", "max_context_length", "context_window_tokens", "max_model_len" })
                if (error.TryGetProperty(field, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int n) && n is >= 1024 and <= 20_000_000) { window = n; break; }
            if (window is null)
            {
                var match = Regex.Match(message, @"(?:maximum context length|context window)(?: is| of|:| equals)?\s*(\d{4,8})\s*(?:tokens)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int n) && n is >= 1024 and <= 20_000_000) window = n;
            }
            return true;
        }
        catch (Exception e) when (e is JsonException or RegexMatchTimeoutException) { return false; }
    }
    private static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("模型 API 地址无效。");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) path += "/chat/completions";
        return new UriBuilder(uri) { Path = path }.Uri;
    }
    internal static string Redact(string text, string key) => string.IsNullOrEmpty(key) ? text : text.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
    public void Dispose() => client.Dispose();
}
