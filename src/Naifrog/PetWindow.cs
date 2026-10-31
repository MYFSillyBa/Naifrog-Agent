using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Naifrog.Desktop;
using Naifrog.UI;

namespace Naifrog;

internal sealed class PetWindow : Window
{
    private readonly PetRuntime runtime;
    private readonly System.Windows.Controls.Image character;
    private SettingsWindow? settingsWindow;
    private ChatWindow? chatWindow;
    private AgentWindow? agentWindow;
    private SpeechBubble? speech;
    private readonly DispatcherTimer movement = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Point press, origin, lastPointer;
    private bool pointerDown;
    public bool IsDragging { get; private set; }
    public bool IsMoving => movement.IsEnabled;
    public bool IsUiBusy => settingsWindow?.IsVisible == true || chatWindow?.IsVisible == true || agentWindow?.IsVisible == true || ContextMenu?.IsOpen == true || speech?.IsMouseOver == true;
    public string HotkeyStatus { get; private set; } = "未启用";
    private Point destination;
    private bool greetOnArrival;
    private int shakeDirection, shakes;
    private DateTime shakeAt;
    private HwndSource? source;
    private bool positionInitialized;

    public PetWindow(PetRuntime runtime)
    {
        this.runtime = runtime;
        Title = "奶蛙"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        UseLayoutRounding = true; AllowDrop = false;
        character = CreateCharacter(); Content = character;
        var menu = new ContextMenu();
        var agent = new MenuItem { Header = "工程工作台" }; agent.Click += (_, _) => ShowAgent(); menu.Items.Add(agent);
        var settings = new MenuItem { Header = "设置" }; settings.Click += (_, _) => ShowSettings();
        var exit = new MenuItem { Header = "退出" }; exit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(settings); menu.Items.Add(exit); ContextMenu = menu;
        menu.Opened += (_, _) => { runtime.Touch(); StopMoving(); };
        character.MouseLeftButtonDown += PointerDown;
        character.MouseMove += PointerMove;
        character.MouseLeftButtonUp += PointerUp;
        character.LostMouseCapture += (_, _) => EndPointer(false);
        movement.Tick += MoveTick;
        Loaded += (_, _) => { runtime.Attach(this, character); ApplySettings(); };
        SourceInitialized += (_, _) => { source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowHook); };
        LocationChanged += (_, _) => speech?.Reposition();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) =>
        {
            movement.Stop(); movement.Tick -= MoveTick; UnregisterKeys(); source?.RemoveHook(WindowHook);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            speech?.Close();
            agentWindow?.Close();
        };
    }
    public void ApplySettings()
    {
        var settings = runtime.Store.Current.Appearance;
        Height = settings.Height; Width = settings.Height * 256 / 330; Opacity = settings.Opacity; Topmost = settings.Topmost;
        if (!positionInitialized)
        {
            positionInitialized = true;
            if (settings.RememberPosition && settings.Left is double x && settings.Top is double y) { Left = x; Top = y; }
            else PlaceAtHome();
        }
        ClampPosition(); ConfigureKeys();
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ClampPosition);
    private Point PointerPosition()
    {
        if (!NativeDesktop.GetCursorPos(out var p)) return new Point(Left, Top);
        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return matrix.Transform(new Point(p.X, p.Y));
    }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (ContextMenu?.IsOpen == true) return;
        runtime.Touch(); StopMoving(); pointerDown = true; IsDragging = false;
        press = lastPointer = PointerPosition(); origin = new Point(Left, Top); shakes = 0;
        character.CaptureMouse(); e.Handled = true;
    }
    private void PointerMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!pointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = PointerPosition(); Vector delta = point - press;
        if (!runtime.Store.Current.Appearance.DragEnabled) return;
        if (!IsDragging && delta.Length < 5) return;
        IsDragging = true; runtime.Animator?.SetBlocked(true);
        Left = origin.X + delta.X; Top = origin.Y + delta.Y;
        double dx = point.X - lastPointer.X;
        if (Math.Abs(dx) > 3)
        {
            int direction = Math.Sign(dx); if ((DateTime.Now-shakeAt).TotalMilliseconds > 700) shakes = 0;
            if (direction != shakeDirection) { shakes++; shakeDirection = direction; shakeAt = DateTime.Now; }
        }
        lastPointer = point;
    }
    private void PointerUp(object sender, MouseButtonEventArgs e) { EndPointer(true); e.Handled = true; }
    private void EndPointer(bool openChat)
    {
        if (!pointerDown) return;
        bool dragged = IsDragging; pointerDown = false; IsDragging = false;
        if (character.IsMouseCaptured) character.ReleaseMouseCapture();
        runtime.Animator?.SetBlocked(false);
        if (dragged)
        {
            ClampPosition(); SavePosition(); if (shakes >= 6) runtime.Emote("dizzy", 5000);
        }
        else if (openChat) ShowChat();
    }
    private void SavePosition()
    {
        if (runtime.Store.Current.Appearance.RememberPosition)
            try { runtime.Store.SavePosition(Left, Top); } catch (Exception e) { runtime.Notify("位置保存失败：" + e.Message, false); }
    }
    public void ResetPosition() { StopMoving(); PlaceAtHome(); SavePosition(); }
    private void PlaceAtHome()
    { Rect area = SystemParameters.WorkArea; Left = Math.Max(area.Left, area.Right-Width-32); Top = Math.Max(area.Top, area.Bottom-Height-8); }
    private void ClampPosition()
    {
        Rect desktop = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) { PlaceAtHome(); return; }
        Left = Math.Clamp(Left, desktop.Left, Math.Max(desktop.Left, desktop.Right-Width));
        Top = Math.Clamp(Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom-Height));
    }
    public void Summon()
    {
        runtime.Touch(); Point cursor = PointerPosition();
        StartMoving(new Point(cursor.X-Width/2, cursor.Y-Height+25), true);
    }
    public void Wander()
    {
        Rect area = SystemParameters.WorkArea;
        StartMoving(new Point(Math.Clamp(Left+Random.Shared.Next(-110, 111), area.Left, Math.Max(area.Left, area.Right-Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom-Height))), false);
    }
    private void StartMoving(Point goal, bool greet)
    {
        double x = SystemParameters.VirtualScreenLeft, y = SystemParameters.VirtualScreenTop;
        destination = new Point(Math.Clamp(goal.X, x, Math.Max(x, x+SystemParameters.VirtualScreenWidth-Width)), Math.Clamp(goal.Y, y, Math.Max(y, y+SystemParameters.VirtualScreenHeight-Height)));
        greetOnArrival = greet; runtime.Animator?.SetBlocked(true); movement.Start();
    }
    private void MoveTick(object? sender, EventArgs e)
    {
        Vector delta = destination - new Point(Left, Top);
        if (delta.Length < 5) { StopMoving(); ClampPosition(); SavePosition(); if (greetOnArrival) runtime.Emote("greet", 4000); return; }
        delta.Normalize(); Left += delta.X*5; Top += delta.Y*5; ClampPosition();
        if (Math.Abs(Left-destination.X) > SystemParameters.VirtualScreenWidth || Math.Abs(Top-destination.Y) > SystemParameters.VirtualScreenHeight) StopMoving();
    }
    private void StopMoving() { movement.Stop(); runtime.Animator?.SetBlocked(false); }
    public void ShowSettings()
    {
        runtime.Touch(); StopMoving();
        if (settingsWindow is null) { settingsWindow = new SettingsWindow(runtime); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Show(); if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }
    public void ShowChat(byte[]? image = null)
    {
        runtime.Touch(); StopMoving(); speech?.Hide();
        if (chatWindow is null) { chatWindow = new ChatWindow(runtime); chatWindow.Closed += (_, _) => chatWindow = null; }
        if (image is not null) chatWindow.AttachImage(image);
        chatWindow.Show(); if (chatWindow.WindowState == WindowState.Minimized) chatWindow.WindowState = WindowState.Normal; chatWindow.Activate();
    }
    public void RefreshChatHistory() => chatWindow?.ReloadHistory();
    public void ShowAgent()
    {
        runtime.Touch(); StopMoving();
        if (agentWindow is null) { agentWindow = new AgentWindow(runtime); agentWindow.Closed += (_, _) => agentWindow = null; }
        agentWindow.Show(); if (agentWindow.WindowState == WindowState.Minimized) agentWindow.WindowState = WindowState.Normal; agentWindow.Activate();
    }
    public void Speak(string text)
    {
        if (chatWindow?.IsVisible == true) return;
        speech ??= new SpeechBubble(this, ShowChat);
        speech.SetText(text);
    }
    private void ConfigureKeys()
    {
        if (source is null) return;
        UnregisterKeys();
        if (!runtime.Store.Current.Data.GlobalHotkeys) { HotkeyStatus = "未启用"; return; }
        bool chat = NativeDesktop.RegisterHotKey(source.Handle, 101, 0x4003, 0x54);
        bool summon = NativeDesktop.RegisterHotKey(source.Handle, 102, 0x4003, 0x48);
        bool pause = NativeDesktop.RegisterHotKey(source.Handle, 103, 0x4003, 0x50);
        HotkeyStatus = chat && summon && pause ? "Ctrl+Alt+T 聊天 / H 召唤 / P 暂停" : "部分快捷键被其他程序占用，设置页入口仍可用";
    }
    private void UnregisterKeys() { if (source is not null) for (int id = 101; id <= 103; id++) NativeDesktop.UnregisterHotKey(source.Handle, id); }
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            handled = true;
            switch (wParam.ToInt32()) { case 101: ShowChat(); break; case 102: Summon(); break; case 103: runtime.TogglePause(); break; }
        }
        return IntPtr.Zero;
    }
    internal static System.Windows.Controls.Image CreateCharacter()
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri("pack://application:,,,/Naifrog;component/Assets/naifrog.png");
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
        var image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); return image;
    }
    internal static void SavePreview(string path)
    {
        var character = CreateCharacter(); var size = new Size(768, 990); character.Measure(size); character.Arrange(new Rect(size)); character.UpdateLayout();
        var bitmap = new RenderTargetBitmap(768, 990, 96, 96, PixelFormats.Pbgra32); bitmap.Render(character);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); png.Save(output);
    }
}
