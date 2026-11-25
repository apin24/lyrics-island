using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricsIsland.Core;

public enum LyricsOutcome
{
    Found,
    NotFound,
    /// <summary>Network or server error. Not remembered, so the next request tries again.</summary>
    Failed,
}

public sealed record LyricsResult(LyricsOutcome Outcome, Lyrics? Lyrics = null)
{
    public static LyricsResult NotFound { get; } = new(LyricsOutcome.NotFound);
    public static LyricsResult Failed { get; } = new(LyricsOutcome.Failed);
    public static LyricsResult Found(Lyrics lyrics) => new(LyricsOutcome.Found, lyrics);
}

public interface ILyricsProvider
{
    /// <summary>Never throws for network errors (returns Failed); throws only when <paramref name="cancellationToken"/> is cancelled.</summary>
    Task<LyricsResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches time-synced lyrics from LRCLIB (https://lrclib.net) and caches them. Port of the Mac app's
/// <c>LyricsService.swift</c>: exact match first, then a search; found lyrics are cached as <c>.lrc</c> files,
/// "not found" only in memory so lyrics added to LRCLIB later still show up.
/// Only the title, artist, album and duration are ever sent.
/// </summary>
public sealed class LyricsService : ILyricsProvider, IDisposable
{
    public const string BaseUrl = "https://lrclib.net";
    public const string SourceName = "LRCLIB";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex VersionSuffix = new(@"\s+-\s+.*$", RegexOptions.CultureInvariant);
    private static readonly Regex Featuring = new(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnsafeFileChars = new("[^A-Za-z0-9_-]", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<string, Lyrics> _memory = new();
    private readonly HashSet<string> _missing = [];

    public string CacheDirectory { get; }

    /// <param name="cacheDirectory">Where <c>.lrc</c> files go, normally <see cref="AppPaths.CacheDirectory"/>.</param>
    /// <param name="handler">For tests; a real handler is created when null.</param>
    public LyricsService(string cacheDirectory, HttpMessageHandler? handler = null)
    {
        CacheDirectory = cacheDirectory;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No cache then; lyrics still work from memory.
        }

        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(12),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"LyricsIsland/{AppInfo.Version} (Windows)");
    }

    public async Task<LyricsResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (DemoPlayer.LyricsFor(track.Id) is { } demo) return LyricsResult.Found(demo);
        if (track.Id.StartsWith("demo:", StringComparison.Ordinal)) return LyricsResult.NotFound;
        lock (_gate)
        {
            if (_memory.TryGetValue(track.Id, out var cached)) return LyricsResult.Found(cached);
            if (_missing.Contains(track.Id)) return LyricsResult.NotFound;
        }

        var file = CacheFilePath(track.Id);
        try
        {
            if (File.Exists(file))
            {
                var lines = LrcParser.Parse(await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken));
                if (lines.Count > 0) return LyricsResult.Found(Remember(track.Id, new Lyrics(lines, SourceName)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable cache file: fetch again.
        }

        try
        {
            var raw = await FetchExactAsync(track, cancellationToken) ?? await FetchSearchAsync(track, cancellationToken);
            if (raw is not null)
            {
                var lines = LrcParser.Parse(raw);
                if (lines.Count > 0)
                {
                    WriteCache(file, raw);
                    return LyricsResult.Found(Remember(track.Id, new Lyrics(lines, SourceName)));
                }
            }
            lock (_gate) _missing.Add(track.Id);
            return LyricsResult.NotFound;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Network error, timeout, server error or bad JSON.
            return LyricsResult.Failed;
        }
    }

    /// <summary>Free-text search (manual search). Returns the top result that has synced lyrics, or null. Throws on network errors.</summary>
    public async Task<string?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var records = await GetAsync<LrclibRecord[]>("/api/search", [("q", query)], cancellationToken);
        return records?.FirstOrDefault(r => !string.IsNullOrEmpty(r.SyncedLyrics))?.SyncedLyrics;
    }

    /// <summary>Uses <paramref name="raw"/> (from a manual search) as the lyrics for <paramref name="track"/> from now on.</summary>
    public Lyrics StoreManual(string raw, Track track)
    {
        WriteCache(CacheFilePath(track.Id), raw);
        var lyrics = new Lyrics(LrcParser.Parse(raw), SourceName);
        lock (_gate)
        {
            _memory[track.Id] = lyrics;
            _missing.Remove(track.Id);
        }
        return lyrics;
    }

    /// <summary>
    /// Cache file for a track. Track IDs on Windows contain the title, so the name is a readable
    /// ASCII prefix plus a hash: non-Latin titles would otherwise all collapse to underscores.
    /// </summary>
    public string CacheFilePath(string trackId)
    {
        var safe = UnsafeFileChars.Replace(trackId, "_");
        if (safe.Length > 60) safe = safe[..60];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId)))[..16].ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{safe}-{hash}.lrc");
    }

    /// <summary>"Song - Remastered 2011" / "Song (feat. X)" → "Song"</summary>
    public static string CleanTitle(string name)
    {
        var s = VersionSuffix.Replace(name, "");
        s = Featuring.Replace(s, "");
        return Text.TrimSpaces(s);
    }

    /// <summary>"A, B" → "A"</summary>
    public static string PrimaryArtist(string artist) =>
        Text.TrimSpaces(artist.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? artist);

    public void Dispose() => _http.Dispose();

    // ── LRCLIB ───────────────────────────────────────────────────────────

    private async Task<string?> FetchExactAsync(Track t, CancellationToken ct)
    {
        var query = new List<(string, string)> { ("track_name", t.Title), ("artist_name", t.Artist) };
        if (t.Album.Length > 0) query.Add(("album_name", t.Album));
        if (t.Duration > 0) query.Add(("duration", Text.RoundSeconds(t.Duration).ToString(CultureInfo.InvariantCulture)));
        var record = await GetAsync<LrclibRecord>("/api/get", query, ct);
        return string.IsNullOrEmpty(record?.SyncedLyrics) ? null : record.SyncedLyrics;
    }

    private async Task<string?> FetchSearchAsync(Track t, CancellationToken ct)
    {
        var records = await GetAsync<LrclibRecord[]>("/api/search",
            [("track_name", CleanTitle(t.Title)), ("artist_name", PrimaryArtist(t.Artist))], ct);
        if (records is null) return null;
        var synced = records.Where(r => !string.IsNullOrEmpty(r.SyncedLyrics)).ToList();
        var closest = synced.FirstOrDefault(r => t.Duration > 0 && Math.Abs((r.Duration ?? 0) - t.Duration) <= 4);
        return (closest ?? synced.FirstOrDefault())?.SyncedLyrics;
    }

    /// <summary>GET with query parameters. 404 → null; other non-2xx → throws.</summary>
    private async Task<T?> GetAsync<T>(string path, IEnumerable<(string Name, string Value)> query, CancellationToken ct)
        where T : class
    {
        var url = path + "?" + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LRCLIB returned {(int)response.StatusCode}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private Lyrics Remember(string trackId, Lyrics lyrics)
    {
        lock (_gate) _memory[trackId] = lyrics;
        return lyrics;
    }

    private static void WriteCache(string file, string raw)
    {
        try
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, raw, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Caching is best effort.
        }
    }

    private sealed record LrclibRecord(
        long? Id,
        string? TrackName,
        string? ArtistName,
        double? Duration,
        bool? Instrumental,
        string? SyncedLyrics);
}
