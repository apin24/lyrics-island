using System.IO;
using System.Windows;
using System.Windows.Threading;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

public partial class App : Application
{
    private LyricsService? _lyrics;
    private IPlayer? _player;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Parse(e.Args);
        if (options.Shadow is { } shadow) PillView.CompactShadow = shadow;

        if (options.SnapshotDirectory is { } directory)
        {
            Snapshot.RenderAll(directory);
            Shutdown();
            return;
        }
        if (options.RecordDirectory is { } frames)
        {
            Recorder.Record(frames);
            Shutdown();
            return;
        }

        // Settings aren't saved yet (milestone 6).
        var settings = new IslandSettings();
        _lyrics = new LyricsService(AppPaths.CacheDirectory);
        var model = new IslandModel(settings, _lyrics);

        var window = new IslandWindow(model, options.Position);
        window.Show();
        var lyrics = _lyrics;
        ExpandedPanel.ManualSearchRequested += () => ManualSearchWindow.Show(model, lyrics);

        // Started on the UI thread, so the players' updates arrive on it too.
        _player = options.Demo ? new DemoPlayer(options.DemoStart) : new SmtcPlayer();
        model.Attach(_player);
        if (options.PauseAfter is { } pause)
        {
            var player = _player;
            if (pause <= 0) player.PlayPause();
            else After(TimeSpan.FromSeconds(pause), player.PlayPause);
        }

        _tray = new TrayIcon(() => window.Position, window.SetPosition, Shutdown);

        if (options.DiagnoseFile is { } file)
        {
            // Rewritten every 2s, so a script can watch it change (e.g. while the display scale changes).
            var diagnose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.DiagnoseSeconds) };
            diagnose.Tick += (_, _) => File.WriteAllText(file, $"time: {DateTime.Now:HH:mm:ss}\n" + window.Diagnose());
            diagnose.Start();
        }
        if (options.LineLogFile is { } log)
        {
            // For checking line flicker after a seek: one row per line change, no lyric text.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var writer = new StreamWriter(log, append: false) { AutoFlush = true };
            writer.WriteLine("ms,position,line,mode");
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(IslandModel.LineIndex) or nameof(IslandModel.Mode) or nameof(IslandModel.Track))
                {
                    writer.WriteLine(string.Join(',', clock.ElapsedMilliseconds,
                        model.CurrentTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        model.LineIndex?.ToString() ?? "-", model.Mode));
                }
            };
            window.Trace = message => writer.WriteLine($"{clock.ElapsedMilliseconds},{message}");
            Exit += (_, _) => writer.Dispose();
        }
        if (options.ExitAfter is { } seconds)
        {
            After(TimeSpan.FromSeconds(seconds), Shutdown);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _player?.Stop();
        (_player as IDisposable)?.Dispose();
        _lyrics?.Dispose();
        base.OnExit(e);
    }

    private void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
