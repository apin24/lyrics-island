# Lyrics Island for Windows (work in progress)

The Windows port of Lyrics Island: C# + WPF on .NET 8, reading the current track from the System Media Transport Controls (SMTC). The plan, decisions and findings are in the [Windows port brief](../docs/windows-port.md).

## Status

| Milestone | Status |
|---|---|
| 1. SMTC probe | Done, see [Findings (milestone 1)](../docs/windows-port.md#findings-milestone-1) |
| 2. Core library + tests | Done, see [Notes from milestone 2](../docs/windows-port.md#notes-from-milestone-2-lyricsislandcore) |
| 3. Compact pill | Done (demo player), see [Notes from milestone 3](../docs/windows-port.md#notes-from-milestone-3-compact-pill) |
| 4. All states + animations | Done; CPU is above the 3% target while the equalizer runs, see [Notes from milestone 4](../docs/windows-port.md#notes-from-milestone-4-states-and-motion) |
| 5. Expanded panel + Spotify | Done, see [Notes from milestone 5](../docs/windows-port.md#notes-from-milestone-5-hover-panel-spotify) |
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
  LyricsIsland.Windows/        the WPF app: transparent island window, pill, hover panel, Spotify (SMTC) player, tray icon
  tools/SmtcProbe/             milestone 1: console probe for SMTC
  tools/record-demo.ps1        records the transitions GIF (needs ffmpeg)
```

## Run the app

```bash
cd windows
dotnet run --project LyricsIsland.Windows
```

It follows whatever Spotify (or, without Spotify, the current media session) is playing. Hover the pill to open the panel; click a line or the progress bar to seek. Quit from the tray icon (the white speech bubble in the notification area), which can also move the island between the top and bottom center.

| Option | Meaning |
|---|---|
| `--demo` | Play the fictional demo song instead of following Spotify |
| `--demo-start <sec>` | Demo, starting at this position |
| `--position top\|bottom` | Top center (default) or bottom center above the taskbar |
| `--start-paused`, `--pause-after <sec>` | Pause the demo right away / after a while |
| `--snapshot <dir>` | Render every pill state and the hover panel to PNG (2×, flat background), plus the lyric pill at 125% and 150%, and exit |
| `--record <dir>` | Write the frames of a scripted demo (25 fps, off screen) and exit; `tools/record-demo.ps1` turns them into a GIF with ffmpeg |
| `--diagnose <file>` | Every 2s (`--diagnose-every <sec>`), write the window and pill rects, DPI, extended styles, font, hover state, animation frames and the panel's click targets to a file (never lyrics) |
| `--log-lines <file>` | Log every line change, mode change and hover in/out with times (never lyrics) |
| `--shadow <blur,offset,opacity>` | Override the pill shadow (for calibrating against the Mac screenshots) |
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
