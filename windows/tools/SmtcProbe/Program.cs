using System.Globalization;
using System.Text;

namespace LyricsIsland.SmtcProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ProbeOptions? options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }
        if (options is null)
        {
            Console.WriteLine(ProbeOptions.Usage);
            return 0;
        }

        // Dots as decimal separators, whatever the system locale is.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;
        if (!Console.IsOutputRedirected) Console.Title = "Lyrics Island · SMTC probe";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        if (options.Duration is { } duration) cts.CancelAfter(duration);

        using var probe = new Probe(options);
        try
        {
            await probe.RunAsync(cts);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Probe failed: {e}");
            return 1;
        }
        probe.PrintSummary();
        return 0;
    }
}

internal sealed record ProbeOptions(
    string App,
    bool CurrentOnly,
    TimeSpan? Duration,
    int TickMs,
    bool PrintTicks,
    string? LogPath,
    string? CsvPath,
    bool Exercise)
{
    public const string Usage = """
        SmtcProbe: prints what Windows' media controls (SMTC) report about the playing track.

        Usage: SmtcProbe [options]

          --app <text>       Prefer the session whose app ID contains <text> (default: Spotify).
                             Falls back to the current session if no such app is playing.
          --current          Always follow the current session (whatever the media flyout shows).
          --duration <sec>   Stop after this many seconds and print the summary.
          --tick <ms>        Local clock tick (default: 100 = 10x per second).
          --events-only      Don't print a line per tick, only events, resyncs and the summary.
          --log <file>       Also write everything printed to a text file.
          --csv <file>       Write one row per tick (positions, drift) for later analysis.
          --exercise         Drive the player by itself: play (if paused), seek +20s at 12s,
                             pause at 20s, resume at 27s, next track at 34s. At the end it
                             goes back to the original track and position, and pauses again
                             if it started playback. Implies --duration 50 unless one is given.
          -h, --help         Show this help.

        Keys while running: Space play/pause · N next · P previous · ←/→ seek ∓10s · Q quit
        """;

    public static ProbeOptions? Parse(string[] args)
    {
        var app = "Spotify";
        var currentOnly = false;
        TimeSpan? duration = null;
        var tickMs = 100;
        var printTicks = true;
        string? logPath = null;
        string? csvPath = null;
        var exercise = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{arg} needs a value.");
                return args[++i];
            }

            switch (arg)
            {
                case "-h" or "--help":
                    return null;
                case "--app":
                    app = Value();
                    break;
                case "--current":
                    currentOnly = true;
                    break;
                case "--duration":
                    duration = TimeSpan.FromSeconds(ParsePositive(arg, Value()));
                    break;
                case "--tick":
                    tickMs = (int)ParsePositive(arg, Value());
                    break;
                case "--events-only":
                    printTicks = false;
                    break;
                case "--log":
                    logPath = Value();
                    break;
                case "--csv":
                    csvPath = Value();
                    break;
                case "--exercise":
                    exercise = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (exercise) duration ??= TimeSpan.FromSeconds(50);
        return new ProbeOptions(app, currentOnly, duration, tickMs, printTicks, logPath, csvPath, exercise);
    }

    private static double ParsePositive(string option, string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
            return number;
        throw new ArgumentException($"{option} needs a positive number, got \"{value}\".");
    }
}
