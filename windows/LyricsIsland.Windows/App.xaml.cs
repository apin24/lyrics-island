using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

public partial class App : Application, ITrayHost
{
    private LyricsService? _lyrics;
    private IPlayer? _player;
    private TrayIcon? _tray;
    private IslandWindow? _window;
    private AppOptions _options = new();
    private Mutex? _singleInstance;

    public IslandModel Model { get; private set; } = null!;
    public IslandSettings Settings { get; private set; } = null!;
    public bool UsingDemo => _options.Demo || Settings.DemoMode;
    public bool DemoForced => _options.Demo;
    public bool HotkeyRegistered => _window?.HotkeyRegistered ?? false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = AppOptions.Parse(e.Args);
        var options = _options;
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

        // One island per user session (a second copy would fight over the hotkey and the screen).
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\LyricsIsland", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // Settings live in %APPDATA%\LyricsIsland\settings.json and are saved on every change.
        var settingsFile = options.SettingsFile ?? AppPaths.SettingsFile;
        Settings = IslandSettings.Load(settingsFile);
        if (options.Position is { } placement) Settings.Placement = placement;
        Settings.SaveOnChange(settingsFile);

        _lyrics = new LyricsService(AppPaths.CacheDirectory);
        Model = new IslandModel(Settings, _lyrics);

        _window = new IslandWindow(Model, Settings.Placement);
        _window.Show();
        _window.HotkeyPressed += () => Settings.ShowIsland = !Settings.ShowIsland;
        ExpandedPanel.ManualSearchRequested += SearchLyricsManually;
        Settings.PropertyChanged += OnSettingChanged;

        // Started on the UI thread, so the players' updates arrive on it too.
        AttachPlayer();
        if (options.PauseAfter is { } pause && _player is { } player)
        {
            if (pause <= 0) player.PlayPause();
            else After(TimeSpan.FromSeconds(pause), player.PlayPause);
        }

        if (options.Expand) Model.SimulatedHover = true;
        _tray = new TrayIcon(this);
        if (options.ShowMenuAt is { } at) After(TimeSpan.FromSeconds(1.5), () => _tray.ShowMenuAt(at.X, at.Y));

        if (options.DiagnoseFile is { } file)
        {
            // Rewritten every 2s, so a script can watch it change (e.g. while the display scale changes).
            var window = _window;
            var diagnose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.DiagnoseSeconds) };
            diagnose.Tick += (_, _) => File.WriteAllText(file, $"time: {DateTime.Now:HH:mm:ss}\n" + window.Diagnose() +
                $"settings: show {Settings.ShowIsland}, hover {Settings.ExpandOnHover}, fullscreen {Settings.HideInFullscreen}, " +
                $"text {Settings.TextSize}, placement {Settings.Placement}, demo {UsingDemo}, hotkey {HotkeyRegistered}, " +
                $"start with Windows {StartWithWindows.IsEnabled}\n");
            diagnose.Start();
        }
        if (options.LineLogFile is { } log) StartLineLog(log);
        if (options.ExitAfter is { } seconds) After(TimeSpan.FromSeconds(seconds), Shutdown);
    }

    public void SearchLyricsManually()
    {
        if (_lyrics is { } lyrics && !UsingDemo) ManualSearchWindow.Show(Model, lyrics);
    }

    public void Quit() => Shutdown();

    /// <summary>Settings take effect at once; most go through the model, these need the app.</summary>
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandSettings.Placement):
                _window?.SetPosition(Settings.Placement);
                break;
            case nameof(IslandSettings.DemoMode):
                if (!_options.Demo) AttachPlayer();
                break;
        }
    }

    /// <summary>The demo player or the media controls, swapped without a restart.</summary>
    private void AttachPlayer()
    {
        var old = _player;
        _player = UsingDemo ? new DemoPlayer(_options.DemoStart) : new SmtcPlayer();
        Model.Attach(_player); // stops the old one
        (old as IDisposable)?.Dispose();
    }

    private void StartLineLog(string log)
    {
        // For checking line flicker after a seek: one row per line change, no lyric text.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var writer = new StreamWriter(log, append: false) { AutoFlush = true };
        writer.WriteLine("ms,position,line,mode");
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IslandModel.Playback))
            {
                writer.WriteLine($"{clock.ElapsedMilliseconds},playback {Model.Playback}");
            }
            else if (args.PropertyName is nameof(IslandModel.LineIndex) or nameof(IslandModel.Mode) or nameof(IslandModel.Track))
            {
                writer.WriteLine(string.Join(',', clock.ElapsedMilliseconds,
                    Model.CurrentTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                    Model.LineIndex?.ToString() ?? "-", Model.Mode));
            }
        };
        if (_window is { } window) window.Trace = message => writer.WriteLine($"{clock.ElapsedMilliseconds},{message}");
        Exit += (_, _) => writer.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _player?.Stop();
        (_player as IDisposable)?.Dispose();
        _lyrics?.Dispose();
        _singleInstance?.Dispose();
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
