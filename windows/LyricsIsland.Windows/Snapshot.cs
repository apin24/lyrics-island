using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// <c>--snapshot &lt;dir&gt;</c>: renders the pill in every state to PNG, off screen, on a flat background
/// at 2× (the size of docs/screenshots/*.png), without needing Spotify or a visible window.
/// </summary>
internal static class Snapshot
{
    // 720×110 points, rendered at 2× like the Mac screenshots (1440×220).
    private const double Width = 720, Height = 110, Scale = 2;

    private static readonly (IslandMode Mode, double Time)[] States =
    [
        (IslandMode.SongChange, 0),
        (IslandMode.Lyric, 16),       // "you said that everything's alright", as in docs/screenshots/lyric.png
        (IslandMode.Interlude, 35),
        (IslandMode.Loading, 0),
        (IslandMode.NoLyrics, 0),
        (IslandMode.Mini, 0),
        (IslandMode.Paused, 0),
    ];

    public static void RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        Animator.Manual = true;
        AnimatedElement.TimeOverride = 0.45; // a moment where the equalizer bars differ
        var model = new IslandModel(new IslandSettings(), new NoLyrics());
        foreach (var (mode, time) in States)
        {
            model.LoadPreview(time);
            model.ForcedMode = mode;
            var name = mode == IslandMode.SongChange ? "songChange" : mode == IslandMode.NoLyrics ? "noLyrics" : mode.ToString().ToLowerInvariant();
            Save(Render(model), Path.Combine(directory, $"{name}.png"));
        }
    }

    private static BitmapSource Render(IslandModel model)
    {
        var pill = new PillView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
        pill.Render(model);
        pill.Finish();
        var root = new Grid { Width = Width, Height = Height, Background = Background(), Children = { pill } };
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)(Width * Scale), (int)(Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Close to the wallpaper in the Mac screenshots: indigo → purple → coral.</summary>
    private static Brush Background() => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x24, 0x1A, 0x52), 0),
            new GradientStop(Color.FromRgb(0x68, 0x26, 0x70), 0.5),
            new GradientStop(Color.FromRgb(0xE8, 0x78, 0x5A), 1),
        },
    };

    private static void Save(BitmapSource bitmap, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private sealed class NoLyrics : ILyricsProvider
    {
        public Task<LyricsResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) =>
            Task.FromResult(LyricsResult.NotFound);
    }
}
