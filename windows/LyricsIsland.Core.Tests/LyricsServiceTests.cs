using System.Net;

namespace LyricsIsland.Core.Tests;

public class LyricsServiceTests : IDisposable
{
    // Fictional track; the artist has a guest so the primary-artist rule is exercised.
    private static readonly Track Night = new("night-1", "Night Drive - Remastered 2011", "Test Band, Guest", "Test Album", 201.5);

    private const string SyncedJson = """
        {"id":1,"trackName":"Night Drive","artistName":"Test Band","duration":201.5,"instrumental":false,
         "plainLyrics":"hello","syncedLyrics":"[00:01.00]hello\n[00:02.00]world"}
        """;

    private readonly TempDirectory _cache = new();

    public void Dispose() => _cache.Dispose();

    private LyricsService Service(FakeHttpHandler handler) => new(_cache.Path, handler);

    [Fact]
    public async Task ExactMatchSendsTitleArtistAlbumAndRoundedDuration()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(SyncedJson));
        using var service = Service(handler);

        var result = await service.GetLyricsAsync(Night);

        Assert.Equal(LyricsOutcome.Found, result.Outcome);
        Assert.Equal(["hello", "world"], result.Lyrics!.Lines.Select(l => l.Text));
        Assert.Equal("LRCLIB", result.Lyrics.Source);
        var url = Assert.Single(handler.Urls);
        // 201.5 rounds away from zero, like Swift's .rounded() (not to even).
        Assert.Equal("/api/get?track_name=Night%20Drive%20-%20Remastered%202011&artist_name=Test%20Band%2C%20Guest"
                     + "&album_name=Test%20Album&duration=202", url);
    }

    [Fact]
    public async Task SendsOnlyTheUserAgentWeExpect()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(SyncedJson));
        using var service = Service(handler);

        await service.GetLyricsAsync(Night);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"LyricsIsland/{AppInfo.Version} (Windows)", request.Headers.UserAgent.ToString());
        Assert.Equal("0.2.0", AppInfo.Version);
        Assert.Equal("https://lrclib.net/", request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/");
    }

    [Fact]
    public async Task OmitsAlbumAndDurationWhenUnknown()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(SyncedJson));
        using var service = Service(handler);

        await service.GetLyricsAsync(new Track("t", "C++ Song", "A & B", "", 0));

        // "+" and "&" must be escaped, or LRCLIB would read them as a space / a new parameter.
        Assert.Equal("/api/get?track_name=C%2B%2B%20Song&artist_name=A%20%26%20B", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task FallsBackToSearchWithCleanTitleAndPrimaryArtist()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/get"
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""
                [{"duration":150,"syncedLyrics":"[00:01.00]far away"},
                 {"duration":199,"syncedLyrics":"[00:01.00]close enough"},
                 {"duration":201.5,"syncedLyrics":null,"plainLyrics":"plain only"}]
                """));
        using var service = Service(handler);

        var result = await service.GetLyricsAsync(Night);

        Assert.Equal("close enough", result.Lyrics!.Lines[0].Text);
        Assert.Equal("/api/search?track_name=Night%20Drive&artist_name=Test%20Band", handler.Urls.Last());
    }

    [Fact]
    public async Task SearchTakesFirstSyncedResultWhenNoDurationIsClose()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/get"
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""
                [{"duration":100,"syncedLyrics":""},
                 {"duration":150,"syncedLyrics":"[00:01.00]first synced"},
                 {"duration":300,"syncedLyrics":"[00:01.00]second synced"}]
                """));
        using var service = Service(handler);

        var result = await service.GetLyricsAsync(Night);

        Assert.Equal("first synced", result.Lyrics!.Lines[0].Text);
    }

    [Fact]
    public async Task ExactMatchWithoutSyncedLyricsAlsoFallsBackToSearch()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/get"
            ? FakeHttpHandler.Json("""{"id":2,"plainLyrics":"no timing","syncedLyrics":null}""")
            : FakeHttpHandler.Json("""[{"duration":201,"syncedLyrics":"[00:01.00]from search"}]"""));
        using var service = Service(handler);

        var result = await service.GetLyricsAsync(Night);

        Assert.Equal("from search", result.Lyrics!.Lines[0].Text);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FoundLyricsAreCachedInMemoryAndOnDisk()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(SyncedJson));
        using (var service = Service(handler))
        {
            await service.GetLyricsAsync(Night);
            var again = await service.GetLyricsAsync(Night);
            Assert.Equal(LyricsOutcome.Found, again.Outcome);
            Assert.Single(handler.Requests);
            Assert.True(File.Exists(service.CacheFilePath(Night.Id)));
        }

        // A new instance (next app start) reads the .lrc file instead of asking LRCLIB.
        var offline = new FakeHttpHandler(_ => throw new HttpRequestException("offline"));
        using var restarted = Service(offline);
        var cached = await restarted.GetLyricsAsync(Night);

        Assert.Equal("hello", cached.Lyrics!.Lines[0].Text);
        Assert.Empty(offline.Requests);
    }

    [Fact]
    public async Task NotFoundIsRememberedOnlyInMemory()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/get"
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("[]"));
        using (var service = Service(handler))
        {
            Assert.Equal(LyricsOutcome.NotFound, (await service.GetLyricsAsync(Night)).Outcome);
            Assert.Equal(LyricsOutcome.NotFound, (await service.GetLyricsAsync(Night)).Outcome);
            Assert.Equal(2, handler.Requests.Count); // exact + search, once
            Assert.False(File.Exists(service.CacheFilePath(Night.Id)));
        }

        // After a restart it asks again, in case the lyrics were added to LRCLIB since.
        using var restarted = Service(handler);
        await restarted.GetLyricsAsync(Night);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.OK, "{not json")]
    public async Task ServerErrorsFailWithoutBeingRemembered(HttpStatusCode status, string body)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(body, status));
        using var service = Service(handler);

        Assert.Equal(LyricsOutcome.Failed, (await service.GetLyricsAsync(Night)).Outcome);
        Assert.Equal(LyricsOutcome.Failed, (await service.GetLyricsAsync(Night)).Outcome);
        Assert.Equal(2, handler.Requests.Count); // tried again the second time
    }

    [Fact]
    public async Task NetworkErrorsFail()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("no network"));
        using var service = Service(handler);

        Assert.Equal(LyricsOutcome.Failed, (await service.GetLyricsAsync(Night)).Outcome);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsFailure()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(SyncedJson));
        using var service = Service(handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetLyricsAsync(Night, cancelled.Token));
    }

    [Fact]
    public async Task DemoTracksNeverTouchTheNetwork()
    {
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no requests expected"));
        using var service = Service(handler);

        var demo = await service.GetLyricsAsync(DemoPlayer.Tracks[0]);
        var noLyrics = await service.GetLyricsAsync(DemoPlayer.Tracks[1]);

        Assert.Equal(LyricsOutcome.Found, demo.Outcome);
        Assert.Equal("Demo", demo.Lyrics!.Source);
        Assert.Equal(LyricsOutcome.NotFound, noLyrics.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ManualSearchUsesFreeTextAndTopSyncedResult()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            [{"syncedLyrics":null},{"syncedLyrics":"[00:01.00]manual pick"},{"syncedLyrics":"[00:01.00]second"}]
            """));
        using var service = Service(handler);

        var raw = await service.SearchAsync("night drive test band");

        Assert.Equal("[00:01.00]manual pick", raw);
        Assert.Equal("/api/search?q=night%20drive%20test%20band", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task ManualSearchThrowsOnServerErrors()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.BadGateway));
        using var service = Service(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("anything"));
    }

    [Fact]
    public async Task StoredManualLyricsReplaceNotFound()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/get"
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("[]"));
        using var service = Service(handler);
        await service.GetLyricsAsync(Night);

        var stored = service.StoreManual("[00:01.00]picked by hand", Night);
        var result = await service.GetLyricsAsync(Night);

        Assert.Equal("picked by hand", stored.Lines[0].Text);
        Assert.Equal(LyricsOutcome.Found, result.Outcome);
        Assert.Equal(2, handler.Requests.Count); // no new requests after storing
        Assert.True(File.Exists(service.CacheFilePath(Night.Id)));
    }

    [Fact]
    public void CacheFilesDontCollideForNonLatinTitles()
    {
        using var service = new LyricsService(_cache.Path, new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.NotFound)));
        var a = TrackKey.Create("夜に駆ける", "アーティスト", "", 261);
        var b = TrackKey.Create("春の歌", "アーティスト", "", 261);

        var fileA = service.CacheFilePath(a);
        var fileB = service.CacheFilePath(b);

        Assert.NotEqual(fileA, fileB);
        Assert.Equal(_cache.Path, Path.GetDirectoryName(fileA));
        Assert.EndsWith(".lrc", fileA);
        Assert.Matches("^[A-Za-z0-9_-]+-[0-9a-f]{16}\\.lrc$", Path.GetFileName(fileA));
        Assert.True(Path.GetFileName(service.CacheFilePath(new string('x', 500))).Length <= 81);
    }

    [Theory]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song (Feat Someone)", "Song")]
    [InlineData("Song [with Someone]", "Song")]
    [InlineData("Song (ft. A & B)", "Song")]
    [InlineData("Song (feat. A) - 2011 Mix", "Song")]
    [InlineData("Song (Live)", "Song (Live)")]
    [InlineData("Song-Title", "Song-Title")]
    [InlineData("Without You", "Without You")]
    [InlineData("  Song  ", "Song")]
    public void CleanTitle(string title, string expected)
    {
        Assert.Equal(expected, LyricsService.CleanTitle(title));
    }

    [Theory]
    [InlineData("A, B", "A")]
    [InlineData("A", "A")]
    [InlineData(" A ,B", "A")]
    [InlineData(",B", "B")]
    [InlineData("", "")]
    public void PrimaryArtist(string artist, string expected)
    {
        Assert.Equal(expected, LyricsService.PrimaryArtist(artist));
    }
}
