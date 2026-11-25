using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

public enum IslandPosition
{
    /// <summary>Top center, 8 px below the top of the work area.</summary>
    Top,
    /// <summary>Bottom center, 8 px above the taskbar.</summary>
    Bottom,
}

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

    private readonly IslandModel _model;
    private readonly DispatcherTimer _timer;
    private nint _hwnd;
    private bool _clickThrough = true;
    private int _ticks;
    private double _scale = 1;

    public IslandWindow(IslandModel model, IslandPosition position)
    {
        InitializeComponent();
        _model = model;
        Position = position;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += OnSourceInitialized;
        _model.PropertyChanged += (_, _) => Pill.Render(_model);
    }

    public IslandPosition Position { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        style |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        style &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)style);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PlaceOnPrimaryMonitor();
        Pill.Render(_model);
        _timer.Start();
    }

    public void SetPosition(IslandPosition position)
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
        if (Position == IslandPosition.Top)
        {
            y = work.Top;
            Pill.VerticalAlignment = VerticalAlignment.Top;
            Pill.Margin = new Thickness(0, EdgeGap, 0, 0);
        }
        else
        {
            // Let the window reach the bottom of the screen so the shadow can fall onto the taskbar.
            y = monitor.Bottom - height;
            Pill.VerticalAlignment = VerticalAlignment.Bottom;
            Pill.Margin = new Thickness(0, 0, 0, (monitor.Bottom - work.Bottom) / scale + EdgeGap);
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
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

    private void Tick()
    {
        _ticks++;
        if (_ticks % 10 == 0) _model.IsFullscreen = Native.IsFullscreenAppActive();
        _model.Tick(CursorOverPill());
        SetClickThrough(!_model.AcceptsMouse);
        Pill.Render(_model);
    }

    private bool CursorOverPill()
    {
        if (Pill.HitArea is not { } area || !Native.GetCursorPos(out var cursor)) return false;
        var rect = ScreenRect(area);
        rect.Inflate(HoverPadding * _scale, HoverPadding * _scale);
        return rect.Contains(cursor.X, cursor.Y);
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
        sb.AppendLine($"mode: {_model.Mode} line: {_model.CurrentLine?.Text}");
        sb.AppendLine($"ex-style: toolwindow={(style & Native.WS_EX_TOOLWINDOW) != 0} noactivate={(style & Native.WS_EX_NOACTIVATE) != 0}" +
                      $" layered={(style & Native.WS_EX_LAYERED) != 0} transparent(click-through)={(style & Native.WS_EX_TRANSPARENT) != 0}");
        sb.AppendLine($"accepts mouse: {_model.AcceptsMouse}");
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
