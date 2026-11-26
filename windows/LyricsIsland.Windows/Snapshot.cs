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

        // The lyric pill at the 125% and 150% display scales (1×, i.e. what a screen at that scale shows).
        model.LoadPreview(16);
        model.ForcedMode = IslandMode.Lyric;
        foreach (var dpiScale in new[] { 1.25, 1.5 })
        {
            Save(Render(model, dpiScale), Path.Combine(directory, $"lyric@{dpiScale * 100:0}.png"));
        }

        // The hover panel, 720×360 like the Mac's expanded.png; 16.2s = "you said that" lit, 0:16 / -1:01.
        foreach (var (name, status) in new[] { ("expanded", (LyricsStatus?)null), ("expanded-noLyrics", LyricsStatus.NotFound), ("expanded-loading", LyricsStatus.Loading) })
        {
            model.LoadPreview(16.2, status);
            model.ForcedMode = IslandMode.Expanded;
            Save(Render(model, Scale, ExpandedHeight), Path.Combine(directory, $"{name}.png"));
        }
    }

    private const double ExpandedHeight = 360;

    private static BitmapSource Render(IslandModel model, double scale = Scale, double height = Height)
    {
        var pill = new PillView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
        pill.Render(model);
        pill.Finish();
        var root = new Grid { Width = Width, Height = height, Background = Background(), Children = { pill } };
        root.Measure(new Size(Width, height));
        root.Arrange(new Rect(0, 0, Width, height));
        root.UpdateLayout();
        // Second pass: rows measure their content on the first layout (text, karaoke).
        pill.Render(model);
        pill.Finish();
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)(Width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
