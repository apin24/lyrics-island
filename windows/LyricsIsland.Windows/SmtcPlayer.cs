using System.Diagnostics;
using System.Windows.Threading;
using LyricsIsland.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using SmtcStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace LyricsIsland.Windows;

/// <summary>
/// The Windows media controls (SMTC): the same data as the media flyout. Prefers the Spotify session and
/// falls back to the current one (e.g. a browser). Findings and decisions: docs/windows-port.md,
/// "Findings (milestone 1)".
/// <list type="bullet">
/// <item>Reports are exact and timestamped, so <see cref="ResyncThreshold"/> is 0: the model takes every one.</item>
/// <item>Events arrive on background threads; snapshots are posted to the thread that called <see cref="Start"/>,
/// and an older read never overwrites a newer one.</item>
/// <item>There's no track ID; the key is title|artist|album|duration. A song keeps its first key even if
/// its duration arrives late (the timeline and the media properties come in separate events).</item>
/// </list>
/// </summary>
public sealed class SmtcPlayer : IPlayer, IDisposable
{
    private readonly object _gate = new();
    private SynchronizationContext? _ui;
    private SessionManager? _manager;
    private Session? _session;
    private DispatcherTimer? _poll;
    private Track? _track;
    private string _title = "", _artist = "", _album = "";
    private IRandomAccessStreamReference? _thumbnail;
    private string? _artworkFor;
    private PlaybackState _state = PlaybackState.Stopped;
    private long _readSequence;
    private long _postedSequence;
    private bool _disposed;

    public event Action<PlayerSnapshot>? Updated;
    public event Action<PlayerIssue?>? IssueChanged;

    public string Name { get; private set; } = "Spotify";
    public bool CanSeek { get; private set; } = true;
    public double ResyncThreshold => 0;
    public string? SessionId { get; private set; }

    public void Start()
    {
        _ui = SynchronizationContext.Current;
        _ = InitAsync();
        // Events are reliable (milestone 1), but re-reading once a second costs nothing and covers a missed one.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh(includeMedia: false);
        _poll.Start();
    }

    public void Stop()
    {
        _poll?.Stop();
        lock (_gate)
        {
            if (_session is { } s) Detach(s);
            _session = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    // ── Commands ─────────────────────────────────────────────────────────

    public void PlayPause() => Command(s => s.TryTogglePlayPauseAsync());
    public void Next() => Command(s => s.TrySkipNextAsync());
    public void Previous() => Command(s => s.TrySkipPreviousAsync());

    public void Seek(double seconds) => Command(s =>
    {
        var start = s.GetTimelineProperties().StartTime;
        return s.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(Math.Max(0, seconds))).Ticks);
    });

