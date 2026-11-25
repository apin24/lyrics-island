namespace LyricsIsland.Core;

/// <summary>
/// Plays a fictional song so the island can be tried and tested without Spotify (<c>--demo</c>).
/// Port of <c>DemoPlayer</c> in the Mac app's <c>Players.swift</c>.
/// Updates are posted to the <see cref="SynchronizationContext"/> that called <see cref="Start"/>.
/// </summary>
public sealed class DemoPlayer : IPlayer, IDisposable
{
    public static IReadOnlyList<Track> Tracks { get; } =
    [
        new("demo:streetlight-hymn", "Streetlight Hymn", "Dusk Collective", "Late Hours", 78),
        new("demo:the-last-carriage", "The Last Carriage", "Dusk Collective", "Late Hours", 18),
    ];

    // Fictional song written for the demo. The second demo track has no lyrics on purpose.
    private const string StreetlightHymn = """
        [00:05.50]the road back home is long tonight
        [00:10.00]the city lights are fading slow
        [00:14.50]you said that everything's alright
        [00:19.50]but the night has never been this quiet
        [00:24.00]let this song keep you company
        [00:28.50]until the morning finds us here
        [00:33.00]
        [00:41.00]we stopped beneath the traffic light
        [00:45.50]counting dreams we couldn't buy
        [00:50.00]you laughed, and so did I
        [00:54.50]though tomorrow's still a haze
        [00:59.00]streetlights, please don't fade away
        [01:03.50]there's one more song to make you stay
        [01:08.00]
        """;

    public static Lyrics? LyricsFor(string? trackId) =>
        trackId == Tracks[0].Id ? new Lyrics(LrcParser.Parse(StreetlightHymn), "Demo") : null;

    private readonly TimeProvider _time;
    private int _index;
    private bool _playing = true;
    private double _base;
    private DateTimeOffset _baseDate;
    private ITimer? _timer;
    private SynchronizationContext? _context;

    public event Action<PlayerSnapshot>? Updated;
    public event Action<PlayerIssue?>? IssueChanged;

    public string Name => "Demo";

    public DemoPlayer(double startAt = 0, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _base = startAt;
        _baseDate = _time.GetUtcNow();
    }

    private double Position => _base + (_playing ? (_time.GetUtcNow() - _baseDate).TotalSeconds : 0);

    public void Start()
    {
        _baseDate = _time.GetUtcNow();
        _context = SynchronizationContext.Current;
        IssueChanged?.Invoke(null);
        _timer = _time.CreateTimer(_ => Post(Emit), null, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5));
        Emit();
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void PlayPause()
    {
        _base = Position;
        _baseDate = _time.GetUtcNow();
        _playing = !_playing;
        Emit();
    }

    public void Next() => Advance(1);

    public void Previous()
    {
        if (Position > 3) Seek(0);
        else Advance(-1);
    }

    public void Seek(double seconds)
    {
        _base = Math.Max(0, seconds);
        _baseDate = _time.GetUtcNow();
        Emit();
    }

    public void Activate()
    {
    }

    public void Dispose() => Stop();

    private void Emit()
    {
        if (Position >= Tracks[_index].Duration)
        {
            Advance(1);
            return;
        }
        Updated?.Invoke(new PlayerSnapshot(
            Tracks[_index], _playing ? PlaybackState.Playing : PlaybackState.Paused, Position, _time.GetUtcNow()));
    }

    private void Advance(int delta)
    {
        _index = (_index + delta + Tracks.Count) % Tracks.Count;
        _base = 0;
        _baseDate = _time.GetUtcNow();
        _playing = true;
        Emit();
    }

    private void Post(Action action)
    {
        if (_context is { } context) context.Post(_ => action(), null);
        else action();
    }
}
