using System.Diagnostics;
using Naifrog.Core;

if (args.Length == 2 && args[0] == "--position-worker")
{
    var worker = new SettingsStore(args[1]);
    File.WriteAllText(Path.Combine(args[1], "worker-ready"), "ready");
    AwaitFile(Path.Combine(args[1], "worker-go"));
    for (int i = 0; i < 100; i++) worker.SavePosition(i, -i);
    return;
}

string root = Path.Combine(Path.GetTempPath(), "NaifrogStoreChecks-" + Guid.NewGuid().ToString("N"));
int passed = 0;
try
{
    Check("stale position save preserves the latest provider and credential", () =>
    {
        var a = NewStore("position"); var b = new SettingsStore(a.DataDirectory);
        SaveProvider(a, "b");
        b.SavePosition(123, 456);
        AssertProvider(b, "b"); AssertProvider(new SettingsStore(a.DataDirectory), "b");
        Ensure(b.Current.Appearance.Left == 123 && b.Current.Appearance.Top == 456, "position missing");
    });
    Check("superseded encrypted credentials remain usable by live snapshots", () =>
    {
        var a = NewStore("retained"); var old = a.Snapshot();
        SaveProvider(a, "b");
        string path = Path.Combine(a.DataDirectory, "credential-" + old.Settings.CredentialId + ".dpapi");
        Ensure(File.Exists(path), "credential referenced by an existing snapshot was deleted");
        Ensure(System.Text.Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(path))) == old.ApiKey, "retained key changed");
    });
    Check("stale Save without an explicit key merges only edited fields", () =>
    {
        var a = NewStore("save"); var b = new SettingsStore(a.DataDirectory);
        var draft = b.Current.Clone(); draft.Appearance.Height = 420;
        SaveProvider(a, "b"); b.Save(draft);
        AssertProvider(b, "b"); AssertProvider(new SettingsStore(a.DataDirectory), "b");
        Ensure(b.Current.Appearance.Height == 420, "local edit missing");
    });
    Check("Snapshot refreshes another instance's coherent provider tuple", () =>
    {
        var a = NewStore("snapshot"); var b = new SettingsStore(a.DataDirectory);
        SaveProvider(a, "b"); AssertProvider(b, "b");
    });
    Check("a draft predating a refresh cannot restore an old endpoint with the new key", () =>
    {
        var a = NewStore("refreshed-draft"); var b = new SettingsStore(a.DataDirectory);
        var draft = b.Current.Clone(); draft.Appearance.Height = 425;
        SaveProvider(a, "b"); b.Snapshot(); b.Save(draft);
        AssertProvider(b, "b");
        Ensure(b.Current.Appearance.Height == 425, "draft edit missing");
    });
    Check("failed settings writes roll back memory, disk and staged credential", () =>
    {
        var a = NewStore("rollback");
        string[] before = Directory.GetFiles(a.DataDirectory, "credential*.dpapi");
        using (var held = new FileStream(Path.Combine(a.DataDirectory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ExpectWriteFailure(() => SaveProvider(a, "b"));
            ExpectWriteFailure(() => a.SavePosition(888, 999));
            AssertProvider(a, "a");
            Ensure(a.Current.Appearance.Left is null, "failed position write published memory");
        }
        AssertProvider(new SettingsStore(a.DataDirectory), "a");
        Ensure(before.Order().SequenceEqual(Directory.GetFiles(a.DataDirectory, "credential*.dpapi").Order()), "failed write left staged key");
    });
    Check("screen grants stay local, survive position refresh and revoke on scope changes", () =>
    {
        var a = NewStore("screen"); var b = new SettingsStore(a.DataDirectory);
        var consent = a.Current.Clone(); consent.Privacy.ScreenEnabled = true; a.Save(consent);
        Ensure(a.Snapshot().Settings.Privacy.ScreenEnabled, "local grant missing");
        Ensure(!b.Snapshot().Settings.Privacy.ScreenEnabled, "another instance inherited grant");
        Ensure(!new SettingsStore(a.DataDirectory).Snapshot().Settings.Privacy.ScreenEnabled, "restart inherited grant");
        b.SavePosition(10, 20);
        Ensure(a.Snapshot().Settings.Privacy.ScreenEnabled, "position-only update revoked grant");
        var changed = b.Current.Clone(); changed.Privacy.MonitorIndex = 1; b.Save(changed);
        Ensure(!a.Snapshot().Settings.Privacy.ScreenEnabled, "changed capture scope retained grant");
        b.SavePosition(30, 40);
        Ensure(!a.Snapshot().Settings.Privacy.ScreenEnabled, "grant resurrected after later refresh");
    });
    Check("a previously granted draft cannot restore consent after a scope change", () =>
    {
        var a = NewStore("stale-consent"); var b = new SettingsStore(a.DataDirectory);
        var consent = a.Current.Clone(); consent.Privacy.ScreenEnabled = true; a.Save(consent);
        var draft = a.Current.Clone();
        var changed = b.Current.Clone(); changed.Privacy.MonitorIndex = 1; b.Save(changed);
        a.Snapshot(); a.Save(draft);
        Ensure(!a.Snapshot().Settings.Privacy.ScreenEnabled, "stale consent was restored");
    });
    Check("a confirmed screen scope can be granted in the same save", () =>
    {
        var a = NewStore("confirmed-consent");
        var settings = a.Current.Clone(); settings.Privacy.MonitorIndex = 1;
        settings.Privacy.AttachOnMessage = true; settings.Privacy.ScreenEnabled = true;
        a.Save(settings, authorizeScreenGrant: true);
        Ensure(a.Snapshot().Settings.Privacy.ScreenEnabled, "explicitly confirmed scope was not granted");
        Ensure(!new SettingsStore(a.DataDirectory).Snapshot().Settings.Privacy.ScreenEnabled, "explicit confirmation leaked to restart");
        settings = a.Current.Clone(); settings.Privacy.MonitorIndex = 2; a.Save(settings);
        Ensure(!a.Snapshot().Settings.Privacy.ScreenEnabled, "unconfirmed new scope retained the grant");
    });
    Check("cross-process position saves never roll back provider writes", () =>
    {
        var a = NewStore("process");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--position-worker"); start.ArgumentList.Add(a.DataDirectory);
        using var child = Process.Start(start)!;
        AwaitFile(Path.Combine(a.DataDirectory, "worker-ready"));
        SaveProvider(a, "b");
        File.WriteAllText(Path.Combine(a.DataDirectory, "worker-go"), "go");
        for (int i = 0; i < 40; i++) { string name = i % 2 == 0 ? "a" : "b"; SaveProvider(a, name); AssertProvider(a, name); }
        Ensure(child.WaitForExit(30000) && child.ExitCode == 0, "position worker failed");
        AssertProvider(new SettingsStore(a.DataDirectory), "b");
    });
    Check("concurrent snapshots never mix provider endpoint, model and key", () =>
    {
        var a = NewStore("concurrent"); var b = new SettingsStore(a.DataDirectory);
        Parallel.Invoke(() => { for (int i = 0; i < 80; i++) SaveProvider(a, i % 2 == 0 ? "a" : "b"); },
            () => { for (int i = 0; i < 150; i++) { var s = b.Snapshot(); string name = s.Settings.Model.ModelId; Ensure(s.Settings.Model.BaseUrl == "http://localhost/" + name && s.ApiKey == "TEST-" + name, "mixed snapshot"); } });
    });
    Check("startup fallback for corrupt settings remains readable and repairable", () =>
    {
        string directory = Path.Combine(root, "corrupt"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{broken");
        var store = new SettingsStore(directory);
        Ensure(store.Snapshot().Settings.Model.BaseUrl == "", "startup fallback unavailable");
        SaveProvider(store, "a"); AssertProvider(new SettingsStore(directory), "a");
    });
    Check("Reset replaces all latest fields and clears the active credential", () =>
    {
        var a = NewStore("reset"); var b = new SettingsStore(a.DataDirectory);
        var changed = a.Current.Clone(); changed.Companion.SoundEnabled = true; a.Save(changed);
        b.Reset(); var restored = new SettingsStore(a.DataDirectory).Snapshot();
        Ensure(restored.ApiKey == "" && restored.Settings.Model.BaseUrl == "" && !restored.Settings.Companion.SoundEnabled, "stale Reset retained settings");
    });
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
Console.WriteLine($"PASS: {passed} store checks");

SettingsStore NewStore(string name) { var store = new SettingsStore(Path.Combine(root, name)); SaveProvider(store, "a"); return store; }
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS: " + name); }
static void SaveProvider(SettingsStore store, string name) { var s = store.Current.Clone(); s.Model.BaseUrl = "http://localhost/" + name; s.Model.ModelId = name; store.Save(s, "TEST-" + name); }
static void AssertProvider(SettingsStore store, string name) { var s = store.Snapshot(); Ensure(s.Settings.Model.BaseUrl == "http://localhost/" + name && s.Settings.Model.ModelId == name && s.ApiKey == "TEST-" + name, "provider tuple lost or mixed"); }
static void Ensure(bool value, string reason) { if (!value) throw new Exception("FAIL: " + reason); }
static void ExpectWriteFailure(Action action) { try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } throw new Exception("FAIL: locked write succeeded"); }
static void AwaitFile(string path) { var timer = Stopwatch.StartNew(); while (!File.Exists(path)) { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("FAIL: process handshake timed out"); Thread.Sleep(10); } }
