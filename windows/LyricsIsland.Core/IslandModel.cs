using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LyricsIsland.Core;

/// <summary>
/// State machine behind the island: what is playing, which lyric line is active, and which state to show.
/// Port of the Mac app's <c>IslandModel.swift</c>, without the UI.
/// <para>
/// Not thread-safe: use it from one thread (the UI thread). The app calls <see cref="Tick"/> about
/// every 100 ms with whether the cursor is over the pill; <see cref="Mode"/> and the other properties
/// raise <see cref="PropertyChanged"/> when they change.
/// </para>
/// </summary>
public sealed class IslandModel : INotifyPropertyChanged
{
    public const double DefaultResyncThreshold = 0.35;
    public static readonly TimeSpan SongChangeDuration = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan PausedHideDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MiniDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HoverExpandDelay = TimeSpan.FromSeconds(0.3);
    public static readonly TimeSpan HoverCollapseDelay = TimeSpan.FromSeconds(1.0);

    private readonly ILyricsProvider _lyricsProvider;
    private readonly TimeProvider _time;
    private IPlayer? _player;
    private PlayerSnapshot _snapshot = PlayerSnapshot.Stopped;
    private DateTimeOffset? _songChangeUntil;
    private DateTimeOffset? _pausedSince;
    private DateTimeOffset? _notFoundSince;
    private DateTimeOffset? _hoverSince;
    private DateTimeOffset? _leaveSince;
    private CancellationTokenSource? _lyricsCancellation;

    private Track? _track;
    private PlaybackState _playback = PlaybackState.Stopped;
    private LyricsStatus _lyricsStatus = LyricsStatus.Idle;
    private double _currentTime;
    private int? _lineIndex;
    private bool _isExpanded;
    private bool _isFullscreen;
    private PlayerIssue? _playerIssue;
    private bool _acceptsMouse;
    private IslandMode _lastMode = IslandMode.Hidden;
    private IslandMode? _forcedMode;

