namespace LyricsIsland.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParsesTimesAndText()
    {
        var lines = LrcParser.Parse("[00:01.00]one\n[00:02.50]two");

        Assert.Collection(lines,
            l => Assert.Equal(new LyricLine(0, 1.0, "one"), l),
            l => Assert.Equal(new LyricLine(1, 2.5, "two"), l));
    }

    [Fact]
    public void LineWithSeveralStampsBecomesSeveralEntries()
    {
        var lines = LrcParser.Parse("[00:10.00][00:20.00]chorus\n[00:15.00]verse");

        Assert.Equal([10.0, 15.0, 20.0], lines.Select(l => l.Time));
        Assert.Equal(["chorus", "verse", "chorus"], lines.Select(l => l.Text));
        Assert.Equal([0, 1, 2], lines.Select(l => l.Id));
    }

    [Fact]
    public void IgnoresMetadataTags()
    {
        var lines = LrcParser.Parse("[ar:Someone]\n[ti:A Title]\n[length:03:20]\n[00:01.00]line");

        var line = Assert.Single(lines);
        Assert.Equal("line", line.Text);
    }

    [Theory]
    [InlineData("[offset:+250]", 0.75)]
    [InlineData("[offset:250]", 0.75)]
    [InlineData("[offset:-500]", 1.5)]
    [InlineData("[OFFSET: 100 ]", 0.9)]
    [InlineData("[offset:2000]", 0.0)] // clamped at 0
    [InlineData("[offset:abc]", 1.0)]
    public void OffsetShiftsEveryLine(string tag, double expected)
    {
        var line = Assert.Single(LrcParser.Parse($"{tag}\n[00:01.00]a"));
        Assert.Equal(expected, line.Time, 6);
    }

    [Fact]
    public void OffsetAppliesEvenWhenItComesLast()
    {
        var line = Assert.Single(LrcParser.Parse("[00:01.00]a\n[offset:500]"));
        Assert.Equal(0.5, line.Time, 6);
    }

    [Fact]
    public void SortIsStableForEqualTimes()
    {
        var lines = LrcParser.Parse("[00:05.00]b\n[00:05.00]c\n[00:01.00]a\n[00:05.00]d");

        Assert.Equal(["a", "b", "c", "d"], lines.Select(l => l.Text));
    }

    [Fact]
    public void BlankLinesAreKeptAsInterludes()
    {
        var lines = LrcParser.Parse("[00:01.00]a\n[00:02.00]\n[00:03.00]   \t");

        Assert.Equal(3, lines.Count);
        Assert.False(lines[0].IsBlank);
        Assert.True(lines[1].IsBlank);
        Assert.True(lines[2].IsBlank);
        Assert.Equal("", lines[2].Text);
    }

    [Fact]
    public void TrimsTextButKeepsLaterBrackets()
    {
        var line = Assert.Single(LrcParser.Parse("  [00:01.00]  hello [world]  "));
        Assert.Equal("hello [world]", line.Text);
    }

    [Theory]
    [InlineData("plain text without a stamp")]
    [InlineData("[00:01.00 unclosed")]
    [InlineData("")]
    [InlineData("[ar:only metadata]")]
    public void LinesWithoutTimeStampsAreIgnored(string raw)
    {
        Assert.Empty(LrcParser.Parse(raw));
    }

    [Fact]
    public void HandlesWindowsAndOldMacLineEndings()
    {
        var lines = LrcParser.Parse("[00:01.00]a\r\n[00:02.00]b\r[00:03.00]c");

        Assert.Equal(["a", "b", "c"], lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData("1:23", 83.0)]
    [InlineData("01:23.45", 83.45)]
    [InlineData("01:23:45", 83.45)]
    [InlineData("01:23,45", 83.45)]
    [InlineData("00:05.5", 5.5)]
    [InlineData("10:00.00", 600.0)]
    public void ParsesTimeFormats(string tag, double expected)
    {
        Assert.Equal(expected, LrcParser.ParseTime(tag)!.Value, 6);
    }

    [Theory]
    [InlineData("ar:Name")]
    [InlineData("00:xx")]
    [InlineData("abc")]
    [InlineData("00")]
    [InlineData(":12.00")]
    [InlineData(" 00:12.00")]
    public void RejectsNonTimeTags(string tag)
    {
        Assert.Null(LrcParser.ParseTime(tag));
    }

    [Fact]
    public void ParsesTheDemoSong()
    {
        var lines = DemoPlayer.LyricsFor(DemoPlayer.Tracks[0].Id)!.Lines;

        Assert.Equal(14, lines.Count);
        Assert.Equal(5.5, lines[0].Time);
        Assert.Equal("the road back home is long tonight", lines[0].Text);
        Assert.True(lines[6].IsBlank);
        Assert.Equal(33.0, lines[6].Time);
        Assert.True(lines[^1].IsBlank);
        Assert.Equal(68.0, lines[^1].Time);
    }
}
