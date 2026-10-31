using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Naifrog.Files;

internal static class FileSafety
{
    // Reject junctions/symlinks in every existing component, including broken links.
    public static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择有效路径。");
        var full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝符号链接或重解析路径：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public static string Category(string category)
    {
        if (category is null || !Regex.IsMatch(category, @"\A[\p{L}\p{N}][\p{L}\p{N} _-]{0,31}\z") ||
            Regex.IsMatch(category, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase) ||
            category.EndsWith(' '))
            throw new ArgumentException("分类须为 1–32 个汉字、字母、数字、空格、下划线或连字符，不能使用路径或系统保留名称。");
        return category;
    }

    public static string AvailablePath(string directory, string filename)
    {
        SafePath(directory);
        if (Path.GetFileName(filename) != filename || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件名称无效。");
        var extension = Path.GetExtension(filename); var stem = Path.GetFileNameWithoutExtension(filename);
        for (int i = 0; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, i == 0 ? filename : $"{stem} ({i}){extension}");
            try { File.GetAttributes(candidate); }
            catch (FileNotFoundException) { return candidate; }
            catch (DirectoryNotFoundException) { return candidate; }
        }
        throw new IOException("同名文件过多，请换一个目标目录。");
    }

    public static async Task<string> HashAsync(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        stream.Position = 0;
        return hash;
    }

    // On Windows the delete-capable handle locks out writers and renames. Deletion marks
    // this exact verified file for removal, avoiding a close-then-delete pathname race.
    public static FileStream OpenLocked(string path, bool mayDelete)
    {
        SafePath(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("此操作只接受普通文件。");
        var handle = CreateFile(path, 0x80000000u | (mayDelete ? 0x00010000u : 0u), 1, IntPtr.Zero, 3, 0x00200000u | 0x08000000u, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0) throw new IOException("拒绝链接或文件夹。");
            return new FileStream(handle, FileAccess.Read, 81920, false);
        }
        catch { handle.Dispose(); throw; }
    }

    public static string Identity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.Links > 1) throw new IOException("为避免影响其他硬链接，此操作拒绝具有多个硬链接的文件。");
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    public static void DeleteLocked(FileStream stream)
    {
        var disposition = new Disposition { Delete = true };
        if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void WriteJournal(string path, byte[] bytes)
    {
        SafePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); SafePath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        SafePath(path); File.Move(temp, path, true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition info, uint size);
}
