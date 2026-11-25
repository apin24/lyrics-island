using System.Globalization;

namespace LyricsIsland.Windows;

/// <summary>Command-line options. Unknown arguments are ignored so a stray flag never stops the app.</summary>
public sealed record AppOptions
{
    /// <summary>Use the demo player (milestone 3 always does; Spotify comes later).</summary>
    public bool Demo { get; init; } = true;

    /// <summary>Where the demo song starts, in seconds (handy for screenshots).</summary>
    public double DemoStart { get; init; }

    public IslandPosition Position { get; init; } = IslandPosition.Top;

    /// <summary>Render every pill state to PNG files in this folder and exit.</summary>
    public string? SnapshotDirectory { get; init; }

    /// <summary>Write what the window looks like to Windows (rects, DPI, styles) to this file after 2s.</summary>
    public string? DiagnoseFile { get; init; }

    /// <summary>Quit after this many seconds (scripted checks).</summary>
    public double? ExitAfter { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            double? Number() => double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

            options = args[i].ToLowerInvariant() switch
            {
                "--demo" => options with { Demo = true },
                "--demo-start" => options with { DemoStart = Number() ?? 0 },
                "--position" => options with
                {
                    Position = Next()?.ToLowerInvariant() == "bottom" ? IslandPosition.Bottom : IslandPosition.Top,
                },
                "--snapshot" => options with { SnapshotDirectory = Next() },
                "--diagnose" => options with { DiagnoseFile = Next() },
                "--exit-after" => options with { ExitAfter = Number() },
                _ => options,
            };
        }
        return options;
    }
}
