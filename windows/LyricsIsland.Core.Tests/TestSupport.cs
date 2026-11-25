using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace LyricsIsland.Core.Tests;

/// <summary>
/// Runs posted callbacks immediately on the posting thread, so async continuations in the model
/// (and the demo player's timer callbacks) happen synchronously inside the test.
/// </summary>
internal sealed class InlineSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly SynchronizationContext? _previous;

    private InlineSynchronizationContext()
    {
        _previous = Current;
    }

    public static InlineSynchronizationContext Install()
    {
        var context = new InlineSynchronizationContext();
        SetSynchronizationContext(context);
        return context;
    }

    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public void Dispose() => SetSynchronizationContext(_previous);
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LyricsIslandTests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Answers HTTP requests from a function and records them.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Path and query of every request, e.g. "/api/get?track_name=…".</summary>
    public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.PathAndQuery);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };
}

/// <summary>Lyrics that arrive when the test says so.</summary>
internal sealed class FakeLyricsProvider : ILyricsProvider
{
    private readonly Dictionary<string, TaskCompletionSource<LyricsResult>> _pending = new();

    /// <summary>When set, every request completes immediately with this result.</summary>
    public LyricsResult? Immediate { get; set; }

    public List<string> Requested { get; } = [];

    public Task<LyricsResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        Requested.Add(track.Id);
        if (Immediate is { } result) return Task.FromResult(result);
        var tcs = new TaskCompletionSource<LyricsResult>();
        _pending[track.Id] = tcs;
        return tcs.Task;
    }

    public void Complete(string trackId, LyricsResult result) => _pending[trackId].SetResult(result);
}

internal sealed class FakePlayer(TimeProvider time) : IPlayer
{
    public event Action<PlayerSnapshot>? Updated;
    public event Action<PlayerIssue?>? IssueChanged;

    public string Name => "Fake";
    public bool CanSeek { get; set; } = true;
    public double ResyncThreshold { get; set; } = IslandModel.DefaultResyncThreshold;
    public bool Started { get; private set; }
    public List<string> Commands { get; } = [];
    public List<double> Seeks { get; } = [];

    public void Emit(Track? track, PlaybackState state, double position) =>
        Updated?.Invoke(new PlayerSnapshot(track, state, position, time.GetUtcNow()));

    public void RaiseIssue(PlayerIssue? issue) => IssueChanged?.Invoke(issue);

    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void PlayPause() => Commands.Add("playpause");
    public void Next() => Commands.Add("next");
    public void Previous() => Commands.Add("previous");
    public void Seek(double seconds) => Seeks.Add(seconds);
    public void Activate() => Commands.Add("activate");
}

/// <summary>A model wired to a fake clock, a fake player and fake lyrics.</summary>
internal sealed class ModelHarness : IDisposable
{
    /// <summary>Fictional lyrics: an intro until 3s, a blank line (interlude) at 9s.</summary>
    public const string Lrc = """
        [00:03.00]first line here
        [00:06.00]second line
        [00:09.00]
        [00:13.00]after the break
        """;

    public static Track Song { get; } = new("song-1", "Fictional Song", "Test Band", "Test Album", 20);
    public static Track OtherSong { get; } = new("song-2", "Another Song", "Test Band", "Test Album", 30);
    public static Lyrics SongLyrics { get; } = new(LrcParser.Parse(Lrc), "Test");

    private readonly InlineSynchronizationContext _context = InlineSynchronizationContext.Install();

    public FakeTimeProvider Time { get; } = new();
    public FakeLyricsProvider Lyrics { get; } = new();
    public IslandSettings Settings { get; } = new();
    public FakePlayer Player { get; }
    public IslandModel Model { get; }

    public ModelHarness(LyricsResult? immediateLyrics = null, double resyncThreshold = IslandModel.DefaultResyncThreshold)
    {
        Lyrics.Immediate = immediateLyrics;
        Player = new FakePlayer(Time) { ResyncThreshold = resyncThreshold };
        Model = new IslandModel(Settings, Lyrics, Time);
        Model.Attach(Player);
    }

    public static LyricsResult Found => LyricsResult.Found(SongLyrics);

    public void Play(double position, Track? track = null) => Player.Emit(track ?? Song, PlaybackState.Playing, position);

    public void Pause(double position, Track? track = null) => Player.Emit(track ?? Song, PlaybackState.Paused, position);

    /// <summary>Lets <paramref name="seconds"/> pass in 100 ms ticks, like the app's timer.</summary>
    public void Run(double seconds, bool cursorOverPill = false)
    {
        var steps = (int)Math.Round(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(100));
            Model.Tick(cursorOverPill);
        }
    }

    /// <summary>Advances the clock by exactly <paramref name="seconds"/> and ticks once.</summary>
    public void Step(double seconds, bool cursorOverPill = false)
    {
        // Rounded to whole ticks: TimeSpan.FromSeconds(0.29) truncates to 2_899_999 ticks.
        Time.Advance(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)));
        Model.Tick(cursorOverPill);
    }

    public void Dispose() => _context.Dispose();
}