    public IslandModel(IslandSettings settings, ILyricsProvider lyricsProvider, TimeProvider? time = null)
    {
        Settings = settings;
        _lyricsProvider = lyricsProvider;
        _time = time ?? TimeProvider.System;
        Settings.PropertyChanged += (_, _) =>
        {
            // Offset, visibility and hover settings all feed into the derived state.
            UpdateTime();
            NotifyMode();
            OnPropertyChanged(nameof(Offset));
            OnPropertyChanged(nameof(OffsetLabel));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IslandSettings Settings { get; }

    public Track? Track { get => _track; private set => Set(ref _track, value); }
    public PlaybackState Playback { get => _playback; private set => Set(ref _playback, value); }
    public LyricsStatus LyricsStatus { get => _lyricsStatus; private set => Set(ref _lyricsStatus, value); }

    /// <summary>Playback position in seconds, clamped to the track's duration.</summary>
    public double CurrentTime
    {
        get => _currentTime;
        private set
        {
            if (Math.Abs(value - _currentTime) <= 0.0001) return;
            _currentTime = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Index of the active line: the last line with <c>time ≤ position + offset</c>. Null before the first line.</summary>
    public int? LineIndex { get => _lineIndex; private set => Set(ref _lineIndex, value); }

    public bool IsExpanded { get => _isExpanded; private set => Set(ref _isExpanded, value); }

    /// <summary>Set by the app when another app is fullscreen (see <see cref="IslandSettings.HideInFullscreen"/>).</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (Set(ref _isFullscreen, value)) NotifyMode();
        }
    }

    public PlayerIssue? PlayerIssue { get => _playerIssue; private set => Set(ref _playerIssue, value); }

    /// <summary>
    /// True while the cursor is over the visible pill. The window should take mouse input only then,
    /// and be click-through everywhere else.
    /// </summary>
    public bool AcceptsMouse { get => _acceptsMouse; private set => Set(ref _acceptsMouse, value); }

    /// <summary>Forces a state (snapshots and screenshots).</summary>
    public IslandMode? ForcedMode
    {
        get => _forcedMode;
        set
        {
            _forcedMode = value;
            NotifyMode();
        }
    }

    /// <summary>Showcase mode drives hover from a script instead of the real cursor.</summary>
    public bool? SimulatedHover { get; set; }

    public string? PlayerName => _player?.Name;
    public bool CanSeek => _player?.CanSeek ?? false;

    // ── Derived state ────────────────────────────────────────────────────

    /// <summary>Which state the island shows right now. Time-based, so re-read it after every <see cref="Tick"/>.</summary>
    public IslandMode Mode => ComputeMode(_time.GetUtcNow());

    private IslandMode ComputeMode(DateTimeOffset now)
    {
        if (ForcedMode is { } forced) return forced;
        if (!Settings.ShowIsland || (Settings.HideInFullscreen && IsFullscreen)
            || Track is null || Playback == PlaybackState.Stopped)
            return IslandMode.Hidden;
        if (IsExpanded) return IslandMode.Expanded;
        if (Playback == PlaybackState.Paused)
        {
            if (_pausedSince is { } p && now - p > PausedHideDelay) return IslandMode.Hidden;
            return IslandMode.Paused;
        }
        if (_songChangeUntil is { } until && now < until) return IslandMode.SongChange;
        switch (LyricsStatus.State)
        {
            case LyricsState.Idle or LyricsState.Loading:
                return IslandMode.Loading;
            case LyricsState.NotFound:
                if (_notFoundSince is { } since && now - since > MiniDelay) return IslandMode.Mini;
                return IslandMode.NoLyrics;
            default:
                return CurrentLine is not null ? IslandMode.Lyric : IslandMode.Interlude;
        }
    }

    /// <summary>The active line if it has text.</summary>
    public LyricLine? CurrentLine =>
        LyricsStatus.Lyrics is { } lyrics && LineIndex is { } i && i >= 0 && i < lyrics.Lines.Count && !lyrics.Lines[i].IsBlank
            ? lyrics.Lines[i]
            : null;

    /// <summary>Seconds added to the playback position before looking up the lyric line.</summary>
    public double Offset => Settings.GetOffset(Track?.Id);

    /// <summary>"±0.0s", "+0.5s", "-1.0s"</summary>
    public string OffsetLabel
    {
        get
        {
            var v = Offset;
            if (Math.Abs(v) < 0.05) return "±0.0s";
            return v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "s";
        }
    }

    /// <summary>0…1: how far into line <paramref name="i"/> the singer is (karaoke highlighting).</summary>
    public double LineProgress(int i)
    {
        if (LyricsStatus.Lyrics is not { } lyrics || i < 0 || i >= lyrics.Lines.Count) return 0;
        var line = lyrics.Lines[i];
        var nextTime = i + 1 < lyrics.Lines.Count ? lyrics.Lines[i + 1].Time : Track?.Duration ?? line.Time + 5;
        var gap = Math.Max(0.5, nextTime - line.Time);
        var estimate = Karaoke.EstimateSeconds(line.Text, gap);
        return Math.Min(1, Math.Max(0, (CurrentTime + Offset - line.Time) / estimate));
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    public void Attach(IPlayer player)
    {
        if (_player is { } old)
        {
            old.Stop();
            old.Updated -= Apply;
            old.IssueChanged -= OnIssueChanged;
        }
        _player = player;
        player.Updated += Apply;
        player.IssueChanged += OnIssueChanged;
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(CanSeek));
        Apply(PlayerSnapshot.Stopped);
        player.Start();
    }

    /// <summary>
    /// Call about every 100 ms. <paramref name="cursorOverPill"/>: the cursor is inside the pill's
    /// screen rect (the Mac pads it by 3 px on each side).
    /// </summary>
    public void Tick(bool cursorOverPill)
    {
        UpdateTime();
        TrackPointer(cursorOverPill);
        NotifyMode();
    }

    private void OnIssueChanged(PlayerIssue? issue) => PlayerIssue = issue;

    private void Apply(PlayerSnapshot s)
    {
        var now = _time.GetUtcNow();
        var trackChanged = s.Track?.Id != Track?.Id;
        var threshold = _player?.ResyncThreshold ?? DefaultResyncThreshold;
        var smallDrift = !trackChanged && s.State == _snapshot.State && s.State == PlaybackState.Playing
            && Math.Abs(_snapshot.PositionAt(now) - s.PositionAt(now)) < threshold;
        // Keep the smooth local clock unless the player really jumped.
        if (!smallDrift) _snapshot = s;

        if (trackChanged)
        {
            SetTrack(s.Track);
        }
        else if (s.Track != Track)
        {
            Track = s.Track;
        }
        if (s.State != Playback)
        {
            Playback = s.State;
            _pausedSince = s.State == PlaybackState.Paused ? now : null;
        }
        UpdateTime();
        NotifyMode();
    }

    private void SetTrack(Track? t)
    {
        Track = t;
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsCancellation = null;
        LineIndex = null;
        _notFoundSince = null;
        OnPropertyChanged(nameof(Offset));
        OnPropertyChanged(nameof(OffsetLabel));
        if (t is null)
        {
            LyricsStatus = LyricsStatus.Idle;
            _songChangeUntil = null;
            return;
        }
        _songChangeUntil = _time.GetUtcNow() + SongChangeDuration;
        LyricsStatus = LyricsStatus.Loading;
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        _ = LoadLyricsAsync(t, cancellation.Token);
    }

    private async Task LoadLyricsAsync(Track t, CancellationToken ct)
    {
        LyricsResult result;
        try
        {
            // Resumes on the caller's SynchronizationContext (the UI thread in the app).
            result = await _lyricsProvider.GetLyricsAsync(t, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            result = LyricsResult.Failed;
        }
        if (ct.IsCancellationRequested || Track?.Id != t.Id) return;

        if (result is { Outcome: LyricsOutcome.Found, Lyrics: { } lyrics })
        {
            LyricsStatus = LyricsStatus.Found(lyrics);
        }
        else
        {
            LyricsStatus = LyricsStatus.NotFound;
            _notFoundSince = _time.GetUtcNow();
        }
        UpdateTime();
        NotifyMode();
    }

    private void UpdateTime()
    {
        var t = _snapshot.PositionAt(_time.GetUtcNow());
        if (Track is { Duration: > 0 } track) t = Math.Min(t, track.Duration);
        CurrentTime = t;

        int? index = null;
        if (LyricsStatus.Lyrics is { } lyrics)
        {
            var lyricTime = t + Offset;
            for (var i = lyrics.Lines.Count - 1; i >= 0; i--)
            {
                if (lyrics.Lines[i].Time <= lyricTime)
                {
                    index = i;
                    break;
                }
            }
        }
        LineIndex = index;
    }

    /// <summary>Hover detection by polling the cursor; also decides where the window is click-through.</summary>
    private void TrackPointer(bool cursorOverPill)
    {
        var now = _time.GetUtcNow();
        var inside = SimulatedHover ?? (Mode != IslandMode.Hidden && cursorOverPill);
        AcceptsMouse = inside;

        if (inside)
        {
            _leaveSince = null;
            _hoverSince ??= now;
            if (!IsExpanded && Settings.ExpandOnHover && now - _hoverSince >= HoverExpandDelay) IsExpanded = true;
        }
        else
        {
            _hoverSince = null;
            if (IsExpanded)
            {
                _leaveSince ??= now;
                if (now - _leaveSince >= HoverCollapseDelay)
                {
                    IsExpanded = false;
                    _leaveSince = null;
                }
            }
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────

    /// <summary>Clicking the compact pill expands it right away.</summary>
    public void ExpandNow()
    {
        IsExpanded = true;
        _hoverSince = _time.GetUtcNow();
        NotifyMode();
    }

    public void TogglePlay() => _player?.PlayPause();
    public void Next() => _player?.Next();
    public void Previous() => _player?.Previous();
    public void OpenPlayer() => _player?.Activate();

    /// <summary>Seek to a fraction (0…1) of the track (progress bar click).</summary>
    public void Seek(double fraction)
    {
        if (Track is not { Duration: > 0 } track) return;
        _player?.Seek(track.Duration * Math.Min(1, Math.Max(0, fraction)));
    }

    /// <summary>Jump to the start of line <paramref name="i"/> (click on a lyric line).</summary>
    public void SeekToLine(int i)
    {
        if (LyricsStatus.Lyrics is not { } lyrics || i < 0 || i >= lyrics.Lines.Count) return;
        _player?.Seek(Math.Max(0, lyrics.Lines[i].Time - Offset + 0.05));
    }

    /// <summary>±0.5s steps from the panel or the tray menu, rounded to 0.1s and remembered per song.</summary>
    public void AdjustOffset(double delta)
    {
        if (Track?.Id is not { } id) return;
        Settings.SetOffset(id, Math.Round((Offset + delta) * 10, MidpointRounding.AwayFromZero) / 10);
        UpdateTime();
    }

    public void ResetOffset()
    {
        if (Track?.Id is not { } id) return;
        Settings.SetOffset(id, 0);
        UpdateTime();
    }

    /// <summary>Lyrics picked through manual search, for the track that was playing when the search started.</summary>
    public void SetManualLyrics(Lyrics lyrics, string trackId)
    {
        if (Track?.Id != trackId) return;
        LyricsStatus = LyricsStatus.Found(lyrics);
        _notFoundSince = null;
        UpdateTime();
        NotifyMode();
    }

    /// <summary>Fills the model with the demo song frozen at <paramref name="time"/> (snapshots and screenshots).</summary>
    public void LoadPreview(double time)
    {
        var t = DemoPlayer.Tracks[0];
        Track = t;
        Playback = PlaybackState.Playing;
        LyricsStatus = DemoPlayer.LyricsFor(t.Id) is { } lyrics ? LyricsStatus.Found(lyrics) : LyricsStatus.NotFound;
        _snapshot = new PlayerSnapshot(t, PlaybackState.Paused, time, _time.GetUtcNow());
        UpdateTime();
        NotifyMode();
    }

    // ── Change notification ──────────────────────────────────────────────

    private void NotifyMode()
    {
        var mode = Mode;
        if (mode == _lastMode) return;
        _lastMode = mode;
        OnPropertyChanged(nameof(Mode));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
