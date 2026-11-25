using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// The compact pill (port of the compact rows in <c>IslandView.swift</c>). Milestone 3: lyric, interlude
/// and the other compact rows without animations. The song-change layout and motion come in milestone 4,
/// the expanded panel in milestone 5.
/// </summary>
public partial class PillView : UserControl
{
    private IslandMode? _mode;
    private string? _text;
    private double _scale = double.NaN;

    public PillView()
    {
        InitializeComponent();
        foreach (var text in new[] { MainText, ChipText })
        {
            text.FontFamily = Theme.TextFont;
        }
        ChipText.Foreground = Theme.White60;
        Chip.Background = Theme.ChipBackground;
    }

    /// <summary>The visible pill, for hover hit-testing (null while hidden).</summary>
    public FrameworkElement? HitArea => Root.Visibility == Visibility.Visible ? Root : null;

    /// <summary>Element sizes in DIPs, for <c>--diagnose</c>.</summary>
    public string DescribeLayout() =>
        $"root {Root.ActualWidth:0.#}x{Root.ActualHeight:0.#}, body {Body.ActualWidth:0.#}, text {MainText.ActualWidth:0.#} " +
        $"(slot {TextSlot.ActualWidth:0.#}), row {Row.ActualWidth:0.#}, control {ActualWidth:0.#}";

    /// <summary>Freezes the equalizer and dots (snapshots).</summary>
    public void FreezeAnimations(double time)
    {
        Eq.FixedTime = time;
        Dots.FixedTime = time;
    }

    /// <summary>Brings the pill up to date with the model. Cheap when nothing changed; called every tick.</summary>
    public void Render(IslandModel model)
    {
        var mode = model.Mode;
        Eq.Playing = model.Playback == PlaybackState.Playing;
        if (mode == IslandMode.Hidden)
        {
            Root.Visibility = Visibility.Collapsed;
            _mode = mode;
            return;
        }
        Root.Visibility = Visibility.Visible;

        // Until the expanded panel exists (milestone 5) it shows as the compact lyric row.
        var row = mode == IslandMode.Expanded ? (model.CurrentLine is null ? IslandMode.Interlude : IslandMode.Lyric) : mode;
        var scale = model.Settings.TextSize.Scale();
        var text = row switch
        {
            IslandMode.Lyric => model.CurrentLine?.Text ?? "",
            IslandMode.Loading => "Finding lyrics…",
            IslandMode.Paused => "Paused",
            IslandMode.NoLyrics or IslandMode.SongChange => $"{model.Track?.Title}\u0001{model.Track?.Artist}",
            _ => null,
        };
        if (row == _mode && text == _text && scale == _scale) return;
        _mode = row;
        _text = text;
        _scale = scale;

        var trailing = row switch
        {
            IslandMode.Loading or IslandMode.Paused => 14,
            IslandMode.NoLyrics => 6,
            _ => 12,
        };
        Body.Padding = new Thickness(6, 0, trailing, 0);

        Art.Opacity = row == IslandMode.Paused ? 0.55 : 1;
        PauseGlyph.Visibility = Show(row == IslandMode.Paused);
        TextSlot.Visibility = Show(text is not null);
        Dots.Visibility = Show(row is IslandMode.Interlude or IslandMode.Loading);
        Chip.Visibility = Show(row == IslandMode.NoLyrics);
        Eq.Visibility = Show(row is IslandMode.Lyric or IslandMode.Interlude or IslandMode.Mini or IslandMode.SongChange);

        (Dots.DotSize, Dots.DotSpacing) = row == IslandMode.Interlude ? (7, 6) : (5, 4);
        Dots.InvalidateMeasure();

        MainText.FontSize = 13 * scale;
        TextSlot.Height = 18 * scale;
        MainText.Inlines.Clear();
        switch (row)
        {
            case IslandMode.Lyric:
                MainText.Foreground = Theme.White;
                MainText.MaxWidth = 380 * scale;
                MainText.Text = text;
                break;
            case IslandMode.NoLyrics or IslandMode.SongChange:
                // "Title · Artist", artist dimmed
                MainText.MaxWidth = 300;
                MainText.Inlines.Add(new Run(model.Track?.Title ?? "") { Foreground = Theme.White });
                MainText.Inlines.Add(new Run(" · " + (model.Track?.Artist ?? "")) { Foreground = Theme.White60 });
                break;
            default:
                MainText.Foreground = Theme.White60;
                MainText.MaxWidth = 380 * scale;
                MainText.Text = text;
                break;
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
