using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Naifrog.Core;

namespace Naifrog.Files;

public sealed class FileOrganizerService
{
    private readonly SettingsStore store;
    private readonly OrganizerEngine engine;
    public FileOrganizerService(SettingsStore store) { this.store = store; engine = new(store); }

    public async Task<string> ReceiveAsync(string[] paths, CancellationToken token)
    {
        if (!store.Current.Files.ReceiveEnabled) return "文件接收已关闭，可在集中设置中启用。";
        if (paths.Length > OrganizerEngine.ScanLimit) return $"一次最多接收 {OrganizerEngine.ScanLimit} 个文件，请分批拖入。";
        var settings = store.Current.Files;
        var target = string.IsNullOrWhiteSpace(settings.ReceiveFolder) ? Path.Combine(store.DataDirectory, "ReceivedFiles") : settings.ReceiveFolder;
        var items = new List<OrganizerItem>(); var errors = new List<string>();
        foreach (var path in paths)
        {
            if (token.IsCancellationRequested) return "已取消接收，原文件均保留。";
            try
            {
                var full = FileSafety.SafePath(path);
                if (Directory.Exists(full)) throw new IOException("接收仅支持普通文件；文件夹可在整理窗口建立引用。");
                var file = new FileInfo(full);
                if (!file.Exists) throw new FileNotFoundException("文件不存在。");
                items.Add(new OrganizerItem { SourcePath = full, Name = file.Name, Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(Path.GetFileName(path) + "：" + error.Message); }
        }
        if (items.Count == 0) return "没有可接收的普通文件。" + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors));
        var result = await engine.ReceiveAsync(items.ToArray(), target,
            settings.ReceiveMode == "Move" ? OrganizationMode.Move : OrganizationMode.Copy, token).ConfigureAwait(false);
        return $"文件接收（{(settings.ReceiveMode == "Move" ? "移动" : "复制，原文件保留")}）\n" + result.Summary +
            (errors.Count == 0 ? "" : "\n未接收：\n" + string.Join("\n", errors));
    }
}
