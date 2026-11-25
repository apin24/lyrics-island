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
- **Position:** the timeline isn't pushed continuously. Compute `position = timeline.Position + (now - timeline.LastUpdatedTime)` while playing, and tick locally about 10×/s. This is the same idea as the Mac's `PlayerSnapshot.position(at:)`. Only resync when the reported position drifts more than 0.35s from the local clock. *Decided after milestone 1: the SMTC player takes every timeline report (`ResyncThreshold = 0`); 0.35s stays the default for the demo player and any other player. See [Findings](#findings-milestone-1).*
- **Verify early:** how often Spotify updates `TimelineProperties`, and whether `LastUpdatedTime` is reliable. If the timeline is stale, add a fallback (resync on `PlaybackInfoChanged`, or estimate from the moment the track changed). *Done in milestone 1: the timeline is reliable. The fallback is still planned, see below.*
- **Controls:** `TryTogglePlayPauseAsync`, `TrySkipNextAsync`, `TrySkipPreviousAsync`, `TryChangePlaybackPositionAsync(ticks)`. Check `playback.Controls.IsPlaybackPositionEnabled`. If Spotify doesn't allow seeking through SMTC, hide click-to-seek instead of showing a dead control. *Spotify allows seeking (milestone 1), but keep the check for other apps.*
- **Artwork:** `props.Thumbnail.OpenReadAsync()` → `BitmapImage`.
- **Track identity:** there's no Spotify track ID. Use a normalized key like `title|artist|album|round(duration)` for the lyrics cache and the per-song offset.
- **Never block the UI thread** on SMTC or network calls.

### Findings (milestone 1)

Measured with [`windows/tools/SmtcProbe`](../windows/tools/SmtcProbe) (`--exercise`: play, seek, pause, resume, next track, then restore) on Windows 11 Enterprise build 26200, x64, with **Spotify from the Microsoft Store** (AUMID `SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify`). Three runs of about 50s each, 2026-11-26.

| What | Result |
|---|---|
| `TimelinePropertiesChanged` | Fires reliably. Every timeline change arrived as an event; the 10×/s poll never saw a change first. |
| Report rate | About every **4.5s** while playing (also while paused: same position, fresh timestamp), plus extra reports right after play, pause, seek and track change. |
| `LastUpdatedTime` | Reliable. 1–4 ms old when the event arrives, never missing. |
| Accuracy | Extrapolating a report matches the next periodic report within **±0.002s**. No fallback needed. |
| Play / pause | The status event comes 30–300 ms after the command (once 1.4s, on the first play after Spotify had been idle). A timeline report with the exact position arrives in the same millisecond. |
| Start-up correction | After playing from 0:00, a seek or a track change, Spotify reports `pos X`, then 0.1–0.2s later a second report that is **0.1–0.2s behind** the first one's extrapolation (audio starts after buffering). |
| Seek | `IsPlaybackPositionEnabled` is true. `TryChangePlaybackPositionAsync` works, applied within ~15 ms, also while paused. Click-to-seek can stay. |
| Track change | The timeline (pos 0, new `EndTime`) arrives first. `MediaPropertiesChanged` follows 80–100 ms later and fires **twice** with identical data. |
| Metadata | Title, artist, album, album artist, `PlaybackType = Music`. Artwork is a PNG thumbnail (~170–215 KB) from `Thumbnail.OpenReadAsync()`. |
| Controls | Play/pause, next, previous and seek are all enabled and accepted. |

What this means for the app:

1. **Resync rule (decided).** With the Mac rule (keep the local clock unless the track or play state changed or it drifts ≥ 0.35s), the clock stayed **0.11–0.15s ahead** after every seek and track change until the song ended, because the correcting report is under the threshold. The Mac needs the dead band since AppleScript positions are polled and coarse. SMTC reports are exact and timestamped, so **the SMTC player uses `ResyncThreshold = 0` and the model takes every timeline report** (the jumps are ≤ 0.25s and only happen right after play, seek or a track change). The demo player and any other player keep the Mac's 0.35s default. Tests: `ZeroThresholdAppliesSmtcCorrectionsAfterSeekAndTrackChange` replays the reports Spotify sent, and `MacThresholdKeepsTheClockAheadAfterSmtcCorrections` shows why.
   - *Watch for in milestones 3–4:* a backward correction right after a seek can briefly move the lyric line back to the previous one when the position is right at a line boundary. Don't fix it pre-emptively. If it flickers on screen, add this rule: ignore a backward line change caused by a correction of less than 0.25s within 1s after a seek or a track change.
2. A play-state change still takes the player's position, as on the Mac. The paused position arrives together with the status event.
3. Dedupe `MediaPropertiesChanged` by the track key so the song-change state and the lyrics fetch run once per song. The timeline arrives first, so its `EndTime` is already the new song's duration when the key is built. The order isn't guaranteed, though, so re-key if `EndTime` changes within ~1s of a track change.
4. Never block on any of this: every SMTC call above returned in under 10 ms, but events arrive on background threads.

**If the timeline is stale or rarely updated** (not seen with Spotify, but other apps that use SMTC behave differently), the SMTC player should fall back like this:

- `LastUpdatedTime` missing (it comes back as the year 1601): use the moment the report arrived as its timestamp.
- No timeline report while playing: keep extrapolating the last report. That stays correct as long as playback runs at normal speed, and the probe showed Spotify re-reports every ~4.5s anyway.
- Re-read `GetTimelineProperties()` on every `PlaybackInfoChanged` and `MediaPropertiesChanged`, not only on `TimelinePropertiesChanged`.
- New track and no fresh timeline yet (its `EndTime` didn't change): assume position 0 at the moment the track changed.

Not verified yet: the Spotify installer version from spotify.com (AUMID `Spotify.exe`), browsers (Spotify web player in Chrome or Edge), several sessions at once, win-arm64, and the probe's keyboard controls (they need someone at the keyboard).

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

### Notes from milestone 2 (`LyricsIsland.Core`)

`windows/LyricsIsland.Core` ports `IslandModel.swift`, `LyricsService.swift`, `LRCParser.swift`, `Settings.swift` and `DemoPlayer` with the same numbers and rules. It targets plain `net8.0`, so the tests also run on a Mac: `dotnet test windows/LyricsIsland.Core.Tests`. Every rule above has a test, and time is driven by a fake clock (`FakeTimeProvider`), so the timing tests are exact.

Where it differs from the Mac code, and why:

| Difference | Why |
|---|---|
| The resync threshold comes from the player (`IPlayer.ResyncThreshold`, default 0.35s as on the Mac). As on the Mac, a drift of exactly 0.35s already resyncs (the Mac keeps the clock only while the drift is `< 0.35`). | The SMTC player uses 0, as decided after the [milestone 1 findings](#findings-milestone-1). Nothing changes for the demo player. |
| Cache file name: an ASCII prefix of the track ID plus 16 hex characters of its SHA-256. | Windows track IDs contain the title. With the Mac's "replace everything that isn't `[A-Za-z0-9_-]`", two Japanese titles by the same artist got the same file. |
| Query values are escaped with `Uri.EscapeDataString`, so `+` becomes `%2B` and `&` becomes `%26`. | `URLComponents` on the Mac leaves `+` as is, which servers read as a space ("C++" → "C  "). Possibly worth fixing on the Mac too. |
| Seconds are rounded half away from zero; text length counts grapheme clusters. | Matches Swift's `.rounded()` and `String.count`. .NET rounds halves to even and counts UTF-16 units by default. |
| `PlayerIssue` has only `NoSupportedPlayer`. | The Mac's "not installed" / "not authorized" (Automation permission) don't exist with SMTC. |
| The model takes `Tick(cursorOverPill)` and `IsFullscreen` from the app instead of reading the cursor, the menu bar or the screen itself. No `MenuBarScanner`, no artwork in the model. | Keeps Core free of Win32 and WPF so it can be tested anywhere. The WPF app (milestone 3+) does cursor polling, fullscreen detection and artwork. |
| Settings are saved as `%APPDATA%\LyricsIsland\settings.json` already (`IslandSettings.Load/Save/SaveOnChange`). | The model needs per-song offsets now; the tray menu that edits the rest is still milestone 6. |

## Window (WPF specifics)

- `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`, `ShowInTaskbar=False`.
- Extended styles via P/Invoke:
  - `WS_EX_TOOLWINDOW` keeps it out of Alt+Tab.
  - `WS_EX_NOACTIVATE` stops it stealing focus.
  - Toggle `WS_EX_TRANSPARENT` so the window is **click-through everywhere except the pill**. Turn it off only while the cursor is inside the pill rect, the same approach as the Mac's `ignoresMouseEvents`.
- Use a fixed, larger transparent window (about 760×420 DIP) at the top center of the primary monitor, and animate the pill inside it. Don't resize the window per frame.
- Per-monitor DPI awareness (PerMonitorV2) in `app.manifest`. Reposition when displays or DPI change.
- **Fullscreen detection:** `SHQueryUserNotificationState` returns `QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN` or `QUNS_PRESENTATION_MODE` when a fullscreen app is active. Use it for the opt-in "Hide when an app is fullscreen" setting (default off).
- **Open question for the maintainer:** Windows has no menu bar, so a pill at the top center can cover browser tabs in maximized windows. Try *top center, 8 px from the top* first, compare it with *bottom center, just above the taskbar*, and make the position a setting if both are useful. *Compared in milestone 3, see below.*

### Notes from milestone 3 (compact pill)

`windows/LyricsIsland.Windows` is the WPF app. It runs the demo player only for now.

- **Window:** 760×420 DIP, transparent, topmost, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. It's placed with `SetWindowPos` in physical pixels, centered on the primary monitor's work area, and placed again on `WM_DISPLAYCHANGE`, `WM_DPICHANGED` and `WM_SETTINGCHANGE` (`SPI_SETWORKAREA`, e.g. the taskbar moved). The pill sits 8 DIP inside the work area. In the bottom position the window reaches the bottom of the screen, so the pill's shadow can fall onto the taskbar.
- **Click-through:** `WS_EX_TRANSPARENT` is set except while the cursor (polled with `GetCursorPos` every 100 ms) is inside the pill rect plus 3 DIP, as on the Mac.
- **Look:** compact rows for lyric, interlude, loading, no lyrics, mini and paused, built from the Mac's `IslandView.swift` numbers. The text uses Segoe UI Variable Text Semibold 13. The pause icon is drawn as two bars, because the Segoe Fluent pause glyph is an outline and the Mac uses `pause.fill`. Off-screen renders of every state are in [`docs/screenshots/windows/`](screenshots/windows) (`LyricsIsland.exe --snapshot <dir>`).
- **Temporary until later milestones:** no animations (milestone 4); song change shows as a compact "Title · Artist" row (milestone 4); hover-to-expand is switched off, and the expanded mode shows the compact row, until the panel exists (milestone 5); settings aren't saved and the tray menu only has Top Center / Bottom Center / Quit (milestone 6).

Checked on the PC (Windows 11, 1920×1080 at 100%, taskbar at the bottom):

| Check | Result |
|---|---|
| Placement | Window 580,0 760×420 px; pill 8 px from the top. Bottom: pill bottom 8 px above the taskbar (y 1024, taskbar from 1032). |
| Extended styles | Tool window, no-activate and layered are set; click-through is on while the cursor is elsewhere. |
| Click-through | Cursor elsewhere: click-through, the point belongs to the window below. Cursor on the pill: not click-through, the point belongs to the island. Cursor beside the pill (inside the window, on the shadow): click-through again. |
| Focus | Starting the app and hovering the pill never changed the foreground window. |
| Font | `Segoe UI Variable Text` resolves to Segoe UI Variable Text Semibold (SEGUIVAR.TTF). |
| Size vs. Mac | The lyric pill is 268 px wide on screen and 536 px in the 2× snapshot, close to the Mac's lyric.png (564 px at 2×, measured the same way; SF Pro is a little wider than Segoe UI). |
| CPU | 0.6% of one core with the equalizer running (Debug build). |

Not checked yet: other scale factors, a DPI change while running, several monitors, and a taskbar at the top or side. The DropShadowEffect looks a little lighter than the Mac shadow at the same values (blur 24, offset 8, 35%); revisit it in milestone 4.

**Top center vs. bottom center.** Both work; **top center should stay the default**, with bottom center as a setting:

| | Top center (8 px from the top) | Bottom center (8 px above the taskbar) |
|---|---|---|
| Good | Same place as on the Mac, reads like a Dynamic Island. Nothing else lives there except the middle of a title bar. | Never covers title bars or browser tabs. |
| Bad | In a maximized window it covers the middle of the title bar or tab strip (y 8–42). While the cursor is on the pill, whatever is beneath it can't be clicked. | Covers the bottom of maximized windows: chat input boxes, status bars, video player controls, which get used a lot while music plays. It also sits right on top of Windows 11's centered taskbar icons and looks crowded. On the PC it covered the toolbar under a chat input. |

Milestone 6 should add an "Island Position: Top / Bottom" menu item (the preview tray menu already has it). *Agreed after review: top center is the default.*

### Notes from milestone 4 (states and motion)

All compact states, the song change state and the motion from `IslandView.swift` / `Components.swift`. Renders of every state and a GIF of the transitions are in [`docs/screenshots/windows/`](screenshots/windows) (`transitions.gif`: appear, song change, line changes, interlude, a track without lyrics, pause, disappear; made with `windows/tools/record-demo.ps1`, off screen on a simulated clock, flat background).

| Piece | How |
|---|---|
| Springs | SwiftUI's `.spring(response:dampingFraction:)` simulated per frame: stiffness (2π/response)², damping 4π·dampingFraction/response, unit mass. A new target keeps the velocity, as in SwiftUI. Mode changes use (0.42, 0.82), line changes and text size (0.35, 0.86). |
| Mode change | Width, height and corner radius on the mode spring; the old and new rows cross-fade in 0.18s, centered and clipped by the pill. |
| Line change | The old line slides up and fades, the new one comes in from below, clipped to the 18 pt slot; the slot width follows on the line spring and the pill hugs it. Ends of the new line can be clipped for 2–3 frames while the pill is still growing. |
| Appear / disappear | Scale from 0.4 plus fade on the mode spring, anchored at the screen edge (top, or bottom in the bottom position). |
| Equalizer / dots | The Mac formulas, 20 fps on one shared clock; the equalizer is flat at 3/14 and stops when paused. |
| Song change | 52 high, radius 26, art 36 (radius 9), title Semibold 14 + artist Regular 12 at white 60%, a 28 pt spacer before the equalizer. 100 px high at 2× like the Mac's songChange.png; 492 vs. 506 px wide (font). Segoe's taller line height is set to SF's 1.19 em, and the text block is nudged up 1.25 DIP, so the two lines sit like on the Mac. |
| Text size | Compact text 13 × scale, max width 380 × scale, then ellipsis (Small 0.9 / Medium 1 / Large 1.15). |

**Shadow.** `DropShadowEffect` blurs differently from SwiftUI: with the literal spec values (blur 24, offset 8, 35%) the shadow ends much closer to the pill. Rendering the lyric pill with several values and comparing the darkening under it with the Mac's `lyric.png` (every 3 px over 70 px at 2×) gave **blur 40, offset 8, opacity 0.42** (RMS difference 0.0095, vs. 0.043 for the literal values). The expanded shadow (0/20/50, 45%) needs the same calibration against `expanded.png` in milestone 5 (`--shadow blur,offset,opacity` overrides it for that). The shadow sits on its own layer with a `BitmapCache`, so the equalizer and line slides don't re-render the blur; it's only re-rendered while the pill changes size.

**CPU.** The test PC's monitor runs at **180 Hz**, and `CompositionTarget.Rendering` fires at that rate. Every animation step re-presents the layered window, which costs about 1.4 ms of CPU however little changed. What was done:

- Animations step at most 60 times a second (every third frame at 180 Hz), still in step with the display. A `DispatcherTimer` was a little cheaper but ticked unevenly (~45 fps, the 15.6 ms timer resolution).
- Springs stop once they're within a quarter pixel (the invisible sub-pixel tail was half the frames).
- Each lyric line is formatted and drawn once into its own `DrawingVisual`; a frame only moves its transform and changes its brush opacity. Re-drawing the text every frame cost ~1 ms per frame.
- The equalizer and the dots share one 20 fps clock.

Measured on a Release build, % of one core, from 3s to 23s after start (startup excluded), three runs each:

| Scenario | Runs | Average |
|---|---|---|
| A: 4 line changes in 20s | 2.77 / 2.57 / 3.14 | **2.83%** |
| B: 2 mode changes + 1 line change in 20s | 2.42 / 3.35 / 2.91 | **2.89%** |
| Paused, nothing moving | 1.41 / 1.26 / 1.74 | 1.47% |
| Interlude (equalizer + dots) | 2.18 / 2.38 / 2.56 | 2.37% |

Caveat: these runs happened while a game was running on the PC (about two cores busy, plus a screen recorder), so they're noisy and probably high. Milestone 3's 0.6% was measured on an idle PC. Measure again on an idle PC before the release.

Not checked yet: **DPI 125% / 150%** and changing the scale while the app runs (the PC was in use, and changing the scale disturbs everything on screen), and a second monitor (the PC has one). Possible line flicker right after a seek can't happen yet (the demo never seeks); check it in milestone 5.

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
