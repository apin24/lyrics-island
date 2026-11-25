# Lyrics Island for Windows (work in progress)

The Windows port of Lyrics Island: C# + WPF on .NET 8, reading the current track from the System Media Transport Controls (SMTC). The plan, decisions and findings are in the [Windows port brief](../docs/windows-port.md).

## Status

| Milestone | Status |
|---|---|
| 1. SMTC probe | Done, see [Findings (milestone 1)](../docs/windows-port.md#findings-milestone-1) |
| 2. Core library + tests | Done, see [Notes from milestone 2](../docs/windows-port.md#notes-from-milestone-2-lyricsislandcore) |
| 3. Compact pill | Done (demo player), see [Notes from milestone 3](../docs/windows-port.md#notes-from-milestone-3-compact-pill) |
| 4. All states + animations | To do |
| 5. Expanded panel | To do |
| 6. Tray menu, settings, hotkey | To do |
| 7. Release | To do |

## Requirements

- Windows 10 1809 or later
- The [.NET SDK](https://dotnet.microsoft.com/download), version 8 or newer. The projects target .NET 8; a newer SDK builds them fine, and running them needs the .NET 8 runtime.

## Layout

```
windows/
  LyricsIsland.Windows.sln
  Directory.Build.props        shared settings (version, nullable)
  LyricsIsland.Core/           LRC parser, LRCLIB client, settings, demo player, state machine (no UI, plain net8.0)
  LyricsIsland.Core.Tests/     xUnit tests for every timing and parsing rule
  LyricsIsland.Windows/        the WPF app: transparent island window, pill, tray icon
  tools/SmtcProbe/             milestone 1: console probe for SMTC
```

## Run the app

```bash
cd windows
dotnet run --project LyricsIsland.Windows
```

For now it always plays the fictional demo song. Quit from the tray icon (the white speech bubble in the notification area), which can also move the island between the top and bottom center.

| Option | Meaning |
|---|---|
| `--demo-start <sec>` | Start the demo song at this position |
| `--position top\|bottom` | Top center (default) or bottom center above the taskbar |
| `--snapshot <dir>` | Render every pill state to PNG (2×, flat background) and exit |
| `--diagnose <file>` | After 2s, write the window and pill rects, DPI, extended styles and font to a file |
| `--exit-after <sec>` | Quit by itself (scripted checks) |

## Tests

```bash
cd windows
dotnet test
```

`LyricsIsland.Core` and its tests target plain `net8.0`, so they also run on macOS and Linux: `dotnet test windows/LyricsIsland.Core.Tests`.

## SMTC probe

Prints what Windows reports about the playing track: title, artist, status, and the interpolated position 10×/s, plus every SMTC event as it arrives.

```bash
cd windows
dotnet run --project tools/SmtcProbe
```

| Option | Meaning |
|---|---|
| `--app <text>` | Prefer the session whose app ID contains `<text>` (default `Spotify`), else the current session |
| `--current` | Always follow the current session (what the Windows media flyout shows) |
| `--duration <sec>` | Stop after this long and print the summary |
| `--events-only` | Skip the per-tick lines |
| `--log <file>` / `--csv <file>` | Save the output / one row per tick |
| `--exercise` | Drive the player by itself (play, seek, pause, resume, next), then restore the original track, position and play state |

Keys while it runs: **Space** play/pause, **N** next, **P** previous, **←/→** seek ∓10s, **Q** quit.

Each tick line shows two clocks: `smtc` is the latest timeline report extrapolated to now, and `local` is a smooth clock that follows the Mac app's resync rule. `Δ` is the difference between them. When it stops (Ctrl+C, Q or `--duration`), the probe prints a summary: how often the timeline was reported, how fresh `LastUpdatedTime` was, and how far the local clock drifted.
