using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Naifrog.Core;

namespace Naifrog.Chat;

public sealed class ModelCatalogService : IDisposable
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumModels = 10000;
    private readonly HttpClient client;

    public ModelCatalogService(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        if (handler is HttpClientHandler httpHandler) httpHandler.AllowAutoRedirect = false;
        client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<string>> ListAsync(AppSettings settings, string apiKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Copy every request input before the first await; UI drafts may change while HTTP runs.
        var endpoint = ModelsEndpoint(settings.Model.BaseUrl);
        string key = apiKey ?? "";
        int timeoutSeconds = Math.Clamp(settings.Model.TimeoutSeconds, 5, 180);
        if (key.Any(char.IsControl)) throw new ArgumentException("API Key 格式无效，请检查后重试。");
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new InvalidOperationException("模型列表请求拒绝重定向，请填写服务的最终 API 地址。");
            if (!response.IsSuccessStatusCode)
            {
                string hint = status is 404 or 405 or 501 ? "该服务可能不支持 GET /models，可继续手动填写模型 ID。" : "请检查 API 地址、Key 和服务状态。";
                throw new InvalidOperationException($"获取模型列表失败（HTTP {status}）。{hint}");
            }
            if (response.Content.Headers.ContentLength > MaximumBytes) throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > MaximumBytes) throw TooLarge();
                bytes.Write(buffer, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("服务未返回兼容的模型列表（data 数组），可继续手动填写模型 ID。");
            if (data.GetArrayLength() > MaximumModels) throw new InvalidOperationException("模型列表超过 10000 项，请缩小服务返回范围后重试。");
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in data.EnumerateArray())
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                string? value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) continue;
                if (seen.Add(value)) result.Add(value);
            }
            return result.AsReadOnly();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) { throw new TimeoutException("获取模型列表超时，请稍后重试；也可以手动填写模型 ID。"); }
        catch (JsonException) { throw new InvalidOperationException("服务返回的模型列表不是有效 JSON，可继续手动填写模型 ID。"); }
        // Do not retain transport/parser exceptions as InnerException: they may contain URLs, headers or response text.
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接模型列表服务，请检查 API 地址和网络。"); }
        catch (IOException) { throw new InvalidOperationException("读取模型列表失败，请检查网络后重试。"); }
        catch (FormatException) { throw new ArgumentException("API Key 格式无效，请检查后重试。"); }
    }

    private static Uri ModelsEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("请先填写模型 API 地址。");
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("API 地址须使用 HTTPS；本机服务可使用 HTTP，地址不能包含密钥、查询参数或片段。");
        string path = uri.AbsolutePath.TrimEnd('/');
        const string completion = "/chat/completions";
        if (path.EndsWith(completion, StringComparison.OrdinalIgnoreCase)) path = path[..^completion.Length];
        if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) path += "/models";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    private static InvalidOperationException TooLarge() => new("模型列表响应超过 4 MiB，请缩小服务返回范围后重试。");
    public void Dispose() => client.Dispose();
}
