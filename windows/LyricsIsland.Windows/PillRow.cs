using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>What the pill shows.</summary>
public enum RowKind
{
    Lyric,
    Interlude,
    Loading,
    NoLyrics,
    Mini,
    Paused,
    SongChange,
    Expanded,
}

/// <summary>
/// One state's content inside the pill (port of <c>content(_:)</c>, <c>CompactRow</c> and <c>SongChangeView</c>
/// in IslandView.swift, plus the hover panel). A new row is built on every mode change and cross-faded with
/// the old one; within a mode, <see cref="Update"/> changes it in place.
/// </summary>
public sealed class PillRow : Border
{
    private readonly LyricTicker? _ticker;
    private readonly Equalizer? _equalizer;
    private readonly ArtView? _art;
    private readonly TextBlock? _label;
    private readonly TextBlock? _title;
    private readonly TextBlock? _artist;
    private readonly ExpandedPanel? _panel;
    private double _scale = double.NaN;

    public PillRow(RowKind kind, IslandModel model)
    {
        Kind = kind;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        if (kind == RowKind.Expanded)
        {
            Child = _panel = new ExpandedPanel(model);
            return;
        }

        var row = new HStack { Spacing = kind == RowKind.SongChange ? 12 : 10 };
        Child = row;
        if (kind == RowKind.SongChange)
        {
            // HStack(spacing: 12) { art 36; title/artist ≤ 260; clear 28; equalizer }.padding(.leading, 8).padding(.trailing, 16), height 52
            Height = 52;
            Padding = new Thickness(8, 0, 16, 0);
            row.Children.Add(_art = new ArtView(36, 9));
            _title = Text(14, FontWeights.SemiBold, Theme.White);
            _artist = Text(12, FontWeights.Normal, Theme.White60);
            _artist.Margin = new Thickness(0, 1, 0, 0);
            // Segoe UI's line height is ~1.33 em, SF Pro's ~1.19 em: use SF's so the two lines sit as close as on the Mac.
            foreach (var text in new[] { _title, _artist })
            {
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                text.LineHeight = Math.Round(text.FontSize * 1.19, 1);
            }
            // Nudged up 1.25 DIP: Segoe's ascent sits lower than SF's (measured against the Mac's songChange.png).
            row.Children.Add(new StackPanel { MaxWidth = 260, RenderTransform = new TranslateTransform(0, -1.25), Children = { _title, _artist } });
            row.Children.Add(new FrameworkElement { Width = 28, Height = 1 });
            row.Children.Add(_equalizer = new Equalizer());
            Update(model, animate: false);
            return;
        }

        Height = 34;
        var trailing = kind switch
        {
            RowKind.Loading or RowKind.Paused => 14,
            RowKind.NoLyrics => 6,
            _ => 12,
        };
        Padding = new Thickness(6, 0, trailing, 0);
        row.Children.Add(_art = new ArtView(22, 6) { Opacity = kind == RowKind.Paused ? 0.55 : 1 });

        switch (kind)
        {
            case RowKind.Lyric:
                row.Children.Add(_ticker = new LyricTicker());
                row.Children.Add(_equalizer = new Equalizer());
                break;
            case RowKind.Interlude:
                row.Children.Add(new PulsingDots { DotSize = 7, DotSpacing = 6 });
                row.Children.Add(_equalizer = new Equalizer());
                break;
            case RowKind.Loading:
                row.Children.Add(_label = Text(13, FontWeights.SemiBold, Theme.White60, "Finding lyrics…"));
                row.Children.Add(new PulsingDots());
                break;
            case RowKind.NoLyrics:
                row.Children.Add(_title = Text(13, FontWeights.SemiBold, Theme.White));
                _title.MaxWidth = 300;
                var chip = new Chip();
                chip.Set(("No lyrics", null));
                row.Children.Add(chip);
                break;
            case RowKind.Mini:
                row.Children.Add(_equalizer = new Equalizer());
                break;
            case RowKind.Paused:
                row.Children.Add(PauseGlyph());
                row.Children.Add(_label = Text(13, FontWeights.SemiBold, Theme.White60, "Paused"));
                break;
        }
        Update(model, animate: false);
    }

