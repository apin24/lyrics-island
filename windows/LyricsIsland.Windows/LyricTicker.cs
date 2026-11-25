using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LyricsIsland.Windows;

/// <summary>
/// The compact pill's current line (port of <c>CompactLyric</c> in IslandView.swift): on a line change
/// the old line slides up and fades out, the new one slides in from below, clipped to an 18 pt slot,
/// on the line spring (response 0.35, damping 0.86). The width follows the new line on the same spring.
/// <para>
/// Each line is laid out and drawn once into its own <see cref="DrawingVisual"/>; a frame only moves its
/// transform and changes its brush opacity, which the compositor handles without re-formatting text.
/// (Re-drawing the text every frame cost about 1 ms per frame.)
/// </para>
/// Driven by <see cref="PillView"/>, which calls <see cref="Step"/> every frame.
/// </summary>
public sealed class LyricTicker : FrameworkElement
{
    // Settled within a quarter pixel: width directly, the slide as 1.4% of the 18 pt slot.
    private readonly Spring _width = new(Spring.Line.Response, Spring.Line.Damping, epsilon: 0.25);
    private readonly Spring _slide = new(Spring.Line.Response, Spring.Line.Damping, epsilon: 0.014);
    private readonly VisualCollection _visuals;
    private Line? _current;
    private Line? _previous;
    private string? _text;
    private int? _lineId;
    private double _scale = 1;

    public LyricTicker()
    {
        _visuals = new VisualCollection(this);
        _slide.Snap(1);
        ClipToBounds = true;
    }

    private double SlotHeight => 18 * _scale;
    private double FontSize => 13 * _scale;
    private double TextLimit => 380 * _scale;

    public bool IsAnimating => !_width.IsSettled || !_slide.IsSettled;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>Shows <paramref name="text"/>; slides when <paramref name="animate"/> and a line is already showing.</summary>
    public void SetLine(int? lineId, string text, double scale, bool animate)
    {
        if (lineId == _lineId && text == _text && scale == _scale) return;
        var scaleChanged = scale != _scale;
        _scale = scale;
        var lineChanged = lineId != _lineId || text != _text;
        _lineId = lineId;
        _text = text;

        var slide = animate && lineChanged && _current is not null;
        if (_previous is not null) _visuals.Remove(_previous);
        _previous = null;
        if (slide) _previous = _current;
        else if (_current is not null) _visuals.Remove(_current);

        _current = new Line(text, Typeface, FontSize, TextLimit, SlotHeight, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _visuals.Add(_current);
        if (slide) _slide.Snap(0);
        _slide.Target = 1;

        if (animate) _width.Target = _current.Width;
        else _width.Snap(_current.Width);
        if (scaleChanged && !animate) _slide.Snap(1);

        Apply();
        InvalidateMeasure();
    }

    /// <summary>Advances the slide and width springs; returns true while still moving.</summary>
    public bool Step(double dt)
    {
        var widthMoving = _width.Step(dt);
        var slideMoving = _slide.Step(dt);
        if (widthMoving) InvalidateMeasure();
        if (!slideMoving && _previous is not null)
        {
            _visuals.Remove(_previous);
            _previous = null;
        }
        Apply();
        return widthMoving || slideMoving;
    }

    public void Finish()
    {
        _width.Snap(_width.Target);
        _slide.Snap(1);
        if (_previous is not null) _visuals.Remove(_previous);
        _previous = null;
        Apply();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available) => new(Math.Max(0, _width.Value), SlotHeight);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        // Lay the line out again for the new pixel density.
        if (_text is null) return;
        var text = _text;
        _text = null;
        SetLine(_lineId, text, _scale, animate: false);
    }

    private static readonly Typeface Typeface = new(Theme.TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private void Apply()
    {
        var p = _slide.Value;
        var h = SlotHeight;
        if (_previous is not null) _previous.Place(-p * h, 1 - p);
        _current?.Place((1 - p) * h, _previous is null ? 1 : p);
    }

    /// <summary>One line of text, formatted and drawn once.</summary>
    private sealed class Line : DrawingVisual
    {
        private readonly SolidColorBrush _brush = new(Colors.White);
        private readonly TranslateTransform _shift = new();

        public Line(string text, Typeface typeface, double fontSize, double maxWidth, double slotHeight, double pixelsPerDip)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, _brush, pixelsPerDip)
            {
                MaxTextWidth = maxWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            Width = formatted.Width;
            using (var dc = RenderOpen())
            {
                dc.DrawText(formatted, new Point(0, (slotHeight - formatted.Height) / 2));
            }
            Transform = _shift;
        }

        public double Width { get; }

        public void Place(double y, double opacity)
        {
            _shift.Y = y;
            _brush.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }
}
