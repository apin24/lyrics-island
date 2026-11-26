using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// The hover panel (port of ExpandedView.swift): header, karaoke lyrics, progress and transport controls.
/// 560 wide, padding 18/20/16/20, 16 between the sections.
/// </summary>
public sealed class ExpandedPanel : Border
{
    public const double PanelWidth = 560;

    /// <summary>"Search lyrics manually…" was clicked.</summary>
    public static event Action? ManualSearchRequested;

    private readonly IslandModel _model;
    private readonly ArtView _art = new(44, 10);
    private readonly TextBlock _title = Text(14, FontWeights.SemiBold, Theme.White);
    private readonly TextBlock _artist = Text(12, FontWeights.Normal, Theme.White60);
    private readonly Equalizer _equalizer = new();
    private readonly ExpandedLyrics _lyrics;
    private readonly ProgressRow _progress;
    private readonly Chip _source = new();
    private readonly Path _playPause = new() { Fill = Theme.White };
    private readonly TextBlock _offset = Text(11, FontWeights.Medium, Theme.White60);
    private bool? _playing;

    public ExpandedPanel(IslandModel model)
    {
        _model = model;
        Width = PanelWidth;
        Padding = new Thickness(20, 18, 20, 16);

        _lyrics = new ExpandedLyrics(model);
        _progress = new ProgressRow(model);
        Typography.SetNumeralAlignment(_offset, FontNumeralAlignment.Tabular);

        Child = new StackPanel
        {
            Children =
            {
                Header(),
                Spaced(_lyrics),
                Spaced(_progress),
                Spaced(Controls()),
            },
        };
        Update(animate: false);
    }

    public bool IsAnimating => _lyrics.IsAnimating;

    private readonly List<(string Name, FrameworkElement Element)> _targets = [];

    /// <summary>Screen rects (px) of the clickable parts, for <c>--diagnose</c> and scripted click checks.</summary>
    public IEnumerable<string> DescribeTargets()
    {
        foreach (var (name, element) in _targets.Concat(_lyrics.LineTargets()))
        {
            if (!element.IsVisible) continue;
            var a = element.PointToScreen(new Point(0, 0));
            var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            yield return $"{name}={a.X:0},{a.Y:0},{b.X - a.X:0},{b.Y - a.Y:0}";
        }
    }

    public bool Step(double dt) => _lyrics.Step(dt);

    public void Finish() => _lyrics.Finish();

    /// <summary>Brings everything up to date; cheap when nothing changed (called on every tick).</summary>
    public void Update(bool animate)
    {
        var m = _model;
        _art.TrackId = m.Track?.Id;
        var open = $"Open {m.PlayerName ?? "Spotify"}";
        if (!Equals(_art.ToolTip, open)) _art.ToolTip = open;
        SetText(_title, m.Track?.Title ?? "");
        SetText(_artist, m.Track?.Artist ?? "");
        var playing = m.Playback == PlaybackState.Playing;
        _equalizer.Playing = playing;
        if (_playing != playing)
        {
            _playing = playing;
            _playPause.Data = playing ? Glyphs.Pause(22) : Glyphs.Play(22);
        }
        _lyrics.Update(animate);
        _progress.Update();
        _source.Set(m.LyricsStatus switch
        {
            { State: LyricsState.Found, Lyrics: { } l } => ($"Synced · {l.Source}", Theme.Green),
            { State: LyricsState.NotFound } => ("No lyrics", Theme.White35),
            _ => ("Finding lyrics…", Theme.White35),
        });
        SetText(_offset, m.OffsetLabel);
    }

    // ── Sections ─────────────────────────────────────────────────────────

