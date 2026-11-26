using System.Globalization;

namespace LyricsIsland.Core;

/// <summary>
/// Parses LRC text (<c>[mm:ss.xx] line</c>) into time-sorted lines. Port of the Mac app's <c>LRCParser.swift</c>:
/// <list type="bullet">
/// <item>A line can have several time stamps; each becomes its own entry.</item>
/// <item>Metadata tags like <c>[ar:…]</c> are ignored. <c>[offset:+250]</c> shifts every line 0.25s earlier.</item>
/// <item>Entries are stable-sorted by time. Blank lines are kept: they mark an interlude.</item>
/// </list>
/// </summary>
public static class LrcParser
{
    // Swift's CharacterSet.newlines: LF, CR, VT, FF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR.
    private static readonly char[] Newlines = [(char)0x0A, (char)0x0D, (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    public static IReadOnlyList<LyricLine> Parse(string raw)
    {
        var offset = 0.0;
        var entries = new List<(double Time, int Order, string Text)>();
        var order = 0;

        foreach (var rawLine in raw.Split(Newlines))
        {
            var rest = Text.TrimSpaces(rawLine.AsSpan());
            var times = new List<double>();
            int close;
            while (rest.StartsWith("[") && (close = rest.IndexOf(']')) >= 0)
            {
                var tag = rest[1..close];
                rest = rest[(close + 1)..];
                if (ParseTime(tag) is { } t)
                {
                    times.Add(t);
                }
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                         && ParseDouble(Text.TrimSpaces(tag[7..])) is { } ms)
                {
                    offset = ms / 1000;
                }
            }

            var text = Text.TrimSpaces(rest).ToString();
            foreach (var t in times) entries.Add((t, order++, text));
        }

        // List.Sort isn't stable, so break ties on the original order explicitly.
        entries.Sort((a, b) => a.Time == b.Time ? a.Order.CompareTo(b.Order) : a.Time.CompareTo(b.Time));
        var lines = new LyricLine[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            lines[i] = new LyricLine(i, Math.Max(0, e.Time - offset), e.Text);
        }
        return lines;
    }

    /// <summary><c>01:23.45</c>, <c>1:23</c>, <c>01:23:45</c> → seconds. Metadata tags like <c>ar:Name</c> return null.</summary>
    public static double? ParseTime(ReadOnlySpan<char> tag)
    {
        var parts = tag.ToString().Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes))
            return null;
        var secText = parts.Length == 3 ? $"{parts[1]}.{parts[2]}" : parts[1];
        if (ParseDouble(secText.Replace(',', '.')) is not { } seconds) return null;
        return minutes * 60.0 + seconds;
    }

    private static double? ParseDouble(ReadOnlySpan<char> text)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(text, style, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }
}
