using System.IO;
using System.Windows;
using System.Windows.Threading;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

public partial class App : Application
{
    private LyricsService? _lyrics;
    private DemoPlayer? _player;
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

        // Settings aren't saved yet (milestone 6). Hover-to-expand stays off until the
        // expanded panel exists (milestone 5).
        var settings = new IslandSettings { ExpandOnHover = false };
        _lyrics = new LyricsService(AppPaths.CacheDirectory);
        var model = new IslandModel(settings, _lyrics);

        var window = new IslandWindow(model, options.Position);
        window.Show();

        // Started on the UI thread, so the demo player's updates arrive on it too.
        _player = new DemoPlayer(options.DemoStart);
        model.Attach(_player);
        if (options.PauseAfter is { } pause)
        {
            if (pause <= 0) _player.PlayPause();
            else After(TimeSpan.FromSeconds(pause), () => _player.PlayPause());
        }

        _tray = new TrayIcon(() => window.Position, window.SetPosition, Shutdown);

        if (options.DiagnoseFile is { } file)
        {
            // Rewritten every 2s, so a script can watch it change (e.g. while the display scale changes).
            var diagnose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            diagnose.Tick += (_, _) => File.WriteAllText(file, $"time: {DateTime.Now:HH:mm:ss}\n" + window.Diagnose());
            diagnose.Start();
        }
        if (options.ExitAfter is { } seconds)
        {
            After(TimeSpan.FromSeconds(seconds), Shutdown);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _player?.Dispose();
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
