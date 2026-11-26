using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// Transparent, topmost, never-activated window at the top (or bottom) center of the primary monitor.
/// It's click-through everywhere except over the pill: WS_EX_TRANSPARENT is turned off only while
/// the cursor is inside the pill (polled with GetCursorPos 10×/s, like the Mac's ignoresMouseEvents).
/// </summary>
public partial class IslandWindow : Window
{
    public const double WindowWidth = 760;
    public const double WindowHeight = 420;
    public const double EdgeGap = 8;
    /// <summary>The Mac pads the pill's hover rect by 3 points on each side.</summary>
    private const double HoverPadding = 3;

    /// <summary>Within this distance of the pill (DIP) the cursor is polled ~64×/s instead of 10×/s.</summary>
    private const double NearDistance = 120;

    private readonly IslandModel _model;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _nearTimer;
    private long _nearTicks;
    private nint _hwnd;
    private bool _clickThrough = true;
    private int _ticks;
    private double _scale = 1;

    public IslandWindow(IslandModel model, IslandPlacement position)
    {
        InitializeComponent();
        _model = model;
        Position = position;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        // 15 ms: fires on every system tick (~64 Hz), never faster even if the timer resolution was raised.
        _nearTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _nearTimer.Tick += (_, _) => NearTick();
        SourceInitialized += OnSourceInitialized;
        _model.PropertyChanged += (_, _) => Pill.Render(_model);
        // Clicking the compact pill expands it right away (the Mac's expandNow()).
        Pill.CompactClicked += () => _model.ExpandNow();
        Pill.Clicked += message => Trace?.Invoke(message);
    }

    public IslandPlacement Position { get; private set; }

    /// <summary>Ctrl+Alt+L was pressed anywhere (Show Island).</summary>
    public event Action? HotkeyPressed;

    /// <summary>False when another app already owns Ctrl+Alt+L.</summary>
    public bool HotkeyRegistered { get; private set; }

