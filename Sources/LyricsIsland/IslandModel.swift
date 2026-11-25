import AppKit
import SwiftUI

/// State machine behind the island: what is playing, which lyric line is active, and which state to show.
@MainActor
final class IslandModel: ObservableObject {
    enum LyricsStatus: Equatable {
        case idle, loading, found(Lyrics), notFound
    }

    @Published private(set) var track: Track?
    @Published private(set) var playback: PlaybackState = .stopped
    @Published private(set) var lyricsStatus: LyricsStatus = .idle
    @Published private(set) var artwork: NSImage?
    /// Not @Published: only the expanded view needs it every tick (see updateTime).
    private(set) var currentTime: Double = 0
    @Published private(set) var lineIndex: Int?
    @Published private(set) var expanded = false
    @Published private(set) var isFullscreen = false
    @Published private(set) var playerIssue: PlayerIssue?
    /// Compact pills never get wider than this, so they don't cover menu bar icons.
    @Published private(set) var maxPillWidth: CGFloat = MenuBarScanner.defaultMaxPillWidth
    /// Distance from the top of the screen to the pill (below the notch on notched MacBooks).
    @Published var topInset: CGFloat = 5

    /// Used by snapshot rendering to force a state.
    var forcedMode: IslandMode?
    /// Pill frame in window coordinates (top-left origin), reported by the view.
    var pillFrameInWindow: CGRect = .zero
    weak var panel: NSPanel?
    var onManualSearch: (() -> Void)?
    /// Screen x of our own menu bar icon (set by the app delegate).
    var statusItemMinX: (() -> CGFloat?)?
    /// Showcase mode drives hover from a script instead of the real cursor.
    var simulatedHover: Bool?
    /// Showcase mode ignores the real menu bar when sizing the pill.
    var fixedMaxPillWidth: CGFloat?

    let settings: Settings
    private var player: PlayerSource?
    private var snapshot = PlayerSnapshot.stopped
    private var songChangeUntil: Date?
    private var pausedSince: Date?
    private var notFoundSince: Date?
    private var hoverSince: Date?
    private var leaveSince: Date?
    private var timer: Timer?
    private var ticks = 0
    private var lyricsTask: Task<Void, Never>?
    private var artworkTask: Task<Void, Never>?

    init(settings: Settings) {
        self.settings = settings
    }

    // MARK: - Derived state

    var mode: IslandMode {
        if let forcedMode { return forcedMode }
        guard settings.showIsland, !(settings.hideInFullscreen && isFullscreen),
              track != nil, playback != .stopped else { return .hidden }
        if expanded { return .expanded }
        let now = Date()
        if playback == .paused {
            if let p = pausedSince, now.timeIntervalSince(p) > 10 { return .hidden }
            return .paused
        }
        if let until = songChangeUntil, now < until { return .songChange }
        switch lyricsStatus {
        case .idle, .loading:
            return .loading
        case .notFound:
            if let since = notFoundSince, now.timeIntervalSince(since) > 5 { return .mini }
            return .noLyrics
        case .found(let lyrics):
            if let i = lineIndex, !lyrics.lines[i].isBlank { return .lyric }
            return .interlude
        }
    }

    var currentLine: LyricLine? {
        guard case .found(let lyrics) = lyricsStatus, let i = lineIndex,
              lyrics.lines.indices.contains(i), !lyrics.lines[i].isBlank else { return nil }
        return lyrics.lines[i]
    }

    /// Seconds added to the playback position before looking up the lyric line.
    var offset: Double { settings.offset(for: track?.id) }

    var offsetLabel: String {
        let v = offset
        if abs(v) < 0.05 { return "±0.0s" }
        return String(format: "%+.1fs", v)
    }

    /// 0…1: how far into line `i` the singer is (used for karaoke highlighting).
    func lineProgress(_ i: Int) -> Double {
        guard case .found(let lyrics) = lyricsStatus, lyrics.lines.indices.contains(i) else { return 0 }
        let line = lyrics.lines[i]
        let nextTime = i + 1 < lyrics.lines.count ? lyrics.lines[i + 1].time : (track?.duration ?? line.time + 5)
        let gap = max(0.5, nextTime - line.time)
        let estimate = min(gap * 0.9, max(1.2, Double(line.text.count) * 0.085))
        return min(1, max(0, (currentTime + offset - line.time) / estimate))
    }

    // MARK: - Lifecycle

