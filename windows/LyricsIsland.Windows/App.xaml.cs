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

        if (options.SnapshotDirectory is { } directory)
        {
            Snapshot.RenderAll(directory);
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

        _tray = new TrayIcon(() => window.Position, window.SetPosition, Shutdown);

        if (options.DiagnoseFile is { } file)
        {
            After(TimeSpan.FromSeconds(2), () => File.WriteAllText(file, window.Diagnose()));
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