    private FrameworkElement Header()
    {
        // HStack(spacing: 12) { art 44; VStack(spacing: 2) { title; artist }; Spacer(minLength: 8); equalizer }
        _art.Cursor = Cursors.Hand;
        _art.MouseLeftButtonUp += (_, e) =>
        {
            _model.OpenPlayer();
            e.Handled = true;
        };
        foreach (var text in new[] { _title, _artist })
        {
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            text.LineHeight = Math.Round(text.FontSize * 1.19, 1);
        }
        _artist.Margin = new Thickness(0, 2, 0, 0);
        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            RenderTransform = new TranslateTransform(0, -1),
            Children = { _title, _artist },
        };
        var grid = new Grid
        {
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
        };
        Grid.SetColumn(texts, 1);
        _equalizer.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_equalizer, 2);
        grid.Children.Add(_art);
        grid.Children.Add(texts);
        grid.Children.Add(_equalizer);
        return grid;
    }

    private FrameworkElement Controls()
    {
        // HStack { source chip (left) | HStack(spacing: 30) { ⏮ ⏯ ⏭ } | offset − ±0.0s + (right) }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var previous = IconButton(Glyphs.Skip(17, back: true), 17, () => _model.Previous());
        var playPause = IconButton(null, 22, () => _model.TogglePlay(), _playPause);
        var next = IconButton(Glyphs.Skip(17, back: false), 17, () => _model.Next());
        buttons.Children.Add(previous);
        buttons.Children.Add(Gap(30));
        buttons.Children.Add(playPause);
        buttons.Children.Add(Gap(30));
        buttons.Children.Add(next);
        _targets.AddRange([("previous", previous), ("playpause", playPause), ("next", next), ("progress", _progress.BarElement), ("art", _art)]);

        var offset = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        offset.ToolTip = "Adjust lyric timing: − if the lyrics run early, + if they lag behind";
        offset.Children.Add(SmallButton(plus: false, () => _model.AdjustOffset(-0.5)));
        _offset.Margin = new Thickness(4, 0, 4, 0);
        _offset.VerticalAlignment = VerticalAlignment.Center;
        offset.Children.Add(_offset);
        offset.Children.Add(SmallButton(plus: true, () => _model.AdjustOffset(0.5)));

        _source.HorizontalAlignment = HorizontalAlignment.Left;
        _source.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid
        {
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
        };
        Grid.SetColumn(buttons, 1);
        Grid.SetColumn(offset, 2);
        grid.Children.Add(_source);
        grid.Children.Add(buttons);
        grid.Children.Add(offset);
        return grid;
    }

    // ── Pieces ───────────────────────────────────────────────────────────

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 16, 0, 0);
        return element;
    }

    private static FrameworkElement Gap(double width) => new() { Width = width };

    /// <summary>A borderless 30×28 button around a glyph (port of <c>IconButton</c>).</summary>
    private static FrameworkElement IconButton(Geometry? glyph, double size, Action action, Path? path = null)
    {
        path ??= new Path { Data = glyph, Fill = Theme.White };
        path.Width = glyph is null ? size : glyph.Bounds.Right;
        path.Height = size;
        path.HorizontalAlignment = HorizontalAlignment.Center;
        path.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border { Width = 30, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = path };
        Pressable(button, action);
        return button;
    }

    /// <summary>The 18 pt round − / + buttons of the offset control.</summary>
    private static FrameworkElement SmallButton(bool plus, Action action)
    {
        var sign = new Path
        {
            Data = Glyphs.Sign(7, plus),
            Stroke = Theme.White70,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 7,
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Theme.ChipBackground, Cursor = Cursors.Hand, Child = sign };
        Pressable(button, action);
        return button;
    }

    /// <summary>Click = press and release on the element; dims a little while pressed.</summary>
    internal static void Pressable(UIElement element, Action action)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            element.CaptureMouse();
            element.Opacity = 0.6;
            e.Handled = true;
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            var inside = element.IsMouseCaptured && element.InputHitTest(e.GetPosition(element)) is not null;
            element.ReleaseMouseCapture();
            element.Opacity = 1;
            e.Handled = true;
            if (inside) action();
        };
        element.LostMouseCapture += (_, _) => element.Opacity = 1;
    }

    internal static void RequestManualSearch() => ManualSearchRequested?.Invoke();

    internal static TextBlock Text(double size, FontWeight weight, Brush color, string text = "") => new()
    {
        Text = text,
        FontFamily = Theme.TextFont,
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }
}

