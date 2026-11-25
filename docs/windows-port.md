# Windows port: handoff brief

This document is the starting point for building **Lyrics Island for Windows**. It's written so that a developer, or a Claude Code session running on a Windows PC, can pick it up without any other context.

The macOS app in this repo is finished and released (v0.1.0). The Windows app should feel like the same product: same states, same timings, same look, rebuilt with native Windows tech.

## Goal

A small Windows app that shows the line of lyrics currently being sung in a black, Dynamic Island–style pill at the top center of the screen. Hovering the pill expands it into a karaoke lyrics panel with playback controls.

Look at `docs/demo.gif` and `docs/screenshots/*.png` before writing any code. That's the target.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Language / UI | **C# + WPF on .NET 8** (`net8.0-windows10.0.19041.0`) | Per-pixel transparent, topmost windows are simple in WPF. It builds from the CLI (`dotnet build`), so Visual Studio isn't needed. The WinRT media APIs are available through the Windows 10 TFM. |
| Where the code lives | `windows/` folder in **this repo** | One repo means one README, one star count, one issue tracker and shared docs/screenshots. The Mac app stays at the repo root, untouched. |
| Now-playing source | **System Media Transport Controls** (`GlobalSystemMediaTransportControlsSessionManager`) | The same data the Windows media flyout shows. No API keys, works with the Spotify desktop app, and also with browsers (so the Spotify web player works too). |
| Lyrics | **LRCLIB**, same logic as the Mac app | Free, no key, time-synced LRC. |
| Minimum OS | Windows 10 1809+ (SMTC requirement); target Windows 11 | |
| Architectures | win-x64 and win-arm64 | |

If any of these turn out to be wrong on real hardware, change them and note why here.

## Ground rules for this repo

- Code, comments, README and UI text are in **English**. The maintainer chats in casual Indonesian.
- Commit identity for this repo (set it repo-locally on the PC):
  ```bash
  git config user.name "Ilham Arifin"
  git config user.email "56109261+apin24@users.noreply.github.com"
  ```
- **No AI co-author trailers** (`Co-Authored-By: …`) or "generated with" lines in commits or PRs.
- Don't change the Mac app (`Sources/`, `scripts/`, `Resources/`) unless asked.
- Demo content must stay **fictional**. Never put real song lyrics in the repo, screenshots or GIFs. Reuse the demo song from `Sources/LyricsIsland/Players.swift` ("Streetlight Hymn" by "Dusk Collective").
- Verify on the real PC with screenshots before calling anything done.

## Reference: the Mac source files

| Mac file | What to port |
|---|---|
| `IslandModel.swift` | State machine, timing rules, hover logic, offset handling. **Port this behavior exactly.** |
| `LyricsService.swift` | LRCLIB client: exact match → search fallback, title cleanup, cache |
| `LRCParser.swift` | LRC parsing rules |
| `Players.swift` | `DemoPlayer` (demo song + LRC text). Ignore the AppleScript part. |
| `IslandView.swift`, `ExpandedView.swift`, `Components.swift` | Layout, sizes, colors, animations |
| `AppDelegate.swift` | Tray menu items and settings |
| `Settings.swift` | Settings and defaults |

## Now playing on Windows (SMTC)

```csharp
var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
// Prefer the Spotify session; optionally fall back to the current session.
var session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
              ?? manager.GetCurrentSession();
var props = await session.TryGetMediaPropertiesAsync();   // Title, Artist, AlbumTitle, Thumbnail
var playback = session.GetPlaybackInfo();                  // PlaybackStatus, Controls.Is*Enabled
var timeline = session.GetTimelineProperties();            // Position, EndTime, LastUpdatedTime
```

- **Events:** `SessionsChanged`, `CurrentSessionChanged`, and per session `MediaPropertiesChanged`, `PlaybackInfoChanged`, `TimelinePropertiesChanged`.
- **Position:** the timeline isn't pushed continuously. Compute `position = timeline.Position + (now - timeline.LastUpdatedTime)` while playing, and tick locally about 10×/s. This is the same idea as the Mac's `PlayerSnapshot.position(at:)`. Only resync when the reported position drifts more than 0.35s from the local clock.
- **Verify early:** how often Spotify updates `TimelineProperties`, and whether `LastUpdatedTime` is reliable. If the timeline is stale, add a fallback (resync on `PlaybackInfoChanged`, or estimate from the moment the track changed).
- **Controls:** `TryTogglePlayPauseAsync`, `TrySkipNextAsync`, `TrySkipPreviousAsync`, `TryChangePlaybackPositionAsync(ticks)`. Check `playback.Controls.IsPlaybackPositionEnabled`. If Spotify doesn't allow seeking through SMTC, hide click-to-seek instead of showing a dead control.
- **Artwork:** `props.Thumbnail.OpenReadAsync()` → `BitmapImage`.
- **Track identity:** there's no Spotify track ID. Use a normalized key like `title|artist|album|round(duration)` for the lyrics cache and the per-song offset.
- **Never block the UI thread** on SMTC or network calls.

