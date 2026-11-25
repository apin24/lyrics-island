using System.Globalization;

namespace LyricsIsland.Windows;

/// <summary>Command-line options. Unknown arguments are ignored so a stray flag never stops the app.</summary>
public sealed record AppOptions
{
    /// <summary>Use the demo player (milestone 3 always does; Spotify comes later).</summary>
    public bool Demo { get; init; } = true;

    /// <summary>Where the demo song starts, in seconds (handy for screenshots).</summary>
    public double DemoStart { get; init; }

    /// <summary>Pause the demo right away (checks of the paused state).</summary>
    public bool StartPaused { get; init; }

    public IslandPosition Position { get; init; } = IslandPosition.Top;

    /// <summary>Render every pill state to PNG files in this folder and exit.</summary>
    public string? SnapshotDirectory { get; init; }

    /// <summary>Write the frames of a scripted demo (PNG, 25 fps) to this folder and exit.</summary>
    public string? RecordDirectory { get; init; }

    /// <summary>Write what the window looks like to Windows (rects, DPI, styles) to this file after 2s.</summary>
    public string? DiagnoseFile { get; init; }

    /// <summary>Quit after this many seconds (scripted checks).</summary>
    public double? ExitAfter { get; init; }

    /// <summary>Overrides the compact shadow as "blur,offset,opacity" (calibrating against the Mac screenshots).</summary>
    public (double Blur, double Offset, double Opacity)? Shadow { get; init; }

    private static (double, double, double)? ParseShadow(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 3 }) return null;
        var numbers = parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN).ToArray();
        return numbers.Any(double.IsNaN) ? null : (numbers[0], numbers[1], numbers[2]);
    }

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
                "--start-paused" => options with { StartPaused = true },
                "--position" => options with
                {
                    Position = Next()?.ToLowerInvariant() == "bottom" ? IslandPosition.Bottom : IslandPosition.Top,
                },
                "--snapshot" => options with { SnapshotDirectory = Next() },
                "--record" => options with { RecordDirectory = Next() },
                "--diagnose" => options with { DiagnoseFile = Next() },
                "--exit-after" => options with { ExitAfter = Number() },
                "--shadow" => options with { Shadow = ParseShadow(Next()) },
                _ => options,
            };
        }
        return options;
    }
}
