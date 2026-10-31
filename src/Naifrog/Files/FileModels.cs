using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Naifrog.Files;

public enum OrganizationMode { Reference, Shortcut, Copy, Move }

public sealed class OrganizerItem : INotifyPropertyChanged
{
    private bool included = true;
    private string category = "其他";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public bool Included { get => included; set { included = value; Changed(); } }
    public string Category { get => category; set { category = value; Changed(); } }
    public string Kind => IsDirectory ? "文件夹（仅引用/快捷方式）" : "文件";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    internal OrganizerItem Snapshot() => new()
    {
        Id = Id, SourcePath = SourcePath, Name = Name, IsDirectory = IsDirectory,
        Length = Length, LastWriteUtc = LastWriteUtc, Category = Category, Included = Included
    };
}

public sealed record OrganizerScan(IReadOnlyList<OrganizerItem> Items, int Skipped, bool LimitReached);
public sealed record OrganizerProgress(int Completed, int Failed, string Message, string JournalPath);
public sealed record OrganizerResult(int Completed, int Failed, bool Cancelled, string? JournalPath, IReadOnlyList<string> Errors)
{
    public string Summary => $"{(Cancelled ? "已取消；" : "")}完成 {Completed} 项，失败/保留 {Failed} 项。" +
        (Errors.Count > 0 ? "\n" + string.Join("\n", Errors) : "") +
        (JournalPath is null ? "" : "\n操作日志：" + JournalPath);
}
public sealed record OrganizerJournalInfo(string Path, DateTime CreatedUtc, string Mode, int Recoverable, bool Incomplete)
{
    public override string ToString() => $"{CreatedUtc.ToLocalTime():MM-dd HH:mm:ss} · {Mode} · {Recoverable} 项" + (Incomplete ? " · 中断日志" : "");
}

internal sealed class OrganizerJournal
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string TargetRoot { get; set; } = "";
    public OrganizationMode Mode { get; set; }
    public bool Finished { get; set; }
    public List<JournalEntry> Entries { get; set; } = new();
}
internal sealed class JournalEnvelope
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}
internal sealed class JournalEntry
{
    public string Source { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Staging { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string DestinationIdentity { get; set; } = "";
    public string RestoreStaging { get; set; } = "";
    public string RestoreIdentity { get; set; } = "";
    public bool SourceIsDirectory { get; set; }
    public string Hash { get; set; } = "";
    public long Length { get; set; }
    public string State { get; set; } = "Intended";
    public string Error { get; set; } = "";
}
