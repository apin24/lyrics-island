namespace LyricsIsland.Core.Tests;

public class KaraokeTests
{
    [Theory]
    [InlineData(4, 0.0, 0)]
    [InlineData(4, 0.01, 1)] // ceil: the first word lights up as soon as the line starts
    [InlineData(4, 0.25, 1)]
    [InlineData(4, 0.26, 2)]
    [InlineData(3, 0.5, 2)]
    [InlineData(4, 1.0, 4)]
    [InlineData(0, 0.7, 0)]
    public void LitWordsIsCeilOfWordsTimesProgress(int words, double progress, int lit)
    {
        Assert.Equal(lit, Karaoke.LitWords(words, progress));
    }

    [Fact]
    public void WordsSplitOnSpacesAndDropEmptyPieces()
    {
        Assert.Equal(["you", "laughed,", "and", "so", "did", "I"], Karaoke.Words("you laughed,  and so did I "));
        Assert.Empty(Karaoke.Words("   "));
    }

    [Theory]
    [InlineData("hi", 10, 1.2)]                      // short line: at least 1.2s
    [InlineData("the road back home is long tonight", 10, 2.89)] // 34 characters × 0.085
    [InlineData("the road back home is long tonight", 2, 1.8)]   // but at most 90% of the gap
    public void EstimateIsMinOfGapAndCharacterTime(string text, double gap, double expected)
    {
        Assert.Equal(expected, Karaoke.EstimateSeconds(text, gap), 6);
    }

    [Fact]
    public void EstimateCountsCharactersLikeSwift()
    {
        // 20 user-perceived characters (Swift's String.count), but 50 UTF-16 units.
        var emoji = string.Concat(Enumerable.Repeat("👍🏽", 10)) + "          ";
        Assert.Equal(20, Text.CharacterCount(emoji));
        Assert.Equal(1.7, Karaoke.EstimateSeconds(emoji, 10), 6);
    }
}

public class TrackKeyTests
{
    [Fact]
    public void KeyIsNormalizedMetadataWithRoundedDuration()
    {
        Assert.Equal("night drive|test band|test album|179", TrackKey.Create("  Night Drive ", "Test Band", "Test Album", 178.5));
        Assert.Equal("night drive|test band|test album|178", TrackKey.Create("Night Drive", "Test Band", "Test Album", 178.4));
        Assert.Equal("a|b||0", TrackKey.Create("A", "B", "", 0));
    }

    [Fact]
    public void TinyDurationDifferencesGiveTheSameKey()
    {
        // The media controls reported 2:58.762 and 2:58.763 for the same song.
        Assert.Equal(TrackKey.Create("x", "y", "z", 178.762), TrackKey.Create("x", "y", "z", 178.763));
    }
}
