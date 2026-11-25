using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Foundation;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace LyricsIsland.SmtcProbe;

/// <summary>
/// Follows one SMTC session and keeps two clocks for its position:
/// <list type="bullet">
/// <item><b>smtc</b>: <c>timeline.Position + (now - timeline.LastUpdatedTime)</c> while playing.</item>
/// <item><b>local</b>: a smooth clock ticking on its own, using the Mac's <c>IslandModel.apply</c>
/// rule: take the player's value on a track or play-state change, otherwise only when it drifts
/// more than <see cref="ResyncThreshold"/> from the smtc value.</item>
/// </list>
/// Every timeline change is logged with how old its timestamp was and how far it jumped
/// from the previous extrapolation, so we can see how Spotify actually updates it.
/// </summary>
internal sealed class Probe : IDisposable
{
    /// <summary>Same threshold as the Mac app's <c>IslandModel</c>.</summary>
    private const double ResyncThreshold = 0.35;

    /// <summary>A timeline change bigger than this is a seek or a track change, not drift.</summary>
    private const double SeekThreshold = 1.0;

    private readonly ProbeOptions _options;
    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StreamWriter? _log;
    private readonly StreamWriter? _csv;

    private SessionManager? _manager;
    private Session? _session;
    private string _sessionId = "";

    private TrackInfo? _track;
    private DateTimeOffset _trackChangedAt = DateTimeOffset.MinValue;
    private PlaybackStatus _status = PlaybackStatus.Closed;
    private double _rate = 1;
    private bool? _canSeek;

    private Timeline? _timeline;
    private bool _timelineWasPlaying;
    private DateTimeOffset _timelineReceivedAt;

    private bool _hasLocal;
    private TimeSpan _localAnchor;
    private TimeSpan _localAnchorAt;
    private bool _localRunning;
    private DateTimeOffset _lastWaitingLine = DateTimeOffset.MinValue;

    // Statistics for the summary.
    private readonly Stats _updateIntervals = new();
    private readonly Stats _timestampAges = new();
    private readonly Stats _updateJumps = new();
    private readonly Stats _localDrift = new();
    private readonly HashSet<string> _tracksSeen = [];
    private readonly HashSet<string> _sessionsSeen = [];
    private int _ticks;
    private int _timelineEvents;
    private int _timelineChanges;
    private int _changesFirstSeenByPoll;
    private int _seekJumps;
    private int _resyncs;
    private int _missingTimestamps;

    public Probe(ProbeOptions options)
    {
        _options = options;
        if (options.LogPath is { } log) _log = new StreamWriter(log, append: false, new UTF8Encoding(false));
        if (options.CsvPath is { } csv)
        {
            _csv = new StreamWriter(csv, append: false, new UTF8Encoding(false));
            _csv.WriteLine("time,status,local_s,smtc_s,drift_s,reported_s,last_updated,end_s,resync,title,artist");
        }
    }

