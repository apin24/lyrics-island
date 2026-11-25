using System.Globalization;

namespace LyricsIsland.Core;

/// <summary>A song as the player reports it. <see cref="Id"/> keys the lyrics cache and the per-song offset.</summary>
/// <param name="Duration">Seconds; 0 if unknown.</param>
public sealed record Track(string Id, string Title, string Artist, string Album, double Duration);

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>Player position at a moment in time. Between reports the position is extrapolated locally.</summary>
public sealed record PlayerSnapshot(Track? Track, PlaybackState State, double Position, DateTimeOffset Timestamp)
{
    public static PlayerSnapshot Stopped { get; } = new(null, PlaybackState.Stopped, 0, DateTimeOffset.MinValue);

    public double PositionAt(DateTimeOffset now) =>
        State == PlaybackState.Playing ? Position + (now - Timestamp).TotalSeconds : Position;
}

public enum PlayerIssue
{
    /// <summary>No supported player (Spotify, or another app that uses the media controls) is running.</summary>
    NoSupportedPlayer,
}

public sealed record LyricLine(int Id, double Time, string Text)
{
    public bool IsBlank => Text.AsSpan().Trim().IsEmpty;
}

public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, string Source);

public enum IslandMode
{
    Hidden,
    SongChange,
    Loading,
    Lyric,
    Interlude,
    NoLyrics,
    Mini,
    Paused,
    Expanded,
}

public enum LyricsState
{
    Idle,
    Loading,
    Found,
    NotFound,
}

/// <summary>Where the lyrics for the current track stand. <see cref="Lyrics"/> is set only when <see cref="State"/> is Found.</summary>
public sealed record LyricsStatus(LyricsState State, Lyrics? Lyrics = null)
{
    public static LyricsStatus Idle { get; } = new(LyricsState.Idle);
    public static LyricsStatus Loading { get; } = new(LyricsState.Loading);
    public static LyricsStatus NotFound { get; } = new(LyricsState.NotFound);
    public static LyricsStatus Found(Lyrics lyrics) => new(LyricsState.Found, lyrics);
}

/// <summary>
/// The Windows media controls have no track ID, so a song is identified by its metadata:
/// <c>title|artist|album|round(duration)</c>, trimmed and lower-cased.
/// </summary>
public static class TrackKey
{
    public static string Create(string title, string artist, string album, double durationSeconds) => string.Join('|',
        Normalize(title),
        Normalize(artist),
        Normalize(album),
        Text.RoundSeconds(durationSeconds).ToString(CultureInfo.InvariantCulture));

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

internal static class Text
{
    /// <summary>Swift's <c>trimmingCharacters(in: .whitespaces)</c>: tabs and Unicode space separators, not newlines.</summary>
    public static string TrimSpaces(string value) => TrimSpaces(value.AsSpan()).ToString();

    public static ReadOnlySpan<char> TrimSpaces(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && IsSpace(value[start])) start++;
        var end = value.Length;
        while (end > start && IsSpace(value[end - 1])) end--;
        return value[start..end];
    }

    private static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Swift's <c>.rounded()</c> rounds halves away from zero; .NET's default is to even.</summary>
    public static long RoundSeconds(double seconds) => (long)Math.Round(seconds, MidpointRounding.AwayFromZero);

    /// <summary>Swift's <c>String.count</c>: user-perceived characters (grapheme clusters), not UTF-16 units.</summary>
    public static int CharacterCount(string value) => new StringInfo(value).LengthInTextElements;
}
