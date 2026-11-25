using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LyricsIsland.Windows;

/// <summary>Colors and fonts from the "Visual spec" table in docs/windows-port.md.</summary>
internal static class Theme
{
    public static readonly Brush White = Frozen(Color.FromArgb(255, 255, 255, 255));
    public static readonly Brush White60 = Frozen(Color.FromArgb(153, 255, 255, 255));
    public static readonly Brush White42 = Frozen(Color.FromArgb(107, 255, 255, 255));
    public static readonly Brush White30 = Frozen(Color.FromArgb(77, 255, 255, 255));
    public static readonly Brush White16 = Frozen(Color.FromArgb(41, 255, 255, 255));
    public static readonly Brush ChipBackground = Frozen(Color.FromArgb(36, 255, 255, 255));
    public static readonly Brush Green = Frozen(Color.FromRgb(0x1E, 0xD7, 0x60));

    /// <summary>Segoe UI Variable (Windows 11) is the Windows counterpart of SF Pro; Segoe UI on Windows 10.</summary>
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Horizontal stack with fixed spacing that skips collapsed children (SwiftUI's HStack).</summary>
public sealed class HStack : Panel
{
    public double Spacing { get; set; } = 10;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            count++;
        }
        return new Size(width + Spacing * Math.Max(0, count - 1), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, (final.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + Spacing;
        }
        return final;
    }
}

/// <summary>Base for small views that redraw on a timer while visible (equalizer, dots).</summary>
public abstract class AnimatedElement : FrameworkElement
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    protected AnimatedElement(double fps)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / fps) };
        _timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    /// <summary>Freezes the animation at this time (snapshots).</summary>
    public double? FixedTime { get; set; }

    protected double Time => FixedTime ?? Clock.Elapsed.TotalSeconds;

    protected virtual bool IsAnimating => true;

    protected void UpdateTimer()
    {
        var run = IsVisible && IsAnimating && FixedTime is null;
        if (run && !_timer.IsEnabled) _timer.Start();
        else if (!run && _timer.IsEnabled) _timer.Stop();
        InvalidateVisual();
    }
}

/// <summary>Four green bars bouncing at 20 fps, flat when paused (port of <c>Equalizer</c> in Components.swift).</summary>
public sealed class Equalizer : AnimatedElement
{
    private static readonly double[] Speeds = [7.1, 9.3, 6.2, 8.4];
    private static readonly double[] Phases = [0, 1.7, 3.1, 4.6];
    private const double BarWidth = 2.5, BarSpacing = 1.5, BarHeight = 14, FrameWidth = 16;
    private bool _playing = true;

    public Equalizer() : base(20)
    {
    }

    public bool Playing
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            UpdateTimer();
        }
    }

    protected override bool IsAnimating => _playing;

    protected override Size MeasureOverride(Size available) => new(FrameWidth, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var x = (FrameWidth - (4 * BarWidth + 3 * BarSpacing)) / 2;
        for (var i = 0; i < 4; i++)
        {
            var h = BarHeight * (_playing ? Level(Time, i) : 3 / BarHeight);
            dc.DrawRoundedRectangle(Theme.Green, null,
                new Rect(x + i * (BarWidth + BarSpacing), (BarHeight - h) / 2, BarWidth, h), BarWidth / 2, BarWidth / 2);
        }
    }

    /// <summary>0.28…1: two sines per bar so the motion doesn't look periodic.</summary>
    private static double Level(double t, int i)
    {
        var a = Math.Sin(t * Speeds[i] + Phases[i]);
        var b = Math.Sin(t * Speeds[(i + 2) % 4] * 0.53 + Phases[i] * 2);
        return 0.64 + 0.36 * (0.6 * a + 0.4 * b);
    }
}

/// <summary>Three pulsing white dots (interlude, loading).</summary>
public sealed class PulsingDots : AnimatedElement
{
    public PulsingDots() : base(20)
    {
    }

    public double DotSize { get; set; } = 5;
    public double DotSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size available) => new(DotSize * 3 + DotSpacing * 2, DotSize);

    protected override void OnRender(DrawingContext dc)
    {
        for (var i = 0; i < 3; i++)
        {
            dc.PushOpacity(0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(Time * 4 - i * 0.9)));
            var r = DotSize / 2;
            dc.DrawEllipse(Theme.White, null, new Point(r + i * (DotSize + DotSpacing), r), r, r);
            dc.Pop();
        }
    }
}

/// <summary>Sunset-over-the-city placeholder shown until real artwork loads (port of <c>PlaceholderArt</c>).</summary>
public sealed class PlaceholderArt : FrameworkElement
{
    private static readonly Brush Sky = CreateSky();
    private static readonly Brush Sun = Theme.Frozen(Color.FromArgb(242, 255, 224, 158));
    private static readonly Brush Skyline = Theme.Frozen(Color.FromRgb(23, 13, 46));

    public double CornerRadius { get; set; } = 6;

    protected override Size MeasureOverride(Size available) => new(Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius));
        dc.DrawRectangle(Sky, null, new Rect(0, 0, w, h));
        dc.DrawEllipse(Sun, null, new Point(w / 2, w * 0.48), w * 0.205, w * 0.205);
        dc.DrawRectangle(Skyline, null, new Rect(0, w * 0.7, w, w * 0.3));
        dc.Pop();
    }

    private static Brush CreateSky()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(250, 140, 84), 0),
                new GradientStop(Color.FromRgb(199, 41, 107), 0.5),
                new GradientStop(Color.FromRgb(46, 26, 97), 1),
            },
        };
        brush.Freeze();
        return brush;
    }
}
