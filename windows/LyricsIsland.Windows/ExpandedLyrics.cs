using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// The lyrics in the hover panel (port of <c>ExpandedLyrics</c>): the previous line, the active line in
/// Bold 22 with word-by-word karaoke, and the next two lines dimmed. On a line change, lines that stay
/// move to their new place, new ones come in from 14 pt below and old ones leave 14 pt upwards, fading,
/// with SwiftUI's <c>.easeOut(duration: 0.3)</c>. Click a line to jump to it.
/// </summary>
public sealed class ExpandedLyrics : Panel
{
    private const double Duration = 0.3;
    private const int Placeholder = -1; // the pulsing dots shown before the first line

    private readonly IslandModel _model;
    private readonly Dictionary<int, Line> _lines = new();
    private readonly List<Line> _leaving = [];
    private FrameworkElement? _message;
    private LyricsState? _messageState;
    private Lyrics? _lyrics;
    private int? _index;
    private double _scale = double.NaN;
    private double _elapsed = double.NaN;

    public ExpandedLyrics(IslandModel model)
    {
        _model = model;
        ClipToBounds = false;
    }

    public bool IsAnimating => !double.IsNaN(_elapsed);

    /// <summary>The visible lines as click targets, named "line{index}" (for --diagnose).</summary>
    public IEnumerable<(string Name, FrameworkElement Element)> LineTargets() =>
        _lines.Where(l => l.Key >= 0).OrderBy(l => l.Key).Select(l => ($"line{l.Key}", (FrameworkElement)l.Value));

    private double Spacing => 8 * _scale;
    private double MinimumHeight => 118 * _scale;

    public void Update(bool animate)
    {
        var status = _model.LyricsStatus;
        var scale = _model.Settings.TextSize.Scale();
        if (status is not { State: LyricsState.Found, Lyrics: { } lyrics })
        {
            ShowMessage(status.State, scale);
            return;
        }

        var index = _model.LineIndex;
        if (_message is not null || !ReferenceEquals(lyrics, _lyrics) || scale != _scale)
        {
            // New lyrics or text size: lay everything out again, no motion.
            Finish();
            ClearAll();
            _lyrics = lyrics;
            _scale = scale;
            _index = index;
            foreach (var key in VisibleKeys(lyrics, index)) Add(key, lyrics, index);
            InvalidateMeasure();
        }
        else if (index != _index)
        {
            Transition(lyrics, index, animate);
        }

        // Karaoke on the active line, ~10 times a second.
        if (index is { } i && _lines.TryGetValue(i, out var current)) current.UpdateKaraoke(_model.LineProgress(i));
        foreach (var line in _lines.Values) line.Cursor = _model.CanSeek ? Cursors.Hand : null;
    }

    public bool Step(double dt)
    {
        if (double.IsNaN(_elapsed)) return false;
        _elapsed += dt;
        var t = Math.Min(1, _elapsed / Duration);
        Apply(EaseOut(t));
        if (t < 1) return true;
        Finish();
        return false;
    }

    public void Finish()
    {
        if (double.IsNaN(_elapsed)) return;
        Apply(1);
        foreach (var line in _leaving) Children.Remove(line);
        _leaving.Clear();
        _elapsed = double.NaN;
    }

    // ── Line changes ─────────────────────────────────────────────────────

    private void Transition(Lyrics lyrics, int? index, bool animate)
    {
        Finish();
        var oldY = Positions();
        var keys = VisibleKeys(lyrics, index).ToList();

        foreach (var key in _lines.Keys.Except(keys).ToList())
        {
            var line = _lines[key];
            _lines.Remove(key);
            line.StartY = oldY.GetValueOrDefault(key);
            line.Kind = Motion.Leave;
            _leaving.Add(line);
        }
        _index = index;
        foreach (var key in keys)
        {
            if (_lines.TryGetValue(key, out var line))
            {
                line.Restyle(lyrics, index, _scale);
                line.Kind = Motion.Move;
            }
            else
            {
                Add(key, lyrics, index).Kind = Motion.Enter;
            }
        }

        var newY = Positions();
        foreach (var (key, line) in _lines)
        {
            if (line.Kind == Motion.Move) line.Delta = oldY.GetValueOrDefault(key) - newY[key];
        }
        InvalidateMeasure();

        if (!animate)
        {
            _elapsed = 0;
            Finish();
            return;
        }
        _elapsed = 0;
        Apply(0);
    }