    func attach(_ newPlayer: PlayerSource) {
        player?.stop()
        player = newPlayer
        newPlayer.onUpdate = { [weak self] s in self?.apply(s) }
        newPlayer.onIssue = { [weak self] issue in
            guard let self, self.playerIssue != issue else { return }
            self.playerIssue = issue
        }
        apply(.stopped)
        newPlayer.start()
    }

    func start() {
        timer = repeatingTimer(0.1) { [weak self] in self?.tick() }
    }

    private func apply(_ s: PlayerSnapshot) {
        let now = Date()
        let trackChanged = s.track?.id != track?.id
        let smallDrift = !trackChanged && s.state == snapshot.state && s.state == .playing
            && abs(snapshot.position(at: now) - s.position(at: now)) < 0.35
        // Keep the smooth local clock unless the player really jumped.
        if !smallDrift { snapshot = s }

        if trackChanged {
            setTrack(s.track)
        } else if s.track != track {
            track = s.track
        }
        if s.state != playback {
            playback = s.state
            pausedSince = s.state == .paused ? now : nil
        }
        updateTime()
    }

    private func setTrack(_ t: Track?) {
        track = t
        lyricsTask?.cancel()
        artworkTask?.cancel()
        lineIndex = nil
        artwork = nil
        notFoundSince = nil
        guard let t else {
            lyricsStatus = .idle
            songChangeUntil = nil
            return
        }
        songChangeUntil = Date().addingTimeInterval(2.5)
        lyricsStatus = .loading
        lyricsTask = Task { [weak self] in
            let result = await LyricsService.shared.lyrics(for: t)
            guard let self, !Task.isCancelled, self.track?.id == t.id else { return }
            switch result {
            case .found(let lyrics):
                self.lyricsStatus = .found(lyrics)
            case .notFound, .failed:
                self.lyricsStatus = .notFound
                self.notFoundSince = Date()
            }
            self.updateTime()
        }
        if let url = t.artworkURL {
            artworkTask = Task { [weak self] in
                guard let (data, _) = try? await URLSession.shared.data(from: url),
                      let image = NSImage(data: data) else { return }
                guard let self, !Task.isCancelled, self.track?.id == t.id else { return }
                self.artwork = image
            }
        }
    }

    private func updateTime() {
        var t = snapshot.position(at: Date())
        if let d = track?.duration, d > 0 { t = min(t, d) }
        let timeMoved = abs(t - currentTime) > 0.0001
        currentTime = t
        // Progress bar + karaoke need ~10 fps, but only while the panel is open.
        if timeMoved, expanded || forcedMode == .expanded { objectWillChange.send() }

        var index: Int?
        if case .found(let lyrics) = lyricsStatus {
            let lyricTime = t + offset
            index = lyrics.lines.lastIndex { $0.time <= lyricTime }
        }
        if index != lineIndex { lineIndex = index }
    }

    private func tick() {
        ticks += 1
        updateTime()
        trackMouse()
        if ticks % 10 == 0 {
            let scan = MenuBarScanner.scan(ownStatusItemX: statusItemMinX?())
            if scan.fullscreen != isFullscreen { isFullscreen = scan.fullscreen }
            let width = fixedMaxPillWidth ?? scan.maxPillWidth
            if abs(width - maxPillWidth) >= 1 { maxPillWidth = width }
            // Re-evaluate time-based states (song change, pause timeout…) even when nothing else changed.
            objectWillChange.send()
        }
    }

    /// Hover detection by polling the cursor; also makes the panel click-through outside the pill.
    private func trackMouse() {
        guard let panel else { return }
        let now = Date()
        let wf = panel.frame
        let r = pillFrameInWindow
        let rect = CGRect(x: wf.minX + r.minX, y: wf.maxY - r.maxY, width: r.width, height: r.height)
            .insetBy(dx: -3, dy: -3)
        let inside = simulatedHover ?? (mode != .hidden && r.width > 0 && rect.contains(NSEvent.mouseLocation))
        if panel.ignoresMouseEvents == inside { panel.ignoresMouseEvents = !inside }

        if inside {
            leaveSince = nil
            if hoverSince == nil { hoverSince = now }
            if !expanded, settings.expandOnHover, let h = hoverSince, now.timeIntervalSince(h) >= 0.3 {
                expanded = true
            }
        } else {
            hoverSince = nil
            if expanded {
                if leaveSince == nil { leaveSince = now }
                if let l = leaveSince, now.timeIntervalSince(l) >= 1.0 {
                    expanded = false
                    leaveSince = nil
                }
            }
        }
    }