    private const int HotkeyId = 0x4C49; // "LI"

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        style |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        style &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)style);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Ctrl+Alt+L, the Windows counterpart of the Mac's ⌥⌘L.
        HotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'L');
        Closed += (_, _) => Native.UnregisterHotKey(_hwnd, HotkeyId);
        PlaceOnPrimaryMonitor();
        Pill.Render(_model);
        _timer.Start();
    }

    public void SetPosition(IslandPlacement position)
    {
        Position = position;
        PlaceOnPrimaryMonitor();
    }

    /// <summary>Centers the window on the primary monitor's work area, in physical pixels.</summary>
    private void PlaceOnPrimaryMonitor()
    {
        if (_hwnd == 0) return;
        var (monitor, work, scale) = Native.PrimaryMonitor();
        _scale = scale;
        var width = (int)Math.Round(WindowWidth * scale);
        var height = (int)Math.Round(WindowHeight * scale);
        var x = work.Left + (work.Width - width) / 2;
        int y;
        if (Position == IslandPlacement.Top)
        {
            y = work.Top;
            Pill.AnchorTop = true;
            Pill.VerticalAlignment = VerticalAlignment.Top;
            Pill.Margin = new Thickness(0, EdgeGap, 0, 0);
        }
        else
        {
            // Let the window reach the bottom of the screen so the shadow can fall onto the taskbar.
            y = monitor.Bottom - height;
            Pill.AnchorTop = false;
            Pill.VerticalAlignment = VerticalAlignment.Bottom;
            Pill.Margin = new Thickness(0, 0, 0, (monitor.Bottom - work.Bottom) / scale + EdgeGap);
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
            return 0;
        }
        var reposition = msg switch
        {
            Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED => true,
            Native.WM_SETTINGCHANGE => wParam == Native.SPI_SETWORKAREA,
            _ => false,
        };
        // Let WPF handle WM_DPICHANGED first (it rescales the content), then move the window.
        if (reposition) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlaceOnPrimaryMonitor));
        return 0;
    }

    private readonly System.Diagnostics.Stopwatch _tickTime = new();
    private readonly System.Diagnostics.Stopwatch _fullscreenTime = new();

    private void Tick()
    {
        _tickTime.Start();
        _ticks++;
        // Only ask Windows about fullscreen apps when the setting that uses it is on (it's off by default).
        if (_ticks % 10 == 0 && _model.Settings.HideInFullscreen)
        {
            _fullscreenTime.Start();
            _model.IsFullscreen = Native.IsFullscreenAppActive();
            _fullscreenTime.Stop();
        }
        else if (!_model.Settings.HideInFullscreen && _model.IsFullscreen)
        {
            _model.IsFullscreen = false;
        }
        var (over, near) = Probe();
        _model.Tick(over);
        SetClickThrough(!_model.AcceptsMouse);
        Pill.Render(_model);
        // Close to the pill, poll faster so a quick click lands on the pill instead of falling through.
        if (near && !_nearTimer.IsEnabled) _nearTimer.Start();
        else if (!near && _nearTimer.IsEnabled) _nearTimer.Stop();
        _tickTime.Stop();
    }

    /// <summary>~64×/s while the cursor is near the pill: only click-through follows; hover timing stays on the 100 ms tick.</summary>
    private void NearTick()
    {
        _nearTicks++;
        // The pill's screen rect from the last 100 ms tick is good enough unless the pill is moving.
        var (over, near) = Probe(refreshRect: !Animator.IsIdle);
        if (!near)
        {
            _nearTimer.Stop();
            return;
        }
        // Same rule as IslandModel.AcceptsMouse: take input only over a visible pill.
        SetClickThrough(!(over && _model.Mode != IslandMode.Hidden));
    }

    /// <summary>Gets a line whenever the hover hit test flips (for <c>--log-lines</c>).</summary>
    public Action<string>? Trace { get; set; }

    private bool? _lastOver;
    private Rect? _pillRect;

    private bool CursorOverPill() => Probe().Over;

    /// <summary>Is the cursor over the pill (plus 3 DIP), and within <see cref="NearDistance"/> of it?</summary>
    private (bool Over, bool Near) Probe(bool refreshRect = true)
    {
        if (refreshRect || _pillRect is null) _pillRect = Pill.HitArea is { } area ? ScreenRect(area) : null;
        bool over = false, near = false;
        var gotCursor = Native.GetCursorPos(out var cursor);
        if (_pillRect is { } pill && gotCursor)
        {
            var around = pill;
            around.Inflate(NearDistance * _scale, NearDistance * _scale);
            near = around.Contains(cursor.X, cursor.Y);
            var rect = pill;
            rect.Inflate(HoverPadding * _scale, HoverPadding * _scale);
            over = rect.Contains(cursor.X, cursor.Y);
        }
        if (over != _lastOver)
        {
            _lastOver = over;
            if (Trace is { } trace)
            {
                var why = _pillRect is not { } r ? "no hit area (pill hidden or leaving)"
                    : !gotCursor ? "GetCursorPos failed"
                    : $"cursor {cursor.X},{cursor.Y} rect {r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}";
                trace($"hover {(over ? "in" : "out")}: {why}");
            }
        }
        return (over, near);
    }

    /// <summary>An element's bounds in screen pixels.</summary>
    private static Rect ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    private void SetClickThrough(bool clickThrough)
    {
        if (clickThrough == _clickThrough || _hwnd == 0) return;
        _clickThrough = clickThrough;
        var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        style = clickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)style);
        Trace?.Invoke($"click-through {(clickThrough ? "on" : "off")}");
    }

    /// <summary>What the window looks like to Windows, for <c>--diagnose</c>.</summary>
    public string Diagnose()
    {
        var sb = new StringBuilder();
        var (monitor, work, scale) = Native.PrimaryMonitor();
        Native.GetWindowRect(_hwnd, out var window);
        var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        sb.AppendLine($"position: {Position}");
        sb.AppendLine($"primary monitor: {monitor} work area: {work} scale: {scale:0.##}");
        sb.AppendLine($"window: {window} (expected {Math.Round(WindowWidth * scale)}x{Math.Round(WindowHeight * scale)})");
        sb.AppendLine($"WPF DPI scale: {System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX:0.##}");
        if (Pill.HitArea is { } area)
        {
            var r = ScreenRect(area);
            sb.AppendLine($"pill: {r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0} px");
            sb.AppendLine($"pill layout: {Pill.DescribeLayout()}");
        }
        else
        {
            sb.AppendLine("pill: hidden");
        }
        // Line index and position only: never write lyrics to a file.
        sb.AppendLine($"mode: {_model.Mode} line: {_model.LineIndex?.ToString() ?? "-"} position: {_model.CurrentTime:0.00} lyrics: {_model.LyricsStatus.State} player: {_model.PlayerName} canSeek: {_model.CanSeek}");
        sb.AppendLine($"ex-style: toolwindow={(style & Native.WS_EX_TOOLWINDOW) != 0} noactivate={(style & Native.WS_EX_NOACTIVATE) != 0}" +
                      $" layered={(style & Native.WS_EX_LAYERED) != 0} transparent(click-through)={(style & Native.WS_EX_TRANSPARENT) != 0}");
        Native.GetCursorPos(out var cursor);
        sb.AppendLine($"accepts mouse: {_model.AcceptsMouse}, expanded: {_model.IsExpanded}, cursor: {cursor.X},{cursor.Y}, over pill: {CursorOverPill()}");
        sb.AppendLine($"animator: {Animator.Describe()}");
        sb.AppendLine($"20 fps clock: {AnimatedElement.DescribeTicking()}");
        sb.AppendLine($"fast hover polling: {(_nearTimer.IsEnabled ? "on" : "off")}, {_nearTicks} fast ticks so far");
        sb.AppendLine($"ticks: {_ticks}, {_tickTime.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, _ticks):0} µs each on average; " +
                      $"fullscreen checks took {_fullscreenTime.Elapsed.TotalMilliseconds:0.0} ms in total");
        var renderMode = HwndSource.FromHwnd(_hwnd)?.CompositionTarget?.RenderMode.ToString() ?? "?";
        sb.AppendLine($"rendering: tier {System.Windows.Media.RenderCapability.Tier >> 16}, window render mode {renderMode}");
        sb.AppendLine($"font: {Theme.TextFont.Source} → {DescribeFont()}");
        return sb.ToString();
    }

    private static string DescribeFont()
    {
        var typeface = new System.Windows.Media.Typeface(Theme.TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out var glyphs)
            ? $"{glyphs.FamilyNames.Values.FirstOrDefault()} {glyphs.FaceNames.Values.FirstOrDefault()} ({Path.GetFileName(glyphs.FontUri.LocalPath)})"
            : "not found";
    }
}