    /// <summary>Opens the player: Spotify through its URI, anything else through its app ID.</summary>
    public void Activate()
    {
        try
        {
            var id = SessionId ?? "";
            var target = id.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || id.Length == 0
                ? "spotify:"
                : $"shell:AppsFolder\\{id}";
            Process.Start(new ProcessStartInfo(target.StartsWith("shell:") ? "explorer.exe" : target)
            {
                Arguments = target.StartsWith("shell:") ? target : "",
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to open.
        }
    }

    private void Command(Func<Session, global::Windows.Foundation.IAsyncOperation<bool>> start)
    {
        Session? s;
        lock (_gate) s = _session;
        if (s is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await start(s);
            }
            catch (Exception)
            {
                // The app may have closed meanwhile.
            }
        });
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
        }
        catch (Exception)
        {
            Post(() => IssueChanged?.Invoke(PlayerIssue.NoSupportedPlayer));
            return;
        }
        _manager.SessionsChanged += (_, _) => SelectSession();
        _manager.CurrentSessionChanged += (_, _) => SelectSession();
        SelectSession();
    }

    private void SelectSession()
    {
        if (_manager is not { } manager || _disposed) return;
        Session? pick;
        try
        {
            pick = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                   ?? manager.GetCurrentSession();
        }
        catch (Exception)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(pick, _session)) return;
            if (_session is { } old) Detach(old);
            _session = pick;
            if (pick is not null) Attach(pick);
            SessionId = pick?.SourceAppUserModelId;
            Name = FriendlyName(SessionId);
            _title = _artist = _album = "";
            _thumbnail = null;
        }
        Post(() => IssueChanged?.Invoke(pick is null ? PlayerIssue.NoSupportedPlayer : null));
        Refresh(includeMedia: true);
    }

    private void Attach(Session s)
    {
        s.MediaPropertiesChanged += OnMediaChanged;
        s.PlaybackInfoChanged += OnPlaybackChanged;
        s.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void Detach(Session s)
    {
        try
        {
            s.MediaPropertiesChanged -= OnMediaChanged;
            s.PlaybackInfoChanged -= OnPlaybackChanged;
            s.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch (Exception)
        {
            // Session already gone.
        }
    }

    private void OnMediaChanged(Session sender, MediaPropertiesChangedEventArgs args) => Refresh(includeMedia: true);

    private void OnPlaybackChanged(Session sender, PlaybackInfoChangedEventArgs args) => Refresh(includeMedia: false);

    private void OnTimelineChanged(Session sender, TimelinePropertiesChangedEventArgs args) => Refresh(includeMedia: false);

    private static string FriendlyName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "Spotify";
        if (appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        var name = appId.Contains('!') ? appId[(appId.LastIndexOf('!') + 1)..] : appId;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? appId : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // ── Reading ──────────────────────────────────────────────────────────

    private void Refresh(bool includeMedia) => _ = RefreshAsync(includeMedia);

    private async Task RefreshAsync(bool includeMedia)
    {
        Session? s;
        lock (_gate) s = _session;
        var sequence = Interlocked.Increment(ref _readSequence);
        if (s is null)
        {
            PostSnapshot(sequence, PlayerSnapshot.Stopped);
            return;
        }

        try
        {
            var needMedia = includeMedia;
            lock (_gate) needMedia |= _title.Length == 0;
            if (needMedia)
            {
                var props = await s.TryGetMediaPropertiesAsync();
                lock (_gate)
                {
                    _title = props?.Title ?? "";
                    _artist = props?.Artist ?? "";
                    _album = props?.AlbumTitle ?? "";
                    _thumbnail = props?.Thumbnail;
                }
            }

            var info = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var now = DateTimeOffset.Now;
            PlayerSnapshot snapshot;
            Track? artworkTrack = null;
            IRandomAccessStreamReference? thumbnail = null;
            lock (_gate)
            {
                CanSeek = info.Controls.IsPlaybackPositionEnabled;
                _state = info.PlaybackStatus switch
                {
                    SmtcStatus.Playing => PlaybackState.Playing,
                    SmtcStatus.Paused => PlaybackState.Paused,
                    SmtcStatus.Changing => _state, // between two tracks
                    _ => PlaybackState.Stopped,
                };
                if (_title.Length == 0 || _state == PlaybackState.Stopped)
                {
                    snapshot = PlayerSnapshot.Stopped;
                }
                else
                {
                    var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                    if (_track is { } same && same.Title == _title && same.Artist == _artist && same.Album == _album)
                    {
                        // Same song: keep its key (cache, offset) even if the duration came late.
                        if (Math.Abs(same.Duration - duration) > 0.001 && duration > 0) _track = same with { Duration = duration };
                    }
                    else
                    {
                        _track = new Track(TrackKey.Create(_title, _artist, _album, duration), _title, _artist, _album, duration);
                    }
                    // Some apps never set LastUpdatedTime (it comes back as 1601): then the report is "now".
                    var stamp = timeline.LastUpdatedTime.Year >= 2000 ? timeline.LastUpdatedTime : now;
                    snapshot = new PlayerSnapshot(_track, _state, (timeline.Position - timeline.StartTime).TotalSeconds, stamp);
                    if (_artworkFor != _track.Id && _thumbnail is not null)
                    {
                        _artworkFor = _track.Id;
                        artworkTrack = _track;
                        thumbnail = _thumbnail;
                    }
                }
            }
            PostSnapshot(sequence, snapshot);
            if (artworkTrack is not null && thumbnail is not null) await LoadArtworkAsync(artworkTrack, thumbnail);
        }
        catch (Exception)
        {
            // The session went away mid-read; the next event or poll reads again.
        }
    }

    private void PostSnapshot(long sequence, PlayerSnapshot snapshot) => Post(() =>
    {
        // Reads overlap (events + poll); never let an older one overwrite a newer one.
        if (sequence < _postedSequence) return;
        _postedSequence = sequence;
        Updated?.Invoke(snapshot);
    });

    private async Task LoadArtworkAsync(Track track, IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            var image = await ArtworkStore.DecodeAsync(bytes);
            if (image is not null) Post(() => ArtworkStore.Set(track.Id, image));
        }
        catch (Exception)
        {
            lock (_gate) _artworkFor = null; // try again on the next update
        }
    }

    private void Post(Action action)
    {
        if (_ui is { } ui) ui.Post(_ => action(), null);
        else action();
    }
}
