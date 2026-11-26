namespace LyricsIsland.Core;

/// <summary>Word-by-word highlighting of the active line (port of <c>Karaoke</c> in <c>Components.swift</c>).</summary>
public static class Karaoke
{
    /// <summary>The words of a line, split on spaces (empty pieces dropped).</summary>
    public static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>How many of <paramref name="wordCount"/> words are lit at <paramref name="progress"/> (0…1): <c>ceil(words × progress)</c>.</summary>
    public static int LitWords(int wordCount, double progress) => (int)Math.Ceiling(wordCount * progress);

    /// <summary>
    /// Seconds the singer is assumed to need for a line: <c>min(gap × 0.9, max(1.2, characters × 0.085))</c>,
    /// where <paramref name="gap"/> is the time until the next line.
    /// </summary>
    public static double EstimateSeconds(string text, double gap) =>
        Math.Min(gap * 0.9, Math.Max(1.2, Text.CharacterCount(text) * 0.085));
}
