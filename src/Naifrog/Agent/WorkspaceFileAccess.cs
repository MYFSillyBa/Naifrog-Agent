using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Naifrog.Agent;

/// <summary>Validate the actual Windows file handle, rather than trusting only its lexical path.</summary>
internal static class WorkspaceFileAccess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref int information, uint length);

    public static FileStream Open(string path, string root, FileAccess access, bool delete = false, bool createNew = false)
    {
        uint desiredAccess = 0x80000000; // GENERIC_READ
        if (access != FileAccess.Read) desiredAccess |= 0x40000000; // GENERIC_WRITE
        if (delete) desiredAccess |= 0x00010000; // DELETE
        var handle = CreateFileW(path, desiredAccess, access == FileAccess.Read ? 1u : 0u,
            IntPtr.Zero, createNew ? 1u : 3u, 0x00200000, IntPtr.Zero); // OPEN_REPARSE_POINT
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException(new Win32Exception(error).Message); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if ((info.Attributes & 0x410) != 0) throw new UnauthorizedAccessException("Directories and reparse points are not supported as text files.");
            if (info.Links > 1) throw new UnauthorizedAccessException("Hardlinked files are not allowed.");
            var final = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) throw new IOException("Could not resolve file handle path.");
            string actual = final.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Opened file escapes workspace.");
            return new FileStream(handle, access, 4096, false);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void Delete(FileStream file)
    {
        int delete = 1;
        if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref delete, sizeof(int)))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
