using System.Globalization;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>Command-line options. Unknown arguments are ignored so a stray flag never stops the app.</summary>
public sealed record AppOptions
{
    /// <summary>Use the demo player instead of the media controls (Spotify). <c>--demo-start</c> implies it.</summary>
    public bool Demo { get; init; }

    /// <summary>Where the demo song starts, in seconds (handy for screenshots).</summary>
    public double DemoStart { get; init; }

    /// <summary>Pause the demo after this many seconds (0 = right away; checks of the paused state).</summary>
    public double? PauseAfter { get; init; }

    /// <summary>Overrides (and saves) the island position for this run.</summary>
    public IslandPlacement? Position { get; init; }

    /// <summary>Use this settings file instead of %APPDATA%\LyricsIsland\settings.json (tests).</summary>
    public string? SettingsFile { get; init; }

    /// <summary>Keep the panel open as if hovered (screenshots without moving the cursor).</summary>
    public bool Expand { get; init; }

    /// <summary>Open the tray menu at this screen point after start ("x,y", px), for screenshots.</summary>
    public (int X, int Y)? ShowMenuAt { get; init; }

    /// <summary>Render every pill state to PNG files in this folder and exit.</summary>
    public string? SnapshotDirectory { get; init; }

    /// <summary>Write the frames of a scripted demo (PNG, 25 fps) to this folder and exit.</summary>
    public string? RecordDirectory { get; init; }

    /// <summary>Write what the window looks like to Windows (rects, DPI, styles) to this file after 2s.</summary>
    public string? DiagnoseFile { get; init; }

    /// <summary>How often <c>--diagnose</c> rewrites its file, in seconds (default 2).</summary>
    public double DiagnoseSeconds { get; init; } = 2;

    /// <summary>Append every line change (time, position, line index, mode; never the lyrics) to this file.</summary>
    public string? LineLogFile { get; init; }

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

    private static (int, int)? ParsePoint(string? value) =>
        value?.Split(',') is [var x, var y] && int.TryParse(x, out var px) && int.TryParse(y, out var py) ? (px, py) : null;

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
                "--demo-start" => options with { Demo = true, DemoStart = Number() ?? 0 },
                "--start-paused" => options with { PauseAfter = 0 },
                "--pause-after" => options with { PauseAfter = Number() },
                "--position" => options with
                {
                    Position = Next()?.ToLowerInvariant() == "bottom" ? IslandPlacement.Bottom : IslandPlacement.Top,
                },
                "--snapshot" => options with { SnapshotDirectory = Next() },
                "--record" => options with { RecordDirectory = Next() },
                "--diagnose" => options with { DiagnoseFile = Next() },
                "--log-lines" => options with { LineLogFile = Next() },
                "--diagnose-every" => options with { DiagnoseSeconds = Number() ?? 2 },
                "--exit-after" => options with { ExitAfter = Number() },
                "--shadow" => options with { Shadow = ParseShadow(Next()) },
                "--settings" => options with { SettingsFile = Next() },
                "--expand" => options with { Expand = true },
                "--show-menu" => options with { ShowMenuAt = ParsePoint(Next()) },
                _ => options,
            };
        }
        return options;
    }
}
