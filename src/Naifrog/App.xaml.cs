using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Naifrog.Core;
using Naifrog.Desktop;
using Naifrog.UI;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Naifrog;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private bool ownsMutex;
    private Forms.NotifyIcon? tray;
    private Drawing.Icon? trayIcon;
    private PetRuntime? runtime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--render")
        {
            PetWindow.SavePreview(e.Args[1]);
            Shutdown();
            return;
        }

        Ui.Install();
        string? directory = null;
        int dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0 && dataIndex+1 < e.Args.Length) directory = Path.GetFullPath(e.Args[dataIndex+1]);
        var store = new SettingsStore(directory);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.DataDirectory.ToUpperInvariant())))[..16];
        instance = new Mutex(true, @"Local\Naifrog.DesktopPet." + identity, out ownsMutex);
        if (!ownsMutex && !store.Current.Data.AllowMultipleInstances) { Shutdown(); return; }

        runtime = new PetRuntime(store, configureDesktop: directory is null);
        var window = new PetWindow(runtime);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("工程工作台", null, (_, _) => Dispatcher.Invoke(window.ShowAgent));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(window.ShowSettings));
        menu.Items.Add("退出奶蛙", null, (_, _) => Dispatcher.Invoke(Shutdown));
        trayIcon = LoadTrayIcon();
        tray = new Forms.NotifyIcon
        {
            Text = "奶蛙 · 桌宠",
            Icon = trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        window.Show();
        if (e.Args.Contains("--settings")) window.Dispatcher.BeginInvoke(window.ShowSettings);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Naifrog;component/Assets/naifrog.ico"));
        using Stream stream = resource.Stream;
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runtime?.Dispose();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