    public async Task RunAsync(CancellationTokenSource cts)
    {
        Print($"SMTC probe · prefer app \"{(_options.CurrentOnly ? "<current session>" : _options.App)}\" · tick {_options.TickMs} ms · Ctrl+C to stop");
        if (!Console.IsInputRedirected)
            Print("Keys: Space play/pause · N next · P previous · ←/→ seek ∓10s · Q quit");

        _manager = await SessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => SelectSession("SessionsChanged");
        _manager.CurrentSessionChanged += (_, _) => SelectSession("CurrentSessionChanged");
        SelectSession("startup");

        if (_options.Exercise) _ = ExerciseAsync(cts.Token);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.TickMs));
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                Tick();
                HandleKeys(cts);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, Q or --duration.
        }

        if (_options.Exercise) await RestoreAsync();
    }

    // ── --exercise ───────────────────────────────────────────────────────

    private bool _startedPlayback;
    private TrackInfo? _originalTrack;
    private TimeSpan _originalPosition;

    /// <summary>
    /// Scripted run: play, seek, pause/resume, next track. <see cref="RestoreAsync"/> puts the
    /// player back on the original track and position afterwards.
    /// </summary>
    private async Task ExerciseAsync(CancellationToken ct)
    {
        try
        {
            await DelayUntil(1, ct);
            if (CurrentSession() is not { } s)
            {
                Print("EXERCISE no session to drive");
                return;
            }
            bool playing;
            lock (_gate)
            {
                playing = _status == PlaybackStatus.Playing;
                _originalTrack = _track;
                _originalPosition = LocalPosition();
            }
            Print($"EXERCISE start · original position {Fmt(_originalPosition)}");
            if (!playing) _startedPlayback = await ControlAsync("play", () => s.TryPlayAsync());

            await DelayUntil(12, ct);
            await Step("seek +20s", session =>
            {
                TimeSpan target;
                lock (_gate) target = LocalPosition() + TimeSpan.FromSeconds(20);
                return session.TryChangePlaybackPositionAsync(target.Ticks);
            });

            await DelayUntil(20, ct);
            await Step("pause", session => session.TryPauseAsync());

            await DelayUntil(27, ct);
            await Step("play", session => session.TryPlayAsync());

            await DelayUntil(34, ct);
            await Step("next track", session => session.TrySkipNextAsync());
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Step(string what, Func<Session, IAsyncOperation<bool>> action)
    {
        if (CurrentSession() is { } s) await ControlAsync(what, () => action(s));
    }

    private async Task RestoreAsync()
    {
        if (CurrentSession() is not { } s) return;

        Print("EXERCISE restoring the original track, position and play state");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            TrackInfo? now;
            lock (_gate) now = _track;
            if (_originalTrack is null || now == _originalTrack) break;
            await ControlAsync("previous (restore)", () => s.TrySkipPreviousAsync());
            await Task.Delay(1500);
        }

        var target = _originalPosition;
        await ControlAsync($"seek to {Fmt(target)} (restore)", () => s.TryChangePlaybackPositionAsync(target.Ticks));
        if (_startedPlayback) await ControlAsync("pause (restore)", () => s.TryPauseAsync());
        await Task.Delay(1500); // let the last events arrive so they show up in the log
    }

    private Task DelayUntil(double seconds, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(seconds) - _clock.Elapsed;
        return wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;
    }

    private Session? CurrentSession()
    {
        lock (_gate) return _session;
    }

    // ── Session selection ────────────────────────────────────────────────

    private void SelectSession(string reason)
    {
        var manager = _manager;
        if (manager is null) return;

        IReadOnlyList<Session> sessions;
        Session? current;
        try
        {
            sessions = manager.GetSessions();
            current = manager.GetCurrentSession();
        }
        catch (Exception e)
        {
            Print($"SESSION  error while listing sessions: {e.Message}");
            return;
        }

        var preferred = _options.CurrentOnly
            ? null
            : sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains(_options.App, StringComparison.OrdinalIgnoreCase));
        var pick = preferred ?? current;
        var pickId = pick?.SourceAppUserModelId ?? "";

        Session? old;
        lock (_gate)
        {
            if (ReferenceEquals(pick, _session)) return;
            old = _session;
            _session = pick;
            var changedApp = pickId != _sessionId;
            _sessionId = pickId;
            if (changedApp)
            {
                // A different app: forget everything we knew about the old one.
                _track = null;
                _timeline = null;
                _status = PlaybackStatus.Closed;
                _canSeek = null;
                _hasLocal = false;
            }
        }

        if (old is not null) Detach(old);

        var all = sessions.Count == 0 ? "none" : string.Join(", ", sessions.Select(s => s.SourceAppUserModelId));
        var source = pick is null ? "none"
            : preferred is not null ? "preferred app"
            : "current session (fallback)";
        Print($"SESSION  ({reason}) following: {(pick is null ? "-" : pickId)} [{source}] · all sessions: {all}");

        if (pick is null) return;
        lock (_gate) _sessionsSeen.Add(pickId);
        Attach(pick);
        RefreshPlayback(pick);
        RefreshTimeline(pick, "initial");
        _ = RefreshMediaAsync(pick);
    }

    private void Attach(Session s)
    {
        s.MediaPropertiesChanged += OnMediaPropertiesChanged;
        s.PlaybackInfoChanged += OnPlaybackInfoChanged;
        s.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void Detach(Session s)
    {
        try
        {
            s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception e)
        {
            // The session may already be gone (app closed).
            Print($"SESSION  detach failed: {e.Message}");
        }
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args)
    {
        if (IsFollowed(sender)) _ = RefreshMediaAsync(sender);
    }

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args)
    {
        if (IsFollowed(sender)) RefreshPlayback(sender);
    }

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args)
    {
        if (IsFollowed(sender)) RefreshTimeline(sender, "event");
    }

    private bool IsFollowed(Session sender)
    {
        lock (_gate) return ReferenceEquals(sender, _session) || sender.SourceAppUserModelId == _sessionId;
    }

    // ── Reading the session ──────────────────────────────────────────────

    private async Task RefreshMediaAsync(Session s)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            ulong? artworkBytes = null;
            string? artworkType = null;
            if (props.Thumbnail is { } thumbnail)
            {
                try
                {
                    using var stream = await thumbnail.OpenReadAsync();
                    artworkBytes = stream.Size;
                    artworkType = stream.ContentType;
                }
                catch (Exception e)
                {
                    artworkType = $"unreadable ({e.Message})";
                }
            }

            var track = new TrackInfo(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "");
            bool changed;
            TimeSpan duration;
            lock (_gate)
            {
                changed = _track != track;
                if (changed)
                {
                    _track = track;
                    _trackChangedAt = DateTimeOffset.Now;
                    _hasLocal = false; // re-anchor the local clock on the next tick
                }
                duration = _timeline?.Duration ?? TimeSpan.Zero;
            }

            var artwork = artworkBytes is { } bytes ? $"{bytes / 1024.0:0.0} KB {artworkType}" : artworkType ?? "none";
            Print($"MEDIA    {(changed ? "track change" : "props again (same track)")}: \"{track.Title}\" — {track.Artist}" +
                  $" · album \"{track.Album}\" · album artist \"{props.AlbumArtist}\" · type {props.PlaybackType?.ToString() ?? "?"}" +
                  $" · artwork {artwork}");
            if (changed && !track.IsEmpty)
            {
                var key = track.Key(duration);
                lock (_gate) _tracksSeen.Add($"{track.Title} — {track.Artist}");
                Print($"         cache key (duration from current timeline): {key}");
            }
        }
        catch (Exception e)
        {
            Print($"MEDIA    error: {e.Message}");
        }
    }

    private void RefreshPlayback(Session s)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackInfo info;
        try
        {
            info = s.GetPlaybackInfo();
        }
        catch (Exception e)
        {
            Print($"PLAYBACK error: {e.Message}");
            return;
        }

        var status = info.PlaybackStatus;
        var rate = info.PlaybackRate ?? 1.0;
        var c = info.Controls;
        lock (_gate)
        {
            // Mac rule (IslandModel.apply): a state change always takes the player's position.
            if (status != _status || Math.Abs(rate - _rate) > 0.001) _hasLocal = false;
            _status = status;
            _rate = rate;
            _canSeek = c.IsPlaybackPositionEnabled;
            // The latest timeline report is read with the status we know now.
            _timelineWasPlaying = status == PlaybackStatus.Playing;
        }

        Print($"PLAYBACK status {status} · rate {rate:0.##} · type {info.PlaybackType?.ToString() ?? "?"}" +
              $" · controls: play/pause {Flag(c.IsPlayPauseToggleEnabled)} next {Flag(c.IsNextEnabled)}" +
              $" prev {Flag(c.IsPreviousEnabled)} seek {Flag(c.IsPlaybackPositionEnabled)}");
    }

    private void RefreshTimeline(Session s, string source)
    {
        Timeline t;
        try
        {
            t = Timeline.From(s.GetTimelineProperties());
        }
        catch (Exception e)
        {
            Print($"TIMELINE error: {e.Message}");
            return;
        }

        var now = DateTimeOffset.Now;
        string? line = null;
        lock (_gate)
        {
            if (source == "event") _timelineEvents++;

            if (_timeline is { } known && known.SameReportAs(t))
            {
                if (source == "event") line = "TIMELINE (event) no new data; the poll already saw this update";
            }
            else
            {
                var playing = _status == PlaybackStatus.Playing;
                var age = t.HasTimestamp ? (now - t.LastUpdated).TotalSeconds : double.NaN;
                if (!t.HasTimestamp) _missingTimestamps++;
                else if (playing) _timestampAges.Add(age);

                var extra = "";
                if (_timeline is { } previous)
                {
                    var interval = (now - _timelineReceivedAt).TotalSeconds;
                    // Extrapolate the previous report with the status it was made under.
                    var predicted = previous.PositionAt(now, _timelineWasPlaying, _rate);
                    var reported = t.PositionAt(now, playing, _rate);
                    var jump = (reported - predicted).TotalSeconds;
                    var isSeek = Math.Abs(jump) > SeekThreshold;
                    var sameTrackWindow = _timelineReceivedAt > _trackChangedAt;
                    var statusFlipped = playing != _timelineWasPlaying;
                    if (isSeek) _seekJumps++;
                    else if (playing && sameTrackWindow && !statusFlipped)
                    {
                        _updateIntervals.Add(interval);
                        _updateJumps.Add(jump);
                    }
                    extra = $" · {interval:0.000}s since last · jump {jump:+0.000;-0.000;0.000}s" +
                            (isSeek ? " (seek or track change)" : statusFlipped ? " (play/pause)" : "");
                }

                _timeline = t;
                _timelineWasPlaying = playing;
                _timelineReceivedAt = now;
                _timelineChanges++;
                if (source == "poll") _changesFirstSeenByPoll++;

                var stamp = t.HasTimestamp
                    ? $"{t.LastUpdated.ToLocalTime():HH:mm:ss.fff} (age {age:0.000}s)"
                    : $"missing ({t.LastUpdated:O})";
                line = $"TIMELINE ({source}) pos {Fmt(t.Position, 3)} · start {Fmt(t.Start, 3)} · end {Fmt(t.End, 3)}" +
                       $" · seek range {Fmt(t.MinSeek, 1)}–{Fmt(t.MaxSeek, 1)} · updated {stamp}{extra}";
            }
        }

        if (line is not null) Print(line);
    }

    // ── Clock ────────────────────────────────────────────────────────────

    private void Tick()
    {
        Session? session;
        lock (_gate) session = _session;
        if (session is not null) RefreshTimeline(session, "poll");

        var now = DateTimeOffset.Now;
        string? line = null;
        var important = false;
        lock (_gate)
        {
            _ticks++;
            if (session is null || _timeline is not { } t)
            {
                if (now - _lastWaitingLine > TimeSpan.FromSeconds(5))
                {
                    _lastWaitingLine = now;
                    line = session is null
                        ? "waiting for a media session… (start playback in Spotify or a browser)"
                        : "waiting for timeline data…";
                }
            }
            else
            {
                var playing = _status == PlaybackStatus.Playing;
                var smtc = t.PositionAt(now, playing, _rate);
                // Mac rule: only a running clock is kept; paused/stopped always shows the player's value.
                if (!_hasLocal || !playing) SetLocal(smtc, playing);
                var local = LocalPosition();
                var drift = (smtc - local).TotalSeconds;
                var resync = Math.Abs(drift) > ResyncThreshold;
                var note = "";
                if (resync)
                {
                    SetLocal(smtc, playing);
                    _resyncs++;
                    note = $"  ← resync, local clock was {(drift > 0 ? "behind" : "ahead")} by {Math.Abs(drift):0.000}s";
                    local = smtc;
                    important = true;
                }
                else if (playing)
                {
                    _localDrift.Add(drift);
                }

                var icon = _status switch
                {
                    PlaybackStatus.Playing => "▶",
                    PlaybackStatus.Paused => "❚❚",
                    _ => "■",
                };
                var title = _track is { IsEmpty: false } tr ? $"{tr.Title} — {tr.Artist}" : "(no title yet)";
                line = $"{icon,-2} {_status,-8} local {Fmt(local)} / {Fmt(t.Duration)} · smtc {Fmt(smtc)} · Δ {drift:+0.000;-0.000;0.000}s │ {title}{note}";

                _csv?.WriteLine(string.Join(',',
                    now.ToString("O"),
                    _status,
                    Sec(local),
                    Sec(smtc),
                    drift.ToString("0.000"),
                    Sec(t.Position),
                    t.HasTimestamp ? t.LastUpdated.ToString("O") : "",
                    Sec(t.End),
                    resync ? "1" : "0",
                    Csv(_track?.Title),
                    Csv(_track?.Artist)));
            }
        }

        if (line is not null && (_options.PrintTicks || important || session is null || _timeline is null))
            Print(line);
    }

    private TimeSpan LocalPosition()
    {
        if (!_hasLocal) return TimeSpan.Zero;
        if (!_localRunning) return _localAnchor;
        var pos = _localAnchor + (_clock.Elapsed - _localAnchorAt) * _rate;
        var end = _timeline?.End ?? TimeSpan.Zero;
        return end > TimeSpan.Zero && pos > end ? end : pos;
    }

    private void SetLocal(TimeSpan position, bool running)
    {
        _hasLocal = true;
        _localAnchor = position;
        _localAnchorAt = _clock.Elapsed;
        _localRunning = running;
    }

    // ── Keyboard controls ────────────────────────────────────────────────

    private void HandleKeys(CancellationTokenSource cts)
    {
        if (Console.IsInputRedirected) return;
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            Session? s;
            TimeSpan local;
            lock (_gate)
            {
                s = _session;
                local = LocalPosition();
            }

            if (key is ConsoleKey.Q or ConsoleKey.Escape)
            {
                cts.Cancel();
                return;
            }
            if (s is null) continue;

            switch (key)
            {
                case ConsoleKey.Spacebar:
                    _ = ControlAsync("toggle play/pause", () => s.TryTogglePlayPauseAsync());
                    break;
                case ConsoleKey.N:
                    _ = ControlAsync("next", () => s.TrySkipNextAsync());
                    break;
                case ConsoleKey.P:
                    _ = ControlAsync("previous", () => s.TrySkipPreviousAsync());
                    break;
                case ConsoleKey.RightArrow or ConsoleKey.LeftArrow:
                    var target = local + TimeSpan.FromSeconds(key == ConsoleKey.RightArrow ? 10 : -10);
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    _ = ControlAsync($"seek to {Fmt(target)}", () => s.TryChangePlaybackPositionAsync(target.Ticks));
                    break;
            }
        }
    }

    private async Task<bool> ControlAsync(string what, Func<IAsyncOperation<bool>> start)
    {
        try
        {
            var sent = DateTimeOffset.Now;
            var accepted = await start();
            Print($"CONTROL  {what} → {(accepted ? "accepted" : "rejected")} after {(DateTimeOffset.Now - sent).TotalMilliseconds:0} ms");
            return accepted;
        }
        catch (Exception e)
        {
            Print($"CONTROL  {what} → error: {e.Message}");
            return false;
        }
    }

    // ── Output ───────────────────────────────────────────────────────────

    public void PrintSummary()
    {
        string seek;
        lock (_gate) seek = _canSeek switch { true => "enabled", false => "DISABLED", null => "unknown" };

        Print("");
        Print("── Summary ─────────────────────────────────────────────");
        Print($"Ran {_clock.Elapsed.TotalSeconds:0.0}s · {_ticks} ticks · sessions followed: {(_sessionsSeen.Count == 0 ? "none" : string.Join(", ", _sessionsSeen))}");
        Print($"Tracks seen ({_tracksSeen.Count}): {(_tracksSeen.Count == 0 ? "-" : string.Join(" | ", _tracksSeen))}");
        Print($"Timeline: {_timelineEvents} TimelinePropertiesChanged events · {_timelineChanges} distinct reports" +
              $" ({_changesFirstSeenByPoll} seen first by polling) · {_seekJumps} seeks/track jumps" +
              $" · {_missingTimestamps} without a LastUpdatedTime");
        Print($"Seconds between timeline reports while playing:  {_updateIntervals}");
        Print($"Age of LastUpdatedTime when we got the report:   {_timestampAges}");
        Print($"Report vs. previous extrapolation (jump, s):     {_updateJumps}");
        Print($"Local clock vs. smtc between resyncs (s):        {_localDrift}");
        Print($"Local clock resyncs (> {ResyncThreshold}s): {_resyncs} · seeking via SMTC: {seek}");
    }

    private void Print(string message)
    {
        var line = message.Length == 0 ? "" : $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (_outputGate)
        {
            Console.WriteLine(line);
            _log?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_outputGate)
        {
            _log?.Dispose();
        }
        lock (_gate)
        {
            _csv?.Dispose();
        }
        if (_session is { } s) Detach(s);
    }

    private static string Flag(bool value) => value ? "yes" : "NO";

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>m:ss.ff</summary>
    private static string Fmt(TimeSpan t, int decimals = 2)
    {
        if (t < TimeSpan.Zero) return "-" + Fmt(-t, decimals);
        var whole = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        if (decimals <= 0) return whole;
        var fraction = (t.TotalSeconds - Math.Floor(t.TotalSeconds)).ToString("F" + decimals, CultureInfo.InvariantCulture);
        // "0.99" → ".99"; rounding up to "1.00" would print ":59.00" for 59.999, close enough for a probe.
        return whole + fraction[fraction.IndexOf('.')..];
    }
}