    private void Apply(double p)
    {
        foreach (var line in _lines.Values)
        {
            switch (line.Kind)
            {
                case Motion.Move:
                    line.Shift.Y = line.Delta * (1 - p);
                    line.Opacity = 1;
                    break;
                case Motion.Enter:
                    line.Shift.Y = 14 * (1 - p);
                    line.Opacity = p;
                    break;
                default:
                    line.Shift.Y = 0;
                    line.Opacity = 1;
                    break;
            }
        }
        foreach (var line in _leaving)
        {
            line.Shift.Y = -14 * p;
            line.Opacity = 1 - p;
        }
        if (p >= 1)
        {
            foreach (var line in _lines.Values) line.Kind = Motion.None;
        }
    }

    /// <summary>SwiftUI's <c>.easeOut</c>: cubic-bezier(0, 0, 0.58, 1).</summary>
    private static double EaseOut(double t)
    {
        // Solve x(s) = t for the curve parameter s (Newton), then return y(s).
        const double x1 = 0, x2 = 0.58, y1 = 0, y2 = 1;
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Bezier(s, x1, x2) - t;
            var dx = BezierSlope(s, x1, x2);
            if (Math.Abs(x) < 1e-6 || Math.Abs(dx) < 1e-6) break;
            s = Math.Clamp(s - x / dx, 0, 1);
        }
        return Bezier(s, y1, y2);