    public RowKind Kind { get; }

    /// <summary>Corner radius of the pill around this row: 32 expanded, 26 song change, 17 compact.</summary>
    public double Radius => Kind switch
    {
        RowKind.Expanded => 32,
        RowKind.SongChange => 26,
        _ => 17,
    };

    public bool IsAnimating => (_ticker?.IsAnimating ?? false) || (_panel?.IsAnimating ?? false);

    public static RowKind? KindFor(IslandModel model) => model.Mode switch
    {
        IslandMode.Hidden => null,
        IslandMode.Lyric => RowKind.Lyric,
        IslandMode.Interlude => RowKind.Interlude,
        IslandMode.Loading => RowKind.Loading,
        IslandMode.NoLyrics => RowKind.NoLyrics,
        IslandMode.Mini => RowKind.Mini,
        IslandMode.Paused => RowKind.Paused,
        IslandMode.SongChange => RowKind.SongChange,
        _ => RowKind.Expanded,
    };

    /// <summary>Brings the row up to date. Returns true when its size may have changed.</summary>
    public bool Update(IslandModel model, bool animate)
    {
        if (_panel is not null)
        {
            _panel.Update(animate);
            return false;
        }
        if (_art is not null) _art.TrackId = model.Track?.Id;
        if (_equalizer is not null) _equalizer.Playing = model.Playback == PlaybackState.Playing;
        var scale = model.Settings.TextSize.Scale();
        var scaleChanged = scale != _scale;
        _scale = scale;

        if (_ticker is not null && model.CurrentLine is { } line)
        {
            _ticker.SetLine(line.Id, line.Text, scale, animate);
        }
        if (Kind == RowKind.SongChange)
        {
            _title!.Text = model.Track?.Title ?? "";
            _artist!.Text = model.Track?.Artist ?? "";
        }
        else if (Kind == RowKind.NoLyrics)
        {
            // Title · Artist, artist dimmed
            var title = model.Track?.Title ?? "";
            var artist = " · " + (model.Track?.Artist ?? "");
            if (_title!.Inlines.Count != 2 || ((Run)_title.Inlines.FirstInline).Text != title || ((Run)_title.Inlines.LastInline).Text != artist)
            {
                _title.Inlines.Clear();
                _title.Inlines.Add(new Run(title) { Foreground = Theme.White });
                _title.Inlines.Add(new Run(artist) { Foreground = Theme.White60 });
            }
        }
        if (scaleChanged)
        {
            if (_label is not null) _label.FontSize = 13 * scale;
            if (Kind == RowKind.NoLyrics) _title!.FontSize = 13 * scale;
        }
        return scaleChanged;
    }

    public bool Step(double dt)
    {
        var moving = _ticker?.Step(dt) ?? false;
        if (_panel?.Step(dt) == true) moving = true;
        return moving;
    }

    public void Finish()
    {
        _ticker?.Finish();
        _panel?.Finish();
    }

    private static TextBlock Text(double size, FontWeight weight, Brush color, string? text = null) =>
        ExpandedPanel.Text(size, weight, color, text ?? "");

    /// <summary>SF Symbol "pause.fill" at 10 pt bold: two solid bars.</summary>
    private static FrameworkElement PauseGlyph()
    {
        var canvas = new Canvas { Width = 8, Height = 9 };
        canvas.Children.Add(Bar(0));
        canvas.Children.Add(Bar(5));
        return canvas;

        static Rectangle Bar(double left)
        {
            var bar = new Rectangle { Width = 3, Height = 9, RadiusX = 0.8, RadiusY = 0.8, Fill = Theme.White60 };
            Canvas.SetLeft(bar, left);
            return bar;
        }
    }
}