/// <summary>Capsule with an optional colored dot and a label (port of <c>Chip</c> / <c>SourceChip</c>).</summary>
public sealed class Chip : Border
{
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = ExpandedPanel.Text(11, FontWeights.Medium, Theme.White60);

    public Chip()
    {
        Background = Theme.ChipBackground;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 4, 8, 4);
        _label.VerticalAlignment = VerticalAlignment.Center;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _label } };
    }

    public void Set((string Text, Brush? Dot) value)
    {
        if (_label.Text != value.Text) _label.Text = value.Text;
        _dot.Fill = value.Dot;
        _dot.Visibility = value.Dot is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Elapsed time, a 4 pt bar (click to seek) and the time left (port of <c>ProgressRow</c>).</summary>
public sealed class ProgressRow : Grid
{
    private readonly IslandModel _model;
    private readonly TextBlock _elapsed = ExpandedPanel.Text(11, FontWeights.Medium, Theme.White60);
    private readonly TextBlock _remaining = ExpandedPanel.Text(11, FontWeights.Medium, Theme.White60);
    private readonly Bar _bar = new();

    public ProgressRow(IslandModel model)
    {
        _model = model;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var t in new[] { _elapsed, _remaining })
        {
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            t.VerticalAlignment = VerticalAlignment.Center;
        }
        _bar.Margin = new Thickness(10, 0, 10, 0);
        _bar.Height = 14;
        _bar.MouseLeftButtonUp += (_, e) =>
        {
            if (!_model.CanSeek) return;
            _model.Seek(e.GetPosition(_bar).X / Math.Max(1, _bar.ActualWidth));
            e.Handled = true;
        };
        SetColumn(_bar, 1);
        SetColumn(_remaining, 2);
        Children.Add(_elapsed);
        Children.Add(_bar);
        Children.Add(_remaining);
    }

    public FrameworkElement BarElement => _bar;

    public void Update()
    {
        var current = _model.CurrentTime;
        var duration = _model.Track?.Duration ?? 0;
        var elapsed = Format(current);
        var remaining = "-" + Format(Math.Max(0, duration - current));
        if (_elapsed.Text != elapsed) _elapsed.Text = elapsed;
        if (_remaining.Text != remaining) _remaining.Text = remaining;
        _bar.Cursor = _model.CanSeek ? Cursors.Hand : null;
        _bar.Fraction = duration > 0 ? Math.Clamp(current / duration, 0, 1) : 0;
    }

    /// <summary>"m:ss", rounded down.</summary>
    public static string Format(double seconds)
    {
        var t = (int)Math.Floor(Math.Max(0, seconds));
        return $"{t / 60}:{t % 60:00}";
    }

    private sealed class Bar : FrameworkElement
    {
        private double _fraction = -1;

        public Bar()
        {
            // Transparent background so the whole 14 pt row takes clicks, not just the 4 pt bar.
            Focusable = false;
        }

        public double Fraction
        {
            set
            {
                // Redraw only when the fill moves by at least a quarter pixel.
                if (Math.Abs(value - _fraction) * ActualWidth < 0.25 && _fraction >= 0) return;
                _fraction = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var y = (ActualHeight - 4) / 2;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
            dc.DrawRoundedRectangle(Theme.ChipBackground, null, new Rect(0, y, w, 4), 2, 2);
            dc.DrawRoundedRectangle(Theme.Green, null, new Rect(0, y, Math.Max(4, w * Math.Max(0, _fraction)), 4), 2, 2);
        }
    }
}
