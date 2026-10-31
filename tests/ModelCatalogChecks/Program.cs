using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Naifrog.Chat;
using Naifrog.Core;

int passed = 0;
await Check("normalizes root versioned and full completion endpoints", async () =>
{
    foreach (var (input, expected) in new[] {
        ("https://catalog.invalid", "/models"), ("https://catalog.invalid/v1/", "/v1/models"),
        ("https://catalog.invalid/prefix/v1/chat/completions/", "/prefix/v1/models") })
    {
        using var service = new ModelCatalogService(new ScriptHandler((r, _) =>
        {
            Ensure(r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == expected, "incorrect model endpoint");
            Ensure(r.Headers.Authorization?.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "TEST-CATALOG", "credential missing");
            return Task.FromResult(Json("{\"data\":[]}"));
        }));
        await service.ListAsync(Settings(input), "TEST-CATALOG", default);
    }
});
await Check("returns every valid model ID preserving order case and exact text", async () =>
{
    using var service = new ModelCatalogService(new ScriptHandler((_, _) => Task.FromResult(Json(
        "{\"data\":[{\"id\":\"chat-one\"},{\"id\":\"embedding-two\"},{\"id\":\"image-three\"},{\"id\":\"chat-one\"},{\"id\":\"CHAT-one\"},{\"id\":\" model spaces \"},{\"id\":\"\"},{\"id\":\"  \"},{\"id\":42},{\"id\":null},{\"id\":\"bad\\nline\"},{}]}"))));
    var models = await service.ListAsync(Settings("https://catalog.invalid/v1"), "", default);
    Ensure(models.SequenceEqual(new[] { "chat-one", "embedding-two", "image-three", "CHAT-one", " model spaces " }), "IDs filtered, changed, or duplicated");
    if (models is IList<string> mutable) Ensure(mutable.IsReadOnly, "result must be immutable");
});
await Check("request keeps its initial provider and key snapshot", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var service = new ModelCatalogService(new ScriptHandler(async (r, ct) =>
    {
        entered.SetResult(); await release.Task.WaitAsync(ct);
        Ensure(r.RequestUri!.AbsoluteUri == "https://first.invalid/v1/models" && r.Headers.Authorization?.Parameter == "FIRST-TEST", "snapshot changed");
        return Json("{\"data\":[{\"id\":\"first-model\"}]}");
    }));
    var draft = Settings("https://first.invalid/v1");
    var pending = service.ListAsync(draft, "FIRST-TEST", default);
    await entered.Task; draft.Model.BaseUrl = "https://second.invalid/v1"; draft.Model.TimeoutSeconds = 180;
    release.SetResult(); Ensure((await pending).Single() == "first-model", "initial model result missing");
});
await Check("rejects unsafe addresses before any HTTP", async () =>
{
    int requests = 0;
    using var service = new ModelCatalogService(new ScriptHandler((_, _) => { requests++; return Task.FromResult(Json("{\"data\":[]}")); }));
    foreach (string url in new[] { "", "http://remote.invalid/v1", "ftp://catalog.invalid", "https://TEST-SECRET@catalog.invalid", "https://catalog.invalid?key=TEST-SECRET", "https://catalog.invalid#TEST-SECRET" })
    {
        var error = await Failure(() => service.ListAsync(Settings(url), "TEST-SECRET", default));
        Ensure(!error.ToString().Contains("TEST-SECRET"), "address error leaked secret");
    }
    Ensure(requests == 0, "unsafe HTTP dispatched");
});
await Check("supports loopback HTTP with actual transport", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var connection = await listener.AcceptTcpClientAsync();
        using var stream = connection.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        var lines = new List<string>(); string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) lines.Add(line);
        Ensure(lines[0].StartsWith("GET /v1/models ") && lines.Any(x => x == "Authorization: Bearer LOOPBACK-TEST"), "wire request mismatch");
        byte[] body = Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"local-model\"}]}");
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(body);
    });
    using var service = new ModelCatalogService();
    Ensure((await service.ListAsync(Settings($"http://127.0.0.1:{port}/v1"), "LOOPBACK-TEST", default)).Single() == "local-model", "wire result mismatch");
    await server;
});
await Check("HTTP errors redirects unsupported JSON and network errors stay safe", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.Redirect })
    {
        int calls = 0;
        using var service = new ModelCatalogService(new ScriptHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("TEST-SECRET"), Headers = { Location = new Uri("https://other.invalid/TEST-SECRET") } }); }));
        var error = await Failure(() => service.ListAsync(Settings("https://catalog.invalid"), "TEST-SECRET", default));
        Ensure(calls == 1 && !error.ToString().Contains("TEST-SECRET"), "unsafe error or redirect followed");
    }
    foreach (string body in new[] { "TEST-SECRET", "{}", "{\"data\":{}}", "{\"error\":\"TEST-SECRET\"}", "[]" })
    {
        using var service = new ModelCatalogService(new ScriptHandler((_, _) => Task.FromResult(Json(body))));
        Ensure(!(await Failure(() => service.ListAsync(Settings("https://catalog.invalid"), "TEST-SECRET", default))).ToString().Contains("TEST-SECRET"), "parse diagnostic leaked response");
    }
    using var failedService = new ModelCatalogService(new ScriptHandler((_, _) => throw new HttpRequestException("TEST-SECRET")));
    Ensure(!(await Failure(() => failedService.ListAsync(Settings("https://catalog.invalid"), "TEST-SECRET", default))).ToString().Contains("TEST-SECRET"), "network diagnostic leaked secret");
});
await Check("bounded response size and model count reject truncated success", async () =>
{
    foreach (string body in new[] { new string(' ', 4 * 1024 * 1024 + 1), JsonSerializer.Serialize(new { data = Enumerable.Range(0, 10001).Select(i => new { id = "model-" + i }) }) })
    {
        using var service = new ModelCatalogService(new ScriptHandler((_, _) => Task.FromResult(Json(body))));
        await Failure(() => service.ListAsync(Settings("https://catalog.invalid"), "", default));
    }
    using var chunked = new ModelCatalogService(new ScriptHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[4 * 1024 * 1024 + 1])) })));
    await Failure(() => chunked.ListAsync(Settings("https://catalog.invalid"), "", default));
});
await Check("caller cancellation reaches HTTP", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var service = new ModelCatalogService(new ScriptHandler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }));
    using var cancellation = new CancellationTokenSource();
    var pending = service.ListAsync(Settings("https://catalog.invalid"), "", cancellation.Token); await entered.Task; cancellation.Cancel();
    Ensure(await Failure(() => pending) is OperationCanceledException, "caller cancellation lost");
});
await Check("configured timeout is bounded and reported without secrets", async () =>
{
    using var service = new ModelCatalogService(new ScriptHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }));
    var settings = Settings("https://catalog.invalid"); settings.Model.TimeoutSeconds = 5;
    var error = await Failure(() => service.ListAsync(settings, "TEST-SECRET", default));
    Ensure(error is TimeoutException && !error.ToString().Contains("TEST-SECRET"), "timeout not safe or not distinguished");
});
Console.WriteLine($"PASS {passed} model catalog checks");

async Task Check(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
static void Ensure(bool condition, string message) { if (!condition) throw new Exception(message); }
static AppSettings Settings(string address) { var settings = new AppSettings(); settings.Model.BaseUrl = address; return settings; }
static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
static async Task<Exception> Failure(Func<Task> run) { try { await run(); } catch (Exception e) { return e; } throw new Exception("expected failure"); }
sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
