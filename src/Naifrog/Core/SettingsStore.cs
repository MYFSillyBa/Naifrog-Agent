using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Naifrog.Core;

public sealed class SettingsStore
{
    private readonly object gate = new();
    private readonly string mutexName;
    private string? persistedText;
    private AppSettings baseline = new();
    public string DataDirectory { get; }
    public AppSettings Current { get; private set; } = new();
    public string ApiKey { get; private set; } = "";
    public string Status { get; private set; } = "配置已就绪";
    public event Action? Changed;
    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Naifrog"));
        mutexName = @"Global\Naifrog.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(DataDirectory).ToUpperInvariant())));
        Directory.CreateDirectory(DataDirectory);
        using var profileLock = AcquireProfileLock();
        try
        {
            var path = Path.Combine(DataDirectory, "settings.json");
            if (File.Exists(path))
            {
                persistedText = File.ReadAllText(path);
                Current = JsonSerializer.Deserialize<AppSettings>(persistedText) ?? new();
            }
            Current.Validate();
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        { Current = new(); Status = "配置读取失败，已使用默认值：" + e.Message; }
        // Screen grants are session-only; a restarted app must be enabled again.
        Current.Privacy.ScreenEnabled = false;
        try
        {
            var keyPath = CredentialPath(Current.CredentialId);
            if (File.Exists(keyPath)) ApiKey = Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(keyPath)));
        }
        catch (Exception e) { Status = "密钥读取失败，请重新填写：" + e.Message; }
        baseline = Current.Clone();
    }
    public void Save(AppSettings settings, string? apiKey = null, bool authorizeScreenGrant = false) => SaveCore(settings, apiKey, false, authorizeScreenGrant);
    private void SaveCore(AppSettings settings, string? apiKey, bool replace, bool authorizeScreenGrant)
    {
        settings.Validate();
        if (apiKey is not null)
        {
            apiKey = apiKey.Trim();
            if (apiKey.Length > 4096 || apiKey.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("API Key 格式无效。");
        }
        lock (gate)
        {
            using var profileLock = AcquireProfileLock();
            var disk = ReadProfile();
            var next = replace ? settings.Clone() : MergeChanges(disk.Settings, baseline, settings);
            string nextKey = apiKey ?? disk.ApiKey;
            // A draft may predate even our last Snapshot. Its credential reference
            // identifies the provider tuple it was edited against; retaining a newer
            // key must also retain that key's provider rather than restore the draft's.
            if (apiKey is null && settings.CredentialId != disk.Settings.CredentialId)
            {
                next.Model.BaseUrl = disk.Settings.Model.BaseUrl;
                next.Model.ModelId = disk.Settings.Model.ModelId;
            }
            next.CredentialId = disk.Settings.CredentialId;
            bool confirmedScope = authorizeScreenGrant &&
                (apiKey is not null || settings.CredentialId == disk.Settings.CredentialId) &&
                SameScreenScope(settings, nextKey, next, nextKey);
            next.Privacy.ScreenEnabled = settings.Privacy.ScreenEnabled && (confirmedScope ||
                settings.CredentialId == disk.Settings.CredentialId &&
                SameScreenScope(Current, ApiKey, disk.Settings, disk.ApiKey) &&
                SameScreenScope(Current, ApiKey, next, nextKey));
            next.Validate();
            string oldPath = CredentialPath(disk.Settings.CredentialId);
            string? stagedPath = null;
            if (nextKey != disk.ApiKey || (!File.Exists(oldPath) && apiKey is not null))
            {
                next.CredentialId = Guid.NewGuid().ToString("N");
                stagedPath = CredentialPath(next.CredentialId);
                AtomicFile.Write(stagedPath, LocalVault.Protect(Encoding.UTF8.GetBytes(nextKey)));
            }
            string text;
            try
            {
                // The atomic settings file points at a unique, already-written key.
                // A failed config write leaves both the old config and key usable.
                text = WriteProfile(next);
            }
            catch { if (stagedPath is not null) TryDelete(stagedPath); throw; }
            Publish(next, nextKey, text);
            Status = "已保存并应用";
            // Keep superseded encrypted keys: another live process or snapshot may
            // still reference them. Only an unpublished staged key is safe to delete.
        }
        Changed?.Invoke();
    }
    public (AppSettings Settings, string ApiKey) Snapshot()
    {
        lock (gate)
        {
            using var profileLock = AcquireProfileLock();
            var disk = ReadProfile();
            if (disk.Text != persistedText)
            {
                disk.Settings.Privacy.ScreenEnabled = Current.Privacy.ScreenEnabled &&
                    SameScreenScope(Current, ApiKey, disk.Settings, disk.ApiKey);
                Publish(disk.Settings, disk.ApiKey, disk.Text);
            }
            return (Current.Clone(), ApiKey);
        }
    }
    private (AppSettings Settings, string ApiKey, string? Text) ReadProfile()
    {
        string path = Path.Combine(DataDirectory, "settings.json");
        string? text = File.Exists(path) ? File.ReadAllText(path) : null;
        // Credentials are immutable once published. Reuse the validated loaded
        // tuple when its pointer file is unchanged, including startup fallback.
        if (text == persistedText) return (baseline.Clone(), ApiKey, text);
        var settings = text is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(text) ?? new();
        settings.Validate();
        settings.Privacy.ScreenEnabled = false;
        string keyPath = CredentialPath(settings.CredentialId);
        string key = File.Exists(keyPath) ? Encoding.UTF8.GetString(LocalVault.Unprotect(File.ReadAllBytes(keyPath))) : "";
        return (settings, key, text);
    }
    private string WriteProfile(AppSettings settings)
    {
        var persisted = settings.Clone();
        persisted.Privacy.ScreenEnabled = false;
        string text = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteText(Path.Combine(DataDirectory, "settings.json"), text);
        return text;
    }
    private void Publish(AppSettings settings, string key, string? text)
    {
        Current = settings; ApiKey = key; baseline = settings.Clone();
        persistedText = text;
    }
    private static AppSettings MergeChanges(AppSettings latest, AppSettings original, AppSettings edited)
    {
        var merged = JsonSerializer.SerializeToNode(latest)!.AsObject();
        Merge(merged, JsonSerializer.SerializeToNode(original)!.AsObject(), JsonSerializer.SerializeToNode(edited)!.AsObject());
        return merged.Deserialize<AppSettings>()!;

        static void Merge(JsonObject target, JsonObject before, JsonObject after)
        {
            foreach (var field in after)
            {
                if (field.Value is JsonObject nested && before[field.Key] is JsonObject old && target[field.Key] is JsonObject current)
                    Merge(current, old, nested);
                else if (!JsonNode.DeepEquals(before[field.Key], field.Value)) target[field.Key] = field.Value?.DeepClone();
            }
        }
    }
    private static bool SameScreenScope(AppSettings a, string aKey, AppSettings b, string bKey) =>
        a.Model.BaseUrl == b.Model.BaseUrl && a.Model.ModelId == b.Model.ModelId && aKey == bKey &&
        a.Privacy.MonitorIndex == b.Privacy.MonitorIndex && a.Privacy.ExcludedApps == b.Privacy.ExcludedApps &&
        a.Privacy.AttachOnMessage == b.Privacy.AttachOnMessage && a.Privacy.PeriodicObservation == b.Privacy.PeriodicObservation &&
        a.Privacy.PeriodicMinutes == b.Privacy.PeriodicMinutes;
    private IDisposable AcquireProfileLock()
    {
        var mutex = new Mutex(false, mutexName);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("配置正由另一实例使用，请稍后重试。"); }
            catch (AbandonedMutexException) { } // The previous owner exited; the atomic file remains authoritative.
            return new ProfileLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class ProfileLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private string CredentialPath(string? id)
    {
        if (id is null) return Path.Combine(DataDirectory, "credential.dpapi");
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("密钥引用无效。");
        return Path.Combine(DataDirectory, "credential-" + id + ".dpapi");
    }
    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void SavePosition(double left, double top)
    {
        lock (gate)
        {
            using var profileLock = AcquireProfileLock();
            var disk = ReadProfile();
            var settings = disk.Settings;
            settings.Privacy.ScreenEnabled = Current.Privacy.ScreenEnabled && SameScreenScope(Current, ApiKey, settings, disk.ApiKey);
            settings.Appearance.Left = left; settings.Appearance.Top = top;
            settings.Validate();
            string text = WriteProfile(settings);
            Publish(settings, disk.ApiKey, text);
        }
    }
    public void Reset()
    {
        SaveCore(new AppSettings(), "", true, false);
    }
}

public static class AtomicFile
{
    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class LocalVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] bytes) => Transform(bytes, false);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, true);
    private static byte[] Transform(byte[] bytes, bool decrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                              : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); }
    }
}
