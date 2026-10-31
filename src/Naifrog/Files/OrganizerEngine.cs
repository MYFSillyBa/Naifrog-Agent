using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Naifrog.Core;

namespace Naifrog.Files;

/// <summary>Top-level file plans and durable, non-overwriting transactions. Never runs a shell.</summary>
public sealed class OrganizerEngine
{
    public const int ScanLimit = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SettingsStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? journalKey;
    private string JournalDirectory => Path.Combine(store.DataDirectory, "FileJournals");
    public string? LastJournalPath { get; private set; }
    public OrganizerEngine(SettingsStore store) => this.store = store;

    public Task<OrganizerScan> ScanAsync(string source, CancellationToken token) => Task.Run(() =>
    {
        source = FileSafety.SafePath(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("源文件夹不存在。");
        var items = new List<OrganizerItem>(); int skipped = 0, seen = 0; bool limited = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            if (++seen > ScanLimit) { limited = true; break; }
            try
            {
                FileSafety.SafePath(path);
                var attributes = File.GetAttributes(path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                var info = new FileInfo(path);
                items.Add(new OrganizerItem
                {
                    SourcePath = Path.GetFullPath(path), Name = Path.GetFileName(path), IsDirectory = directory,
                    Length = directory ? 0 : info.Length, LastWriteUtc = File.GetLastWriteTimeUtc(path),
                    Category = Classify(path, directory)
                });
            }
            catch (Exception error) when (IsFileError(error)) { skipped++; }
        }
        return new OrganizerScan(items, skipped, limited);
    }, token);

    public static string Classify(string path, bool directory = false)
    {
        if (directory) return "文件夹";
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("发票") || name.Contains("invoice")) return "票据";
        if (name.Contains("截图") || name.StartsWith("screenshot", StringComparison.Ordinal)) return "图片";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" => "图片",
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "视频",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "音频",
            ".pdf" or ".doc" or ".docx" or ".txt" or ".md" or ".ppt" or ".pptx" or ".xls" or ".xlsx" or ".csv" => "文档",
            ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => "压缩包",
            ".exe" or ".msi" or ".msix" => "安装包",
            ".cs" or ".py" or ".js" or ".ts" or ".json" or ".html" or ".css" or ".c" or ".cpp" or ".h" => "代码",
            _ => "其他"
        };
    }

    public static void ApplyAiCategories(IEnumerable<OrganizerItem> items, string response)
    {
        if (response.Length > 128000) throw new ArgumentException("AI 分类结果过长。");
        var lookup = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("AI 分类须返回 JSON 数组。");
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (changes.Count >= ScanLimit || row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var id) || !row.TryGetProperty("category", out var category) ||
                    id.ValueKind != JsonValueKind.String || category.ValueKind != JsonValueKind.String ||
                    !lookup.ContainsKey(id.GetString()!) || !changes.TryAdd(id.GetString()!, FileSafety.Category(category.GetString()!)))
                    throw new ArgumentException("AI 返回了未知或重复的文件 ID，未更改预览。");
            }
        }
        catch (JsonException error) { throw new ArgumentException("AI 未返回有效 JSON，预览保持原样。", error); }
        foreach (var change in changes) lookup[change.Key].Category = change.Value;
    }

    public Task<OrganizerResult> ExecuteAsync(IEnumerable<OrganizerItem> items, string target, OrganizationMode mode,
        CancellationToken token, Action<OrganizerProgress>? progress = null) =>
        ExecuteSnapshotAsync(items.Where(x => x.Included).Select(x => x.Snapshot()).ToArray(), target, mode, false, token, progress);

    internal Task<OrganizerResult> ReceiveAsync(OrganizerItem[] items, string target, OrganizationMode mode,
        CancellationToken token) => ExecuteSnapshotAsync(items, target, mode, true, token, null);

    private Task<OrganizerResult> ExecuteSnapshotAsync(OrganizerItem[] items, string target, OrganizationMode mode, bool flat,
        CancellationToken token, Action<OrganizerProgress>? progress) => Task.Run(async () =>
    {
        int completed = 0, failed = 0; bool cancelled = false; var errors = new List<string>();
        string? path = null;
        try { await gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new OrganizerResult(0, 0, true, null, errors); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (items.Length == 0) return new OrganizerResult(0, 0, false, null, new[] { "没有勾选任何文件。" });
            if (items.Length > ScanLimit || !Enum.IsDefined(mode)) throw new ArgumentException("操作数量或方式无效。");
            target = FileSafety.SafePath(target);
            var journal = new OrganizerJournal { TargetRoot = target, Mode = mode };
            path = Path.Combine(FileSafety.SafePath(JournalDirectory), journal.Id + ".json");
            LastJournalPath = path;
            Save(path, journal);
            foreach (var item in items)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                JournalEntry? entry = null;
                try
                {
                    FileSafety.SafePath(item.SourcePath);
                    if (item.IsDirectory && (mode == OrganizationMode.Copy || mode == OrganizationMode.Move))
                        throw new IOException("文件夹仅支持引用或快捷方式；复制/移动文件夹未实现。");
                    if (Directory.Exists(item.SourcePath) != item.IsDirectory) throw new IOException("文件类型已变化，请重新扫描。");
                    if (!item.IsDirectory)
                    {
                        var current = new FileInfo(item.SourcePath);
                        if (!current.Exists || current.Length != item.Length || current.LastWriteTimeUtc != item.LastWriteUtc)
                            throw new IOException("文件自预览后已变化，请重新扫描。");
                    }
                    string directory = flat ? target : Path.Combine(target, FileSafety.Category(item.Category));
                    FileSafety.SafePath(directory);
                    string filename = item.Name + (mode == OrganizationMode.Reference ? ".reference.json" : mode == OrganizationMode.Shortcut ? ".lnk" : "");
                    entry = new JournalEntry
                    {
                        Source = Path.GetFullPath(item.SourcePath), SourceRoot = Path.GetDirectoryName(Path.GetFullPath(item.SourcePath))!,
                        SourceIsDirectory = item.IsDirectory, Destination = FileSafety.AvailablePath(directory, filename),
                        Staging = Path.Combine(directory, ".naifrog-" + Guid.NewGuid().ToString("N") + (mode == OrganizationMode.Shortcut ? ".partial.lnk" : ".partial"))
                    };
                    journal.Entries.Add(entry);
                    if (mode == OrganizationMode.Copy || mode == OrganizationMode.Move)
                    {
                        using var source = FileSafety.OpenLocked(entry.Source, mode == OrganizationMode.Move);
                        entry.SourceIdentity = FileSafety.Identity(source);
                        entry.Hash = await FileSafety.HashAsync(source, token).ConfigureAwait(false); entry.Length = source.Length;
                        Save(path, journal); // Durable intent precedes every file write.
                        Directory.CreateDirectory(FileSafety.SafePath(directory)); FileSafety.SafePath(directory);
                        await CopyToStagingAsync(source, entry, token).ConfigureAwait(false);
                        await PrepareAndCommitAsync(entry, journal, path, token).ConfigureAwait(false);
                        using var committed = FileSafety.OpenLocked(entry.Destination, false);
                        await VerifyAsync(committed, entry.Hash, entry.Length, entry.DestinationIdentity, token).ConfigureAwait(false);
                        if (mode == OrganizationMode.Move) { token.ThrowIfCancellationRequested(); FileSafety.DeleteLocked(source); }
                    }
                    else
                    {
                        byte[]? reference = mode == OrganizationMode.Reference ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                        { version = 1, originalPath = entry.Source, name = item.Name, isDirectory = item.IsDirectory, category = item.Category, createdUtc = DateTime.UtcNow }, JsonOptions)) : null;
                        if (reference is not null) { entry.Hash = Convert.ToHexString(SHA256.HashData(reference)); entry.Length = reference.Length; }
                        Save(path, journal);
                        Directory.CreateDirectory(FileSafety.SafePath(directory)); FileSafety.SafePath(directory);
                        token.ThrowIfCancellationRequested();
                        if (reference is not null)
                        {
                            using var file = new FileStream(entry.Staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            await file.WriteAsync(reference, token).ConfigureAwait(false); file.Flush(true);
                        }
                        else await CreateShortcutAsync(entry.Staging, entry.Source, token).ConfigureAwait(false);
                        await PrepareAndCommitAsync(entry, journal, path, token).ConfigureAwait(false);
                    }
                    entry.State = "Completed"; Save(path, journal); completed++;
                    progress?.Invoke(new(completed, failed, "完成：" + item.Name, path));
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception error) when (IsFileError(error))
                {
                    failed++; errors.Add(item.Name + "：" + error.Message);
                    if (entry is not null) { entry.Error = error.Message; Save(path, journal); }
                    progress?.Invoke(new(completed, failed, "保留/失败：" + item.Name, path));
                }
            }
            journal.Finished = !cancelled; Save(path, journal);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception error) when (IsFileError(error))
        { failed += Math.Max(1, items.Length - completed - failed); errors.Add(error.Message); }
        finally { gate.Release(); }
        return new OrganizerResult(completed, failed, cancelled, path, errors);
    });

    private static async Task CopyToStagingAsync(Stream source, JournalEntry entry, CancellationToken token)
    {
        FileSafety.SafePath(entry.Staging);
        using (var destination = new FileStream(entry.Staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            source.Position = 0;
            await source.CopyToAsync(destination, 81920, token).ConfigureAwait(false);
            destination.Flush(true);
            if (destination.Length != entry.Length) throw new IOException("写入长度校验失败，原文件已保留。");
        }
        using var verification = FileSafety.OpenLocked(entry.Staging, false);
        if (verification.Length != entry.Length || await FileSafety.HashAsync(verification, token).ConfigureAwait(false) != entry.Hash)
            throw new IOException("SHA256 校验失败，原文件已保留。");
    }

    private async Task PrepareAndCommitAsync(JournalEntry entry, OrganizerJournal journal, string path, CancellationToken token)
    {
        using (var file = FileSafety.OpenLocked(entry.Staging, false))
        {
            var hash = await FileSafety.HashAsync(file, token).ConfigureAwait(false);
            if (entry.Hash.Length > 0 && (entry.Hash != hash || entry.Length != file.Length)) throw new IOException("暂存文件校验失败。");
            entry.Hash = hash; entry.Length = file.Length; entry.DestinationIdentity = FileSafety.Identity(file);
        }
        entry.State = "Prepared"; Save(path, journal);
        token.ThrowIfCancellationRequested();
        FileSafety.SafePath(entry.Destination); FileSafety.SafePath(entry.Staging);
        File.Move(entry.Staging, entry.Destination, false);
        entry.State = "DestinationCommitted"; Save(path, journal);
    }

    public Task<OrganizerResult> UndoAsync(string journalPath, CancellationToken token, Action<OrganizerProgress>? progress = null) => Task.Run(async () =>
    {
        var journal = Load(journalPath); int completed = 0, failed = 0; bool cancelled = false; var errors = new List<string>();
        try { await gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new OrganizerResult(0, 0, true, journalPath, errors); }
        try
        {
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                if (entry.State == "Undone") continue;
                try
                {
                    await UndoEntryAsync(entry, journal, journalPath, token).ConfigureAwait(false);
                    entry.State = "Undone"; entry.Error = ""; Save(journalPath, journal); completed++;
                    progress?.Invoke(new(completed, failed, "已撤销：" + Path.GetFileName(entry.Source), journalPath));
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception error) when (IsFileError(error))
                {
                    failed++; errors.Add(Path.GetFileName(entry.Source) + "：" + error.Message);
                    entry.Error = error.Message; Save(journalPath, journal);
                }
            }
        }
        finally { gate.Release(); }
        return new OrganizerResult(completed, failed, cancelled, journalPath, errors);
    });

    private async Task UndoEntryAsync(JournalEntry entry, OrganizerJournal journal, string path, CancellationToken token)
    {
        FileSafety.SafePath(entry.Destination); FileSafety.SafePath(entry.Staging); FileSafety.SafePath(entry.Source);
        if (!File.Exists(entry.Destination))
        {
            if (Directory.Exists(entry.Destination)) throw new IOException("目标变成了文件夹，已保留。");
            if (File.Exists(entry.Staging))
            {
                if (entry.DestinationIdentity.Length == 0) throw new IOException("中断时留下未校验暂存文件，已保留供人工检查：" + entry.Staging);
                using var staging = FileSafety.OpenLocked(entry.Staging, true);
                await VerifyAsync(staging, entry.Hash, entry.Length, entry.DestinationIdentity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); FileSafety.DeleteLocked(staging);
            }
            else if (journal.Mode == OrganizationMode.Move && !File.Exists(entry.Source))
                throw new IOException("源与目标均缺失，无法自动恢复。");
            return;
        }
        if (entry.DestinationIdentity.Length == 0) throw new IOException("日志未记录已验证目标身份，已保留文件。");
        using var destination = FileSafety.OpenLocked(entry.Destination, true);
        await VerifyAsync(destination, entry.Hash, entry.Length, entry.DestinationIdentity, token).ConfigureAwait(false);
        if (journal.Mode == OrganizationMode.Move)
        {
            if (Directory.Exists(entry.Source)) throw new IOException("原位置被文件夹占用，保留目标。");
            if (File.Exists(entry.Source))
            {
                using var source = FileSafety.OpenLocked(entry.Source, false);
                var identity = FileSafety.Identity(source);
                bool originalUntouched = entry.State != "Completed" && identity == entry.SourceIdentity;
                bool alreadyRestored = entry.RestoreIdentity.Length > 0 && identity == entry.RestoreIdentity;
                if (!originalUntouched && !alreadyRestored) throw new IOException("原位置已有文件，拒绝覆盖并保留目标。");
                await VerifyAsync(source, entry.Hash, entry.Length, identity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); FileSafety.DeleteLocked(destination);
                return;
            }
            // Resume a verified restoration staging file; never abandon an older one silently.
            if (entry.RestoreStaging.Length > 0 && (File.Exists(entry.RestoreStaging) || Directory.Exists(entry.RestoreStaging)))
            {
                if (entry.RestoreIdentity.Length == 0) throw new IOException("撤销中断留下未验证暂存文件，已保留：" + entry.RestoreStaging);
                using var restored = FileSafety.OpenLocked(entry.RestoreStaging, false);
                await VerifyAsync(restored, entry.Hash, entry.Length, entry.RestoreIdentity, token).ConfigureAwait(false);
            }
            else
            {
                // Record restoration intent before writing, and identity before committing.
                entry.RestoreStaging = Path.Combine(entry.SourceRoot, ".naifrog-undo-" + Guid.NewGuid().ToString("N") + ".partial");
                entry.RestoreIdentity = ""; entry.State = "UndoPreparing"; Save(path, journal);
                var restore = new JournalEntry { Staging = entry.RestoreStaging, Hash = entry.Hash, Length = entry.Length };
                await CopyToStagingAsync(destination, restore, token).ConfigureAwait(false);
                using var restored = FileSafety.OpenLocked(entry.RestoreStaging, false);
                entry.RestoreIdentity = FileSafety.Identity(restored);
            }
            entry.State = "UndoPrepared"; Save(path, journal);
            token.ThrowIfCancellationRequested(); FileSafety.SafePath(entry.Source);
            File.Move(entry.RestoreStaging, entry.Source, false);
            entry.State = "UndoRestored"; Save(path, journal);
            // Keep the restored source locked and verified until the old target is deleted.
            using var restoredSource = FileSafety.OpenLocked(entry.Source, false);
            await VerifyAsync(restoredSource, entry.Hash, entry.Length, entry.RestoreIdentity, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); FileSafety.DeleteLocked(destination);
            return;
        }
        token.ThrowIfCancellationRequested(); FileSafety.DeleteLocked(destination);
    }

    private static async Task VerifyAsync(FileStream file, string hash, long length, string identity, CancellationToken token)
    {
        if (FileSafety.Identity(file) != identity || file.Length != length || await FileSafety.HashAsync(file, token).ConfigureAwait(false) != hash)
            throw new IOException("文件内容、类型或身份已变化，拒绝删除或覆盖，已保留。");
    }

    public IReadOnlyList<OrganizerJournalInfo> ListJournals()
    {
        var result = new List<OrganizerJournalInfo>();
        if (!Directory.Exists(JournalDirectory)) return result;
        foreach (var path in Directory.EnumerateFiles(FileSafety.SafePath(JournalDirectory), "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try
            {
                var journal = Load(path);
                result.Add(new(path, journal.CreatedUtc, journal.Mode.ToString(), journal.Entries.Count(x => x.State != "Undone"), !journal.Finished));
            }
            catch (Exception error) when (IsFileError(error)) { /* Corrupt logs are never used for recovery. */ }
        }
        return result;
    }

    private OrganizerJournal Load(string path)
    {
        try
        {
            path = FileSafety.SafePath(path);
            if (!FileSafety.Same(Path.GetDirectoryName(path)!, JournalDirectory) || new FileInfo(path).Length > 4_000_000)
                throw new InvalidDataException("仅允许恢复本应用操作日志目录内的日志。");
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(File.ReadAllText(path)) ?? throw new InvalidDataException("日志为空。");
            byte[] signature;
            try { signature = Convert.FromHexString(envelope.Signature); }
            catch (FormatException error) { throw new InvalidDataException("日志签名无效。", error); }
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(GetJournalKey(false), Encoding.UTF8.GetBytes(envelope.Payload))))
                throw new InvalidDataException("日志签名校验失败；已保留所有文件。");
            var journal = JsonSerializer.Deserialize<OrganizerJournal>(envelope.Payload) ?? throw new InvalidDataException("日志内容为空。");
            if (journal.Version != 1 || !Guid.TryParseExact(journal.Id, "N", out _) || Path.GetFileName(path) != journal.Id + ".json" ||
                !Path.IsPathFullyQualified(journal.TargetRoot) || !Enum.IsDefined(journal.Mode) || journal.Entries.Count > ScanLimit)
                throw new InvalidDataException("日志版本、标识或结构无效。");
            foreach (var entry in journal.Entries)
            {
                if (!Path.IsPathFullyQualified(entry.Source) || !Path.IsPathFullyQualified(entry.SourceRoot) ||
                    !FileSafety.Same(Path.GetDirectoryName(entry.Source)!, entry.SourceRoot) ||
                    !Path.IsPathFullyQualified(entry.Destination) || !FileSafety.IsWithin(entry.Destination, journal.TargetRoot) ||
                    !FileSafety.Same(Path.GetDirectoryName(entry.Destination)!, Path.GetDirectoryName(entry.Staging)!) ||
                    !Path.GetFileName(entry.Staging).StartsWith(".naifrog-", StringComparison.Ordinal) ||
                    (entry.RestoreStaging.Length > 0 && (!FileSafety.Same(Path.GetDirectoryName(entry.RestoreStaging)!, entry.SourceRoot) ||
                    !Path.GetFileName(entry.RestoreStaging).StartsWith(".naifrog-undo-", StringComparison.Ordinal))) ||
                    entry.Length < 0 || (entry.Hash.Length > 0 && (entry.Hash.Length != 64 || entry.Hash.Any(x => !Uri.IsHexDigit(x)))) ||
                    entry.State is not ("Intended" or "Prepared" or "DestinationCommitted" or "Completed" or "UndoPreparing" or "UndoPrepared" or "UndoRestored" or "Undone"))
                    throw new InvalidDataException("日志路径或状态校验失败，未执行任何恢复。");
            }
            return journal;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("无法安全读取操作日志：" + error.Message, error); }
    }

    private void Save(string path, OrganizerJournal journal)
    {
        var payload = JsonSerializer.Serialize(journal, JsonOptions);
        var signature = Convert.ToHexString(HMACSHA256.HashData(GetJournalKey(true), Encoding.UTF8.GetBytes(payload)));
        FileSafety.WriteJournal(path, JsonSerializer.SerializeToUtf8Bytes(new JournalEnvelope { Payload = payload, Signature = signature }, JsonOptions));
    }

    private byte[] GetJournalKey(bool create)
    {
        lock (this)
        {
            if (journalKey is not null) return journalKey;
            var path = Path.Combine(FileSafety.SafePath(JournalDirectory), "journal-key.dpapi");
            FileSafety.SafePath(path);
            if (!File.Exists(path))
            {
                if (!create) throw new InvalidDataException("日志签名密钥缺失，不能安全恢复。");
                Directory.CreateDirectory(JournalDirectory);
                var key = RandomNumberGenerator.GetBytes(32);
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    file.Write(LocalVault.Protect(key)); file.Flush(true);
                    journalKey = key; return key;
                }
                catch (IOException) when (File.Exists(path)) { /* Another engine created it first. */ }
            }
            journalKey = LocalVault.Unprotect(File.ReadAllBytes(FileSafety.SafePath(path)));
            if (journalKey.Length != 32) throw new InvalidDataException("日志签名密钥格式无效。");
            return journalKey;
        }
    }
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        System.ComponentModel.Win32Exception or COMException or NotSupportedException or TargetInvocationException or InvalidOperationException;

    private static Task CreateShortcutAsync(string path, string target, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null, shortcut = null;
            try
            {
                token.ThrowIfCancellationRequested(); FileSafety.SafePath(path);
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("快捷方式暂存位置已被占用。");
                var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new NotSupportedException("Windows 快捷方式服务不可用。");
                shell = Activator.CreateInstance(type)!;
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
                shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcut.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "奶蛙整理：原文件保留在原位置" });
                token.ThrowIfCancellationRequested();
                shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                using (var flush = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }) { IsBackground = true, Name = "Naifrog shortcut writer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