        static double Bezier(double s, double a, double b) => 3 * a * s * (1 - s) * (1 - s) + 3 * b * s * s * (1 - s) + s * s * s;
        static double BezierSlope(double s, double a, double b) => 3 * a * (1 - s) * (1 - s) + 6 * (b - a) * s * (1 - s) + 3 * (1 - b) * s * s;
    }

    // ── Layout ───────────────────────────────────────────────────────────

    /// <summary>Lines from the previous one to two after the active one (or the first three, with dots, before the first line).</summary>
    private static IEnumerable<int> VisibleKeys(Lyrics lyrics, int? index)
    {
        if (index is null) yield return Placeholder;
        var lo = Math.Max(0, (index ?? 0) - 1);
        var hi = Math.Min(lyrics.Lines.Count - 1, index is { } i ? i + 2 : 2);
        for (var j = lo; j <= hi; j++) yield return j;
    }

    /// <summary>Top of every visible line in the stack (from its desired height).</summary>
    private Dictionary<int, double> Positions()
    {
        var width = ActualWidth > 0 ? ActualWidth : ExpandedPanel.PanelWidth - 40;
        var y = 0.0;
        var result = new Dictionary<int, double>();
        foreach (var key in _lines.Keys.Order())
        {
            var line = _lines[key];
            line.Measure(new Size(width, double.PositiveInfinity));
            result[key] = y;
            y += line.DesiredSize.Height + Spacing;
        }
        return result;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? ExpandedPanel.PanelWidth - 40 : available.Width;
        double height;
        if (_message is not null)
        {
            _message.Measure(new Size(width, double.PositiveInfinity));
            height = _message.DesiredSize.Height;
        }
        else
        {
            height = 0;
            var count = 0;
            foreach (var line in _lines.Values)
            {
                line.Measure(new Size(width, double.PositiveInfinity));
                height += line.DesiredSize.Height;
                count++;
            }
            foreach (var line in _leaving) line.Measure(new Size(width, double.PositiveInfinity));
            height += Spacing * Math.Max(0, count - 1);
        }
        // Never shrink while the panel is open: when a two-line karaoke line is followed by a one-line one,
        // the progress bar and the buttons would move up from under a resting cursor, the cursor would end
        // up outside the pill, and the panel would close by itself 1 s later. A new panel (next hover) starts over.
        _tallest = Math.Max(_tallest, Math.Max(MinimumHeight, height));
        return new Size(width, _tallest);
    }

    private double _tallest;

    protected override Size ArrangeOverride(Size final)
    {
        if (_message is not null)
        {
            _message.Arrange(new Rect(0, 0, final.Width, _message.DesiredSize.Height));
            return final;
        }
        var y = 0.0;
        foreach (var key in _lines.Keys.Order())
        {
            var line = _lines[key];
            line.Arrange(new Rect(0, y, final.Width, line.DesiredSize.Height));
            y += line.DesiredSize.Height + Spacing;
        }
        foreach (var line in _leaving) line.Arrange(new Rect(0, line.StartY, final.Width, line.DesiredSize.Height));
        return final;
    }

    // ── Content ──────────────────────────────────────────────────────────

    private Line Add(int key, Lyrics lyrics, int? index)
    {
        var line = new Line(key);
        line.Restyle(lyrics, index, _scale);
        if (key != Placeholder)
        {
            line.MouseLeftButtonUp += (_, e) =>
            {
                if (!_model.CanSeek) return;
                _model.SeekToLine(key);
                e.Handled = true;
            };
        }
        _lines[key] = line;
        Children.Add(line);
        return line;
    }

    private void ClearAll()
    {
        _lines.Clear();
        _leaving.Clear();
        Children.Clear();
        _message = null;
        _messageState = null;
    }

    private void ShowMessage(LyricsState state, double scale)
    {
        if (_messageState == state && scale == _scale) return;
        Finish();
        ClearAll();
        _lyrics = null;
        _scale = scale;
        _messageState = state;

        var label = ExpandedPanel.Text(17 * scale, FontWeights.SemiBold, Theme.White60);
        if (state == LyricsState.NotFound)
        {
            // "No lyrics found for this song." + a chip that opens the manual search
            label.Text = "No lyrics found for this song.";
            var chip = new Chip { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand };
            chip.Set(("Search lyrics manually…", null));
            ExpandedPanel.Pressable(chip, ExpandedPanel.RequestManualSearch);
            _message = new StackPanel { Children = { label, chip } };
        }
        else
        {
            label.Text = "Finding lyrics…";
            label.VerticalAlignment = VerticalAlignment.Center;
            var dots = new PulsingDots { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _message = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, dots } };
        }
        Children.Add(_message);
        InvalidateMeasure();
    }

    private enum Motion
    {
        None,
        Move,
        Enter,
        Leave,
    }

    /// <summary>One line (or the placeholder dots) in the stack.</summary>
    private sealed class Line : Border
    {
        private readonly int _key;
        private TextBlock? _text;
        private string[] _words = [];
        private int _lit = -1;

        public Line(int key)
        {
            _key = key;
            Background = Brushes.Transparent; // the whole row takes clicks
            RenderTransform = Shift;
        }

        public TranslateTransform Shift { get; } = new();
        public Motion Kind { get; set; }
        public double Delta { get; set; }
        public double StartY { get; set; }
        private bool IsKaraoke => _words.Length > 0;

        public void Restyle(Lyrics lyrics, int? index, double scale)
        {
            var current = index ?? -1;
            if (_key == Placeholder || (_key == current && lyrics.Lines[_key].IsBlank))
            {
                // PulsingDots(size: 7, spacing: 6).frame(height: 28)
                _text = null;
                _words = [];
                Child = new PulsingDots { DotSize = 7, DotSpacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                Height = 28 * scale;
                Opacity = 1;
                return;
            }

            Height = double.NaN;
            var line = lyrics.Lines[_key];
            if (_key == current)
            {
                // Karaoke, Bold 22, up to two lines
                var size = 22 * scale;
                _text = ExpandedPanel.Text(size, FontWeights.Bold, Theme.White);
                _text.FontFamily = Theme.DisplayFont;
                _text.TextWrapping = TextWrapping.Wrap;
                _text.TextTrimming = TextTrimming.WordEllipsis;
                _text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                _text.LineHeight = Math.Round(size * 1.19, 1);
                _text.MaxHeight = _text.LineHeight * 2 + 1;
                _words = Karaoke.Words(line.Text);
                _lit = -1;
                UpdateKaraoke(0);
            }
            else
            {
                // Semibold 17, one line, white 30% / 16%
                var size = 17 * scale;
                _text = ExpandedPanel.Text(size, FontWeights.SemiBold, Theme.White, line.IsBlank ? "♪" : line.Text);
                _text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                _text.LineHeight = Math.Round(size * 1.19, 1);
                _words = [];
                var dim = _key < current || _key - current <= 1 ? 0.3 : 0.16;
                _text.Foreground = dim == 0.3 ? Theme.White30 : Theme.White16;
            }
            Child = _text;
        }

        public void UpdateKaraoke(double progress)
        {
            if (!IsKaraoke || _text is null) return;
            var lit = Karaoke.LitWords(_words.Length, progress);
            if (lit == _lit) return;
            _lit = lit;
            _text.Inlines.Clear();
            for (var i = 0; i < _words.Length; i++)
            {
                _text.Inlines.Add(new Run((i == 0 ? "" : " ") + _words[i]) { Foreground = i < lit ? Theme.White : Theme.White42 });
            }
        }
    }
}
