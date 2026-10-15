using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.Core;
using Island.Diagnostics;
using Island.Services;
using Island.UI;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Island;

public sealed class IslandWindow : Window
{
    private readonly Settings settings = Storage.LoadSettings();
    private readonly ActivityModel model;
    private readonly ChatService chat;
    private readonly SpotifyService spotify;
    private readonly IslandMotion motion = new();
    private readonly IslandSurface surface;
    private readonly HoverGate hover = new();
    private readonly MotionMetrics metrics = new();
    private readonly DispatcherTimer pointer;
    private Forms.NotifyIcon? tray;
    private HwndSource? source;
    private IntPtr hwnd;
    private bool expanded, subscribed, dragging, disposed, previousClick, previousEscape, closingSaved, savingBeforeClose;
    private double? lastFrame;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    private double lastDragTime, lastDragPosition;
    private Forms.Screen screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    private SettingsWindow? setup;
    private Native.POINT? diagnosticPointer;
    private int hostChanges;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public IslandWindow()
    {
        Title = "Island"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Width = 900; Height = 860; UseLayoutRounding = false; SnapsToDevicePixels = false;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        model = new(settings, Dispatcher); chat = new(settings); spotify = new(settings); surface = new(model, motion, chat, spotify) { ShowIdle = true }; surface.Refresh(); Content = surface;
        surface.NeedsMotion += Wake; surface.DestinationChanged += Destination; surface.ExpansionRequested += Expand;
        surface.SettingsRequested += ShowSettings; surface.Dragged += Drag; surface.DragReleased += ReleaseDrag;
        surface.ScratchpadFocusRequested += () => { Native.SetForegroundWindow(hwnd); Native.SetActiveWindow(hwnd); Activate(); surface.FocusScratchpad(); };
        model.Changed += Changed; model.Completed += () => { if (!Storage.Diagnostic) tray?.ShowBalloonTip(4000, "Island", "Your timer is complete.", Forms.ToolTipIcon.Info); };
        pointer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, (_, _) => Probe(), Dispatcher);
        SourceInitialized += (_, _) => InitializeNative();
        Loaded += async (_, _) =>
        {
            PositionHost(); ReducedMotion();
            if (Storage.Diagnostic) { await Exercise(); return; }
            await model.InitializeClipboard();
            if (Environment.GetCommandLineArgs().Contains("--clipboard")) model.EnableClipboard();
            if (settings.MediaSource.Length > 0) await model.Media.Connect(settings.MediaSource);
            if (chat.Accounts.Count > 0) { int index = chat.Accounts.FindIndex(a => a.ClientId == settings.AccountId); await chat.Select(Math.Max(0, index)); }
            if (Environment.GetCommandLineArgs().Contains("--open")) Expand(true);
            else Destination();
            if (!File.Exists(Path.Combine(Storage.Root, "settings.json")) || Environment.GetCommandLineArgs().Contains("--settings")) ShowSettings();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Expand(false); e.Handled = true; } };
        PreviewMouseDown += (_, e) =>
        {
            var current = e.OriginalSource as DependencyObject;
            while (current != null && current is not TextBox && current is not ComboBox)
                current = current is Visual ? VisualTreeHelper.GetParent(current) : current is FrameworkContentElement content ? content.Parent : null;
            if (current != null) { Native.SetForegroundWindow(hwnd); Native.SetActiveWindow(hwnd); Activate(); }
        };
        Closed += (_, _) => Cleanup();
        Closing += async (sender, e) =>
        {
            ProcessLifetime.Write("window-closing", "saved=" + closingSaved + " saving=" + savingBeforeClose);
            if (Storage.Diagnostic || closingSaved) return;
            e.Cancel = true; if (savingBeforeClose) return;
            savingBeforeClose = true;
            if (await surface.SaveScratchpad()) { closingSaved = true; _ = Dispatcher.BeginInvoke(Close); }
            else { savingBeforeClose = false; surface.SelectPage(3); Expand(true); }
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged; SystemEvents.UserPreferenceChanged += PreferenceChanged;
    }
    private void InitializeNative()
    {
        hwnd = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(hwnd); source?.AddHook(Hook); Native.ToolWindow(hwnd);
        if (!Storage.Diagnostic)
        {
            bool hotkey = Native.RegisterHotKey(hwnd, 1, 0x4000 | 1 | 2, 0x20); // MOD_NOREPEAT | ALT | CONTROL
            Native.AddClipboardFormatListener(hwnd); CreateTray();
            if (!hotkey) model.SetError("Ctrl+Alt+Space is in use. Open Island from its tray icon.");
        }
        // Layered-window alpha already passes clicks through the transparent pixels.
        // WS_EX_TRANSPARENT would also pass fast clicks through the visible controls.
        Native.ClickThrough(hwnd, false);
    }
    private IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312) { Expand(!expanded); if (expanded) Activate(); handled = true; }
        else if (message == 0x031D) Dispatcher.BeginInvoke(() => model.CaptureClipboard());
        else if (message == 0x0021) { handled = true; return (IntPtr)3; } // Hover and buttons do not steal foreground focus.
        else if (message is 0x02E0 or 0x007E) Dispatcher.BeginInvoke(PositionHost);
        return IntPtr.Zero;
    }
    private void CreateTray()
    {
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Island · Ctrl+Alt+Space", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open / collapse Island", null, (_, _) => Dispatcher.Invoke(() => Expand(!expanded)));
        menu.Items.Add("Settings and connections…", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("Ask AI…", null, (_, _) => Dispatcher.Invoke(() => { Expand(true); surface.SetDetail(3); Activate(); }));
        menu.Items.Add("Motion diagnostics…", null, (_, _) => Dispatcher.Invoke(ShowDiagnostics));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Island", null, (_, _) => Dispatcher.Invoke(() => { ProcessLifetime.Write("tray-quit"); Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => Expand(!expanded));
    }
    private void DisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(PositionHost);
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => Dispatcher.BeginInvoke(ReducMotionAndPosition);
    private void ReducMotionAndPosition() { ReducedMotion(); PositionHost(); }
    private void PositionHost()
    {
        screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Screen) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var dpi = VisualTreeHelper.GetDpi(this); double width = Math.Min(900, screen.Bounds.Width / dpi.DpiScaleX), height = Math.Min(860, screen.Bounds.Height / dpi.DpiScaleY);
        int physicalWidth = (int)Math.Round(width * dpi.DpiScaleX), physicalHeight = (int)Math.Round(height * dpi.DpiScaleY);
        int x = screen.Bounds.Left + (screen.Bounds.Width - physicalWidth) / 2, y = screen.Bounds.Top;
        if (Width != width || Height != height || Math.Abs(Left * dpi.DpiScaleX - x) > .5 || Math.Abs(Top * dpi.DpiScaleY - y) > .5)
        {
            Width = width; Height = height; Left = x / dpi.DpiScaleX; Top = y / dpi.DpiScaleY;
            Native.SetWindowPos(hwnd, (IntPtr)(-1), x, y, physicalWidth, physicalHeight, 0x10 | 0x40);
            hostChanges++;
        }
        surface.ResizeHost(width, height); Destination();
    }
    private void ReducedMotion()
    {
        surface.ReducedMotion = settings.ReduceMotion ?? !SystemParameters.ClientAreaAnimation;
        if (surface.ReducedMotion) { motion.Snap(); surface.Refresh(); Sleep(); } else Wake();
    }
    private void Changed()
    {
        if (disposed) return;
        surface.Refresh(); Destination();
    }
    private void Destination()
    {
        if (disposed) return;
        if (expanded) motion.SetExpanded(true, availableWidth: surface.AvailableWidth, extraHeight: surface.ExpandedHeight - 208, expandedWidth: surface.ExpandedWidth);
        else motion.SetExpanded(false, model.HasActivity ? surface.CompactWidth : 116, surface.AvailableWidth);
        if (surface.ReducedMotion) { motion.Snap(); surface.Apply(expanded); surface.Frame(Now); Sleep(); }
        else Wake();
    }
    public void Expand(bool value) => SetExpanded(value, suppressHover: true);
    private void SetExpanded(bool value, bool suppressHover)
    {
        expanded = value; if (!value) { if (suppressHover) hover.Suppress(); Keyboard.ClearFocus(); surface.SetDetail(0); }
        Destination();
    }
    private void Wake()
    {
        if (disposed || surface.ReducedMotion || subscribed) return;
        if (motion.IsSettled && !surface.NeedsFrames && !dragging) { surface.Apply(expanded); return; }
        lastFrame = null; lastRenderingTime = TimeSpan.MinValue; subscribed = true; CompositionTarget.Rendering += Frame;
    }
    private void Sleep() { if (!subscribed) return; CompositionTarget.Rendering -= Frame; subscribed = false; lastFrame = null; }
    private void Frame(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs rendering && rendering.RenderingTime == lastRenderingTime) return;
        double? renderInterval = null;
        double renderTime = Now;
        if (args is RenderingEventArgs timestamp)
        {
            renderTime = timestamp.RenderingTime.TotalSeconds;
            if (lastRenderingTime != TimeSpan.MinValue) renderInterval = (timestamp.RenderingTime - lastRenderingTime).TotalSeconds;
            lastRenderingTime = timestamp.RenderingTime;
        }
        double start = Now; long before = GC.GetAllocatedBytesForCurrentThread(); double? interval = lastFrame.HasValue ? start - lastFrame : null;
        lastFrame = start; motion.Advance(renderInterval ?? 1.0 / 60, dragging);
        if (motion.IsSettled && !dragging) motion.Snap();
        surface.Apply(expanded); surface.Frame(renderTime);
        if (!surface.BezelConnected) metrics.DisconnectedShapeFrames++;
        metrics.Record(interval, renderInterval, Now - start, GC.GetAllocatedBytesForCurrentThread() - before);
        if (!surface.NeedsFrames && !dragging) Sleep();
    }
    private void Probe()
    {
        if (disposed || (Storage.Diagnostic && diagnosticPointer == null)) return;
        Native.POINT point;
        if (diagnosticPointer is Native.POINT injected) point = injected; else if (!Native.GetCursorPos(out point)) return;
        Point local = PointFromScreen(new Point(point.X, point.Y));
        bool capsule = surface.Contains(local), inside = capsule || HoverGate.BezelContains(point.X, point.Y, screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, VisualTreeHelper.GetDpi(this).DpiScaleX);
        bool click = (Native.GetAsyncKeyState(1) & 0x8000) != 0 || (Native.GetAsyncKeyState(2) & 0x8000) != 0;
        bool escape = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
        if (!Storage.Diagnostic && expanded && ((click && !previousClick && !inside) || (escape && !previousEscape))) Expand(false);
        previousClick = click; previousEscape = escape;
        bool? request = hover.Sample(inside, expanded, dragging || surface.PreventsCollapse, Now);
        if (request.HasValue) SetExpanded(request.Value, suppressHover: false);
    }
    private void Drag(double delta, bool first)
    {
        double position = Math.Sign(delta) * 24 * Math.Abs(delta) / (24 + Math.Abs(delta)), now = Now;
        motion.Height.Velocity = first ? 0 : .8 * motion.Height.Velocity + .2 * (position - lastDragPosition) / Math.Max(.001, now - lastDragTime);
        motion.Height.Value = Math.Max(4, motion.Height.Target + position); dragging = true; lastDragPosition = position; lastDragTime = now; Wake();
    }
    private void ReleaseDrag() { dragging = false; if (Now - lastDragTime > .1) motion.Height.Velocity = 0; Destination(); }
    public void ShowSettings()
    {
        if (setup != null) { setup.Activate(); return; }
        setup = new(model, spotify, name => { settings.Screen = name; model.Persist(); PositionHost(); }, ReducedMotion, ShowDiagnostics);
        setup.Closed += (_, _) => setup = null; setup.Show();
    }
    public void Reveal() => Expand(true);
    private void ShowDiagnostics()
    {
        string report = JsonSerializer.Serialize(metrics.Snapshot(), new JsonSerializerOptions { WriteIndented = true });
        MessageBox.Show(report, "Island · Motion diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void SaveRender(string path)
    {
        surface.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            // A neutral diagnostic backdrop makes the alpha edge inspectable.
            // Only Island's own visual tree is captured, never other applications.
            drawing.DrawRectangle(Theme.Brush("#526276"), null, new Rect(0, 0, Width, Height));
        }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private async Task Exercise()
    {
        string[] args = Environment.GetCommandLineArgs(); int reportIndex = Array.IndexOf(args, "--report");
        string reportPath = reportIndex >= 0 && reportIndex + 1 < args.Length ? Path.GetFullPath(args[reportIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "motion-report.json");
        string directory = Path.GetDirectoryName(reportPath)!; Directory.CreateDirectory(directory);
        string renders = Path.Combine(directory, "renders"); Directory.CreateDirectory(renders);
        try
        {
            if (args.Contains("--startup-check"))
            {
                surface.ReducedMotion = true;
                diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + 10, Y = screen.Bounds.Top + 300 };
                model.StopTimer(); model.Media.Fixture(false); Expand(false);
                await Task.Delay(100);
                bool visibleWithoutActivity = surface.Contains(new Point(Width / 2, 16));
                SaveRender(Path.Combine(renders, "startup-idle.png"));
                diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + 3 };
                Expand(true); await Task.Delay(100); bool opens = expanded;
                diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + 10, Y = screen.Bounds.Top + 300 };
                Expand(false); await Task.Delay(100);
                bool visibleAfterCollapse = surface.Contains(new Point(Width / 2, 16));
                bool passed = visibleWithoutActivity && opens && visibleAfterCollapse;
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, visibleWithoutActivity, opens, visibleAfterCollapse }, new JsonSerializerOptions { WriteIndented = true }));
                Application.Current.Shutdown(passed ? 0 : 2); return;
            }
            if (args.Contains("--interaction-check"))
            {
                // Exercise the real window coordinator: pure HoverGate tests cannot
                // detect Settings accidentally disabling pointer polling.
                surface.ReducedMotion = true; model.Media.Fixture(true); ShowSettings();
                void PointAt(bool inside)
                {
                    diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + (inside ? screen.Bounds.Width / 2 : 10), Y = screen.Bounds.Top + (inside ? 3 : 300) };
                    Probe();
                }
                Expand(false); PointAt(false); await Task.Delay(100);
                PointAt(true); await Task.Delay(300); bool opensWithSettings = expanded;
                Expand(true); PointAt(false); await Task.Delay(650); bool leavesWithSettings = !expanded;
                PointAt(true); await Task.Delay(300); bool reopensWithSettings = expanded;
                Expand(true); surface.SelectPage(1); surface.UpdateLayout();
                TextBox? FindInput(DependencyObject parent)
                {
                    if (parent is TextBox input && System.Windows.Automation.AutomationProperties.GetName(input) == "Timer duration in minutes") return input;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                        if (FindInput(VisualTreeHelper.GetChild(parent, i)) is TextBox found) return found;
                    return null;
                }
                Activate(); var entry = FindInput(surface); entry?.Focus();
                bool inputFocused = entry?.IsKeyboardFocused == true;
                PointAt(false); await Task.Delay(650); bool leavesWithInputFocus = !expanded;
                setup?.Close(); PointAt(true); await Task.Delay(300); bool opensWithoutSettings = expanded;
                PointAt(false); await Task.Delay(650); bool leavesWithoutSettings = !expanded;
                bool passed = opensWithSettings && leavesWithSettings && reopensWithSettings && inputFocused && leavesWithInputFocus && opensWithoutSettings && leavesWithoutSettings;
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, opensWithSettings, leavesWithSettings, reopensWithSettings, inputFocused, leavesWithInputFocus, opensWithoutSettings, leavesWithoutSettings }, new JsonSerializerOptions { WriteIndented = true }));
                Application.Current.Shutdown(passed ? 0 : 2); return;
            }
            if (args.Contains("--render-only"))
            {
                surface.ReducedMotion = true; model.Media.Fixture(true); model.StartTimer(25); Expand(true);
                for (int i = 0; i < 3; i++) ClipboardHistory.Add(model.ClipboardItems, ClipboardHistory.Identify(new("Preview copy " + (i + 1), Text: "Local clipboard fixture " + (i + 1))));
                foreach (var entry in new[] { (0, "media"), (1, "timer"), (2, "clipboard"), (3, "scratchpad") })
                {
                    surface.SelectPage(entry.Item1); await Task.Delay(100); SaveRender(Path.Combine(renders, entry.Item2 + ".png"));
                    if (entry.Item1 == 2)
                    {
                        ((System.Windows.Controls.Button)FindNamed(surface, "Older copy")!).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Task.Delay(100); SaveRender(Path.Combine(renders, "clipboard-older.png"));
                    }
                    if (entry.Item1 == 3)
                    {
                        var note = (TextBox)FindNamed(surface, "Scratchpad note")!;
                        note.Text = "Quick ideas\nPick up where you left off.\nNotes save automatically.";
                        await surface.SaveScratchpad(); await Task.Delay(100); SaveRender(Path.Combine(renders, "scratchpad-note.png"));
                    }
                }
                surface.SetDetail(2); await Task.Delay(100); SaveRender(Path.Combine(renders, "spotify-library.png"));
                FrameworkElement? FindNamed(DependencyObject parent, string name)
                {
                    if (parent is FrameworkElement element && System.Windows.Automation.AutomationProperties.GetName(element) == name) return element;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                        if (FindNamed(VisualTreeHelper.GetChild(parent, i), name) is { } found) return found;
                    return null;
                }
                // Exercise the visible Search tab, input and Enter submission in
                // the isolated fixture, without accessing an account or playback.
                var searchTab = (System.Windows.Controls.Button)FindNamed(surface, "Search")!;
                searchTab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Task.Delay(100); SaveRender(Path.Combine(renders, "spotify-search-empty.png"));
                var searchInput = (TextBox)FindNamed(surface, "Search Spotify tracks")!;
                searchInput.Text = "A moment of focus";
                searchInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                await Task.Delay(100); SaveRender(Path.Combine(renders, "spotify-search-results.png"));
                chat.Fixture(); surface.SetDetail(3); await Task.Delay(100); SaveRender(Path.Combine(renders, "chat.png"));
                surface.SetDetail(0); Expand(false); await Task.Delay(100); SaveRender(Path.Combine(renders, "compact.png"));
                model.StopTimer(); await Task.Delay(100); SaveRender(Path.Combine(renders, "compact-music.png"));
                Application.Current.Shutdown(); return;
            }
            surface.ReducedMotion = false; model.Media.Fixture(true); model.StartTimer(25); Expand(true); await Task.Delay(1800);
            metrics.Reset(); int initialHost = hostChanges;
            for (int i = 0; i < 48; i++) { Expand(i % 2 == 0); await Task.Delay(140); }
            Expand(true); await Task.Delay(1000);
            for (int i = 0; i < 32; i++) { surface.SelectPage(i % 4); await Task.Delay(140); }
            surface.SelectPage(0); await Task.Delay(2000);
            metrics.HostChanges = hostChanges - initialHost; var measured = metrics.Snapshot();
            foreach (var entry in new[] { (0, "media"), (1, "timer"), (2, "clipboard"), (3, "scratchpad") })
            { surface.SelectPage(entry.Item1); await Task.Delay(800); SaveRender(Path.Combine(renders, entry.Item2 + ".png")); }
            surface.SetDetail(2); await Task.Delay(1000); SaveRender(Path.Combine(renders, "spotify-library.png"));
            chat.Fixture(); surface.SetDetail(3); await Task.Delay(1000); SaveRender(Path.Combine(renders, "chat.png"));
            surface.SetDetail(0); surface.SelectPage(0); Expand(false); await Task.Delay(1000); SaveRender(Path.Combine(renders, "compact.png"));
            model.StopTimer(); model.Media.Fixture(false); Expand(false); await Task.Delay(1400);
            bool idleHidden = motion.Height.Value == 0 && !subscribed;
            diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + 10, Y = screen.Bounds.Top + 200 }; Probe(); await Task.Delay(100);
            diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top }; Probe(); await Task.Delay(900);
            bool edgeOpens = expanded && motion.Height.Value > 200;
            Expand(false); await Task.Delay(500); bool escapeSuppressed = !expanded;
            diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + 10, Y = screen.Bounds.Top + 200 }; Probe(); await Task.Delay(100);
            diagnosticPointer = new Native.POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top }; Probe(); await Task.Delay(900);
            bool edgeReopens = expanded && motion.Height.Value > 200;
            surface.ReducedMotion = true; Expand(false); bool reducedSettles = motion.IsSettled;
            var result = new { timestamp = DateTimeOffset.UtcNow, platform = Environment.OSVersion.ToString(), motion = measured, interaction = new { idleHidden, edgeOpens, escapeSuppressed, edgeReopens, reducedSettles }, renders, limitation = "Synthetic interaction and WPF callback measurement. No live account authorization, physical input, or GPU present-time capture performed." };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(idleHidden && edgeOpens && escapeSuppressed && edgeReopens && reducedSettles ? 0 : 2);
        }
        catch (Exception e) { await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { error = e.ToString() })); Application.Current.Shutdown(3); }
    }
    private void Cleanup()
    {
        disposed = true; Sleep(); pointer.Stop(); model.Dispose(); chat.Dispose(); spotify.Dispose(); surface.Volume.Dispose();
        Native.UnregisterHotKey(hwnd, 1); Native.RemoveClipboardFormatListener(hwnd); source?.RemoveHook(Hook);
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        tray?.Dispose(); setup?.Close(); Application.Current.Shutdown();
    }
}