internal sealed record TrackInfo(string Title, string Artist, string Album)
{
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0;

    /// <summary>SMTC has no track ID, so the app will key its cache and offsets on this.</summary>
    public string Key(TimeSpan duration) => string.Join('|',
        Normalize(Title),
        Normalize(Artist),
        Normalize(Album),
        ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture));

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

internal readonly record struct Timeline(
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Position,
    DateTimeOffset LastUpdated,
    TimeSpan MinSeek,
    TimeSpan MaxSeek)
{
    public static Timeline From(GlobalSystemMediaTransportControlsSessionTimelineProperties p) =>
        new(p.StartTime, p.EndTime, p.Position, p.LastUpdatedTime, p.MinSeekTime, p.MaxSeekTime);

    public TimeSpan Duration => End - Start;

    /// <summary>Some apps never set it; then it comes back as the 1601 FILETIME epoch.</summary>
    public bool HasTimestamp => LastUpdated.Year >= 2000;

    public bool SameReportAs(Timeline other) =>
        Position == other.Position && LastUpdated == other.LastUpdated && End == other.End && Start == other.Start;

    /// <summary>Same idea as the Mac's <c>PlayerSnapshot.position(at:)</c>.</summary>
    public TimeSpan PositionAt(DateTimeOffset now, bool playing, double rate)
    {
        var position = Position;
        if (playing && HasTimestamp) position += (now - LastUpdated) * rate;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (End > TimeSpan.Zero && position > End) position = End;
        return position;
    }
}

internal sealed class Stats
{
    private readonly List<double> _values = [];

    public void Add(double value)
    {
        if (!double.IsNaN(value)) _values.Add(value);
    }

    public override string ToString()
    {
        if (_values.Count == 0) return "n=0";
        var sorted = _values.Order().ToList();
        var median = sorted[sorted.Count / 2];
        var meanAbs = sorted.Average(Math.Abs);
        return $"n={sorted.Count} · min {sorted[0]:+0.000;-0.000;0.000} · median {median:+0.000;-0.000;0.000}" +
               $" · max {sorted[^1]:+0.000;-0.000;0.000} · mean |x| {meanAbs:0.000}";
    }
}
