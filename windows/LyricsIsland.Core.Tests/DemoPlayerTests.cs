using Microsoft.Extensions.Time.Testing;

namespace LyricsIsland.Core.Tests;

public class DemoPlayerTests : IDisposable
{
    private readonly InlineSynchronizationContext _context = InlineSynchronizationContext.Install();
    private readonly FakeTimeProvider _time = new();
    private readonly List<PlayerSnapshot> _snapshots = [];

    public void Dispose() => _context.Dispose();

    private DemoPlayer Start(double startAt = 0)
    {
        var player = new DemoPlayer(startAt, _time);
        player.Updated += _snapshots.Add;
        player.Start();
        return player;
    }

    private PlayerSnapshot Last => _snapshots[^1];

    private void Advance(double seconds) => _time.Advance(TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)));

    [Fact]
    public void DemoContentIsTheFictionalSong()
    {
        Assert.Equal(["Streetlight Hymn", "The Last Carriage"], DemoPlayer.Tracks.Select(t => t.Title));
        Assert.All(DemoPlayer.Tracks, t => Assert.Equal("Dusk Collective", t.Artist));
        Assert.Equal([78.0, 18.0], DemoPlayer.Tracks.Select(t => t.Duration));
        Assert.NotNull(DemoPlayer.LyricsFor("demo:streetlight-hymn"));
        Assert.Null(DemoPlayer.LyricsFor("demo:the-last-carriage")); // no lyrics on purpose
    }

    [Fact]
    public void StartsPlayingAndReportsTwiceASecond()
    {
        using var player = Start();
        Assert.Equal(PlaybackState.Playing, Last.State);
        Assert.Equal(DemoPlayer.Tracks[0], Last.Track);
        Assert.Equal(0, Last.Position);

        Advance(10);

        Assert.Equal(21, _snapshots.Count);
        Assert.Equal(10, Last.Position, 6);
    }

    [Fact]
    public void MovesToTheNextTrackAtTheEndAndWrapsAround()
    {
        using var player = Start(startAt: 77);

        Advance(1);
        Assert.Equal(DemoPlayer.Tracks[1], Last.Track);
        Assert.Equal(0, Last.Position, 6);

        Advance(18);
        Assert.Equal(DemoPlayer.Tracks[0], Last.Track);
    }

    [Fact]
    public void PlayPauseFreezesThePosition()
    {
        using var player = Start();
        Advance(5);

        player.PlayPause();
        Advance(5);
        Assert.Equal(PlaybackState.Paused, Last.State);
        Assert.Equal(5, Last.Position, 6);

        player.PlayPause();
        Advance(1);
        Assert.Equal(PlaybackState.Playing, Last.State);
        Assert.Equal(6, Last.Position, 6);
    }

    [Fact]
    public void PreviousRestartsTheSongAfterThreeSeconds()
    {
        using var player = Start();
        Advance(4);

        player.Previous();
        Assert.Equal(DemoPlayer.Tracks[0], Last.Track);
        Assert.Equal(0, Last.Position, 6);

        player.Previous(); // within 3s: previous track (wraps to the last one)
        Assert.Equal(DemoPlayer.Tracks[1], Last.Track);
    }

    [Fact]
    public void SeekAndNext()
    {
        using var player = Start();

        player.Seek(-5);
        Assert.Equal(0, Last.Position);
        player.Seek(42);
        Assert.Equal(42, Last.Position);
        player.Next();
        Assert.Equal(DemoPlayer.Tracks[1], Last.Track);
    }

    [Fact]
    public void StopEndsTheUpdates()
    {
        var player = Start();
        player.Stop();
        var count = _snapshots.Count;

        Advance(5);

        Assert.Equal(count, _snapshots.Count);
    }
}
