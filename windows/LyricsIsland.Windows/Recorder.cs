using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// <c>--record &lt;dir&gt;</c>: plays a scripted demo off screen on a simulated clock and writes every frame
/// as a PNG (2×, flat background, so GIFs don't band). The script covers appear, song change, line
/// changes, interlude, a track without lyrics, pause and disappear. <c>tools/record-demo.ps1</c> turns
/// the frames into a GIF with ffmpeg.
/// </summary>
internal static class Recorder
{
    public const int Fps = 25;
    private const double Width = 420, Height = 84, Scale = 2;
    private static readonly Color BackgroundColor = Color.FromRgb(0x2B, 0x22, 0x44);

    public static void Record(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory, "frame-*.png")) File.Delete(old);

        Animator.Manual = true;
        var time = new ManualTime();
        var settings = new IslandSettings { ExpandOnHover = false };
        using var lyrics = new LyricsService(Path.Combine(Path.GetTempPath(), "LyricsIslandRecorder"));
        var model = new IslandModel(settings, lyrics, time);
        var player = new ScriptPlayer(time);
        model.Attach(player);

        var pill = new PillView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
        var root = new Grid { Width = Width, Height = Height, Background = new SolidColorBrush(BackgroundColor), Children = { pill } };
        model.PropertyChanged += (_, _) => pill.Render(model);

        var song = DemoPlayer.Tracks[0];
        var noLyrics = DemoPlayer.Tracks[1];
        // (seconds into the recording, what happens)
        var script = new (double At, Action Do)[]
        {
            (0.4, () => player.Emit(song, PlaybackState.Playing, 16.0)),       // appear + song change (2.5s)
            (5.4, () => player.Emit(song, PlaybackState.Playing, 23.6)),       // skip ahead: next line at 5.8s
            (7.3, () => player.Emit(song, PlaybackState.Playing, 28.2)),       // next line at 7.6s
            (9.1, () => player.Emit(song, PlaybackState.Playing, 32.6)),       // blank line at 9.5s: interlude
            (11.0, () => player.Emit(noLyrics, PlaybackState.Playing, 0)),     // song change, then "No lyrics"
            (15.0, () => player.Emit(noLyrics, PlaybackState.Paused, 4.0)),    // paused
            (16.5, () => player.Emit(null, PlaybackState.Stopped, 0)),         // disappear
        };
        const double length = 17.5;

        var next = 0;
        var frames = (int)(length * Fps);
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)Fps;
            time.Seconds = t;
            while (next < script.Length && script[next].At <= t) script[next++].Do();

            model.Tick(false);
            pill.Render(model);
            if (frame > 0) Animator.StepAll(1.0 / Fps);
            AnimatedElement.TimeOverride = t;
            Invalidate(root);
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)(Width * Scale), (int)(Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"frame-{frame:D4}.png"));
            encoder.Save(stream);
        }
    }

    /// <summary>The equalizer and dots draw from <see cref="AnimatedElement.TimeOverride"/>; ask them to redraw.</summary>
    private static void Invalidate(DependencyObject element)
    {
        if (element is AnimatedElement animated) animated.InvalidateVisual();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Invalidate(VisualTreeHelper.GetChild(element, i));
    }

    private sealed class ManualTime : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public double Seconds { get; set; }

        public override DateTimeOffset GetUtcNow() => Start.AddSeconds(Seconds);
    }

    private sealed class ScriptPlayer(TimeProvider time) : IPlayer
    {
        public event Action<PlayerSnapshot>? Updated;
        public event Action<PlayerIssue?>? IssueChanged;

        public string Name => "Demo";

        public void Emit(Track? track, PlaybackState state, double position) =>
            Updated?.Invoke(new PlayerSnapshot(track, state, position, time.GetUtcNow()));

        public void Start() => IssueChanged?.Invoke(null);
        public void Stop() { }
        public void PlayPause() { }
        public void Next() { }
        public void Previous() { }
        public void Seek(double seconds) { }
        public void Activate() { }
    }
}