## Lyrics (port of `LyricsService.swift` + `LRCParser.swift`)

1. `GET https://lrclib.net/api/get?track_name=…&artist_name=…&album_name=…&duration=<seconds>`. A 404 means not found. Use `syncedLyrics`.
2. If there's no synced result: `GET /api/search?track_name=<clean title>&artist_name=<primary artist>`. Pick the first result with `syncedLyrics` whose `duration` is within ±4s, else the first synced one.
3. Clean the title: strip ` - Remastered 2011`-style suffixes and `(feat. …)` / `[with …]`. The primary artist is the text before the first comma.
4. Manual search: `GET /api/search?q=<free text>`, top synced result.
5. Send the header `User-Agent: LyricsIsland/<version> (Windows)`. Only send title/artist/album/duration, nothing else.
6. Cache found lyrics as `.lrc` files in `%LOCALAPPDATA%\LyricsIsland\Cache\`. Keep not-found results in memory only, so lyrics added to LRCLIB later still show up.
7. LRC parsing:
   - A line can have several `[mm:ss.xx]` stamps.
   - Metadata tags like `[ar:…]` are ignored.
   - `[offset:+250]` means `time - 0.25s`.
   - Stable-sort by time.
   - Blank lines are real entries (they mean an interlude).

## Behavior (port of `IslandModel.swift`)

**Mode precedence**, evaluated about every 100 ms:

1. **Hidden** if the island is turned off, if "hide in fullscreen" is on and a fullscreen app is active, or if nothing is playing.
2. **Expanded** while hovered.
3. **Paused** if playback is paused. Hidden after 10s paused.
4. **Song change** for 2.5s after the track changes.
5. **Loading** while lyrics are being fetched.
6. **No lyrics** if none were found. Becomes **mini** (art + equalizer only) after 5s.
7. **Lyric** if the current line has text.
8. **Interlude** if the current line is blank, or it's before the first line.

**Timing rules:**

- **Current line:** the last line with `time <= position + offset`.
- **Offset:** per song, stored in settings, adjusted in ±0.5s steps. Positive offset means lines appear earlier (it fixes lyrics that lag).
- **Karaoke** (expanded panel):
  - Progress through the line = `(position + offset - line.time) / estimate`.
  - `estimate = min(gap * 0.9, max(1.2, chars * 0.085))`, where `gap` is the time until the next line.
  - Light up `ceil(words * progress)` words.
- **Hover:**
  - Expand after the cursor has been over the pill for 0.3s. Collapse 1.0s after it leaves.
  - Detect hover by polling `GetCursorPos` against the pill's screen rect. Don't rely on mouse-enter events of a click-through window.
  - Clicking the compact pill expands it right away.

## Window (WPF specifics)

- `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`, `ShowInTaskbar=False`.
- Extended styles via P/Invoke:
  - `WS_EX_TOOLWINDOW` keeps it out of Alt+Tab.
  - `WS_EX_NOACTIVATE` stops it stealing focus.
  - Toggle `WS_EX_TRANSPARENT` so the window is **click-through everywhere except the pill**. Turn it off only while the cursor is inside the pill rect, the same approach as the Mac's `ignoresMouseEvents`.
- Use a fixed, larger transparent window (about 760×420 DIP) at the top center of the primary monitor, and animate the pill inside it. Don't resize the window per frame.
- Per-monitor DPI awareness (PerMonitorV2) in `app.manifest`. Reposition when displays or DPI change.
- **Fullscreen detection:** `SHQueryUserNotificationState` returns `QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN` or `QUNS_PRESENTATION_MODE` when a fullscreen app is active. Use it for the opt-in "Hide when an app is fullscreen" setting (default off).
- **Open question for the maintainer:** Windows has no menu bar, so a pill at the top center can cover browser tabs in maximized windows. Try *top center, 8 px from the top* first, compare it with *bottom center, just above the taskbar*, and make the position a setting if both are useful.

## Visual spec (from the Mac app and Figma)

| Element | Spec |
|---|---|
| Pill background | `#000000`, 1 px inner border white 9%, shadow 0/8/24 black 35% (expanded: 0/20/50, 45%) |
| Compact | Height 34, fully rounded (radius 17), padding 6 left / 12 right, art 22×22 radius 6, gap 10, text max width ≈ 380 then ellipsis, equalizer 16×14 on the right |
| Song change | Height 52, radius 26, art 36 radius 9, title Semibold 14 + artist Regular 12 (white 60%) |
| Expanded | Width 560, radius 32, padding 18/20/16/20, spacing 16. Header: art 44 radius 10, title, artist, equalizer. Lyrics: previous line, current line (Bold 22, karaoke), next two lines (Semibold 17, white 30% / 16%). Progress bar 4 px with times. Controls: source chip · ⏮ ⏯ ⏭ · offset − / + |
| Colors | Text white 100 / 60 / 42 / 30 %, accent green `#1ED760`, chip background white 14% |
| Font | **Segoe UI Variable** (Windows' equivalent of SF Pro), same sizes and weights |
| Motion | Size changes: spring-like ease, about 0.42s with a slight overshoot. Line change in compact: old line slides up + fades, new line slides in from below, 0.28s. Equalizer: 4 bars, smooth random heights at about 20 fps, flat when paused. |
| Placeholder art | Orange→magenta→indigo gradient with a sun circle and a dark skyline (see `PlaceholderArt` in `Components.swift`) |

## Tray menu and settings (port of `AppDelegate.swift`)

Tray icon (quote-bubble style) with this menu:

- "Lyrics Island" header and "Now playing: Title — Artist"
- A warning item if no supported player is found
- **Show Island** (Ctrl+Alt+L, global hotkey via `RegisterHotKey`)
- **Expand on Hover**
- **Hide When an App Is Fullscreen**
- **Text Size** (Small 0.9 / Medium 1 / Large 1.15)
- **Lyrics Timing (±0.0s)**: Too Early −0.5s / Too Late +0.5s / Reset
- **Search Lyrics Manually…**
- **Demo Mode (No Spotify Needed)**
- **Start with Windows** (registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)
- **Quit**

Store settings in `%APPDATA%\LyricsIsland\settings.json`.

## Milestones

Verify each one on the PC (screenshots) before moving on.

1. **SMTC probe:** a console app that prints title, artist, status, and the interpolated position 10×/s while Spotify plays. Note how the timeline behaves.
2. **Core library + tests:** LRC parser, LRCLIB client, and the state machine as plain C# (`LyricsIsland.Core`) with xUnit tests. Port the timing rules exactly.
3. **Compact pill:** a transparent, topmost, click-through window showing the current line from the demo player.
4. **All states + animations**, matching the screenshots.
5. **Expanded panel:** hover, karaoke, progress, controls, offset, manual search.
6. **Tray menu, settings, hotkey, start with Windows.**
7. **Release:**
   - Build with `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` (and `win-arm64`).
   - Zip as `LyricsIsland-Windows-<version>-<arch>.zip`.
   - Attach to a GitHub release.
   - Add a Windows section to the README.
   - Later: a winget manifest.

Suggested layout:

```
windows/
  LyricsIsland.Windows.sln
  LyricsIsland.Core/          parser, LRCLIB client, state machine (no UI)
  LyricsIsland.Core.Tests/
  LyricsIsland.Windows/       WPF app (window, tray, SMTC player, demo player)
```

## Distribution notes

- Unsigned apps trigger **SmartScreen** ("Windows protected your PC" → More info → Run anyway). This is the Windows version of the Mac's Gatekeeper step. Document it in the README, the same way the Mac section does.
- Code signing needs a paid certificate. That's out of scope for now.

## Lessons from the Mac version

- Never block the UI thread on permission prompts, network calls or media APIs. On the Mac, an Apple Event sent on the main thread froze the whole app until a permission dialog was answered.
- Detect hover by polling the cursor position; event-based hover on a click-through overlay is unreliable.
- Keep the animated content and the blurred shadow on separate layers, so animations don't re-render the blur every frame.
- Record README GIFs with a flat background. GIFs have 256 colors and gradients band.
- Keep demo and showcase modes in the app (`--demo`, `--showcase`). They make testing and screenshots easy without Spotify.