    // MARK: - Actions

    func expandNow() {
        expanded = true
        hoverSince = Date()
    }

    func togglePlay() { player?.playPause() }
    func next() { player?.next() }
    func previous() { player?.previous() }
    func openPlayer() { player?.activate() }

    func seek(fraction: Double) {
        guard let d = track?.duration, d > 0 else { return }
        player?.seek(to: d * min(1, max(0, fraction)))
    }

    func seek(toLine i: Int) {
        guard case .found(let lyrics) = lyricsStatus, lyrics.lines.indices.contains(i) else { return }
        player?.seek(to: max(0, lyrics.lines[i].time - offset + 0.05))
    }

    func adjustOffset(_ delta: Double) {
        guard let id = track?.id else { return }
        settings.setOffset(((offset + delta) * 10).rounded() / 10, for: id)
        updateTime()
    }

    func resetOffset() {
        guard let id = track?.id else { return }
        settings.setOffset(0, for: id)
        updateTime()
    }

    func setManualLyrics(_ lyrics: Lyrics, for trackID: String) {
        guard track?.id == trackID else { return }
        lyricsStatus = .found(lyrics)
        notFoundSince = nil
        updateTime()
    }

    /// Fills the model with the demo song frozen at `time` (for snapshot rendering).
    func loadPreview(time: Double) {
        let t = DemoPlayer.tracks[0]
        track = t
        playback = .playing
        lyricsStatus = DemoPlayer.lyrics(forTrackID: t.id).map { .found($0) } ?? .notFound
        snapshot = PlayerSnapshot(track: t, state: .paused, position: time, timestamp: Date())
        updateTime()
    }
}

/// Looks at on-screen windows (bounds only — no Screen Recording permission needed).
enum MenuBarScanner {
    struct Result {
        /// The frontmost app has a window covering the whole primary screen (menu bar hidden).
        var fullscreen: Bool
        /// Widest pill that still stays clear of the menu bar status icons.
        var maxPillWidth: CGFloat
    }

    static let defaultMaxPillWidth: CGFloat = 460

    /// `ownStatusItemX`: left edge of our own menu bar icon. New status items are inserted leftmost,
    /// so it is a permission-free estimate of where the status icons begin.
    @MainActor
    static func scan(ownStatusItemX: CGFloat? = nil) -> Result {
        guard let screen = NSScreen.screens.first else { return Result(fullscreen: false, maxPillWidth: defaultMaxPillWidth) }
        let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] ?? []
        let ownPID = ProcessInfo.processInfo.processIdentifier
        let frontPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let size = screen.frame.size
        let midX = screen.frame.midX
        var fullscreen = false
        var leftmostStatusX = CGFloat.infinity
        if let x = ownStatusItemX, x > midX - 40 { leftmostStatusX = x }

        for w in windows {
            guard let pid = (w[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value, pid != ownPID,
                  let layer = (w[kCGWindowLayer as String] as? NSNumber)?.intValue,
                  let b = w[kCGWindowBounds as String] as? [String: Any] else { continue }
            let x = CGFloat((b["X"] as? NSNumber)?.doubleValue ?? -1)
            let y = CGFloat((b["Y"] as? NSNumber)?.doubleValue ?? -1)
            let width = CGFloat((b["Width"] as? NSNumber)?.doubleValue ?? 0)
            let height = CGFloat((b["Height"] as? NSNumber)?.doubleValue ?? 0)

            // Note: macOS doesn't tell other apps whether the menu bar is currently revealed in a
            // fullscreen space, so this can't tell "fullscreen video" from "fullscreen editor".
            if layer == 0, pid == frontPID, pid != ownPID, x == 0, y == 0, width == size.width, height == size.height {
                fullscreen = true
            }
            // Menu bar extras: small windows at the status-bar level along the top edge, right of center.
            if layer == Int(CGWindowLevelForKey(.statusWindow)), y <= 2, height <= 44, width < 400, x > midX - 40 {
                leftmostStatusX = min(leftmostStatusX, x)
            }
        }

        var maxWidth = defaultMaxPillWidth
        if leftmostStatusX.isFinite {
            maxWidth = min(maxWidth, 2 * (leftmostStatusX - midX) - 16)
        }
        return Result(fullscreen: fullscreen, maxPillWidth: max(160, maxWidth))
    }
}
