using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Naifrog.Desktop;

internal static class NativeDesktop
{
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    public static bool HasExcludedWindow(System.Drawing.Rectangle screen, string excluded)
    {
        var names = excluded.Split(new[] { ',', ';', '\n', '\r', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return false;
        bool found = false;
        EnumProc callback = (window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var r)) return true;
            if (!screen.IntersectsWith(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))) return true;
            GetWindowThreadProcessId(window, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                foreach (var name in names)
                    if (process.ProcessName.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)) { found = true; return false; }
            }
            catch (Exception) { }
            return true;
        };
        EnumWindows(callback, IntPtr.Zero); GC.KeepAlive(callback); return found;
    }

    public static void ConfigureStartup(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) run.SetValue("Naifrog", "\"" + Environment.ProcessPath + "\"");
        else run.DeleteValue("Naifrog", false);
    }
    public static void CreateShortcut()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("快捷方式服务不可用。");
        object shell = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "奶蛙桌宠.lnk"));
            dynamic link = shortcut;
            link.TargetPath = Environment.ProcessPath; link.WorkingDirectory = AppContext.BaseDirectory; link.IconLocation = Environment.ProcessPath + ",0";
            link.Description = "奶蛙桌宠"; link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
