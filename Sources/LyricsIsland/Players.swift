import AppKit
import Carbon

/// Something that plays music and reports what it is doing.
@MainActor
protocol PlayerSource: AnyObject {
    var onUpdate: ((PlayerSnapshot) -> Void)? { get set }
    var onIssue: ((PlayerIssue?) -> Void)? { get set }
    func start()
    func stop()
    func playPause()
    func next()
    func previous()
    func seek(to seconds: Double)
    func activate()
}

func repeatingTimer(_ interval: TimeInterval, _ block: @escaping @MainActor () -> Void) -> Timer {
    let timer = Timer(timeInterval: interval, repeats: true) { _ in
        MainActor.assumeIsolated { block() }
    }
    // .common so it keeps firing while the status menu is open.
    RunLoop.main.add(timer, forMode: .common)
    return timer
}

// MARK: - Spotify desktop (AppleScript)

@MainActor
final class SpotifyPlayer: PlayerSource {
    static let bundleID = "com.spotify.client"

    var onUpdate: ((PlayerSnapshot) -> Void)?
    var onIssue: ((PlayerIssue?) -> Void)?

    private var timer: Timer?
    private var observer: NSObjectProtocol?
    private var statusScript: NSAppleScript?
    private var installedCheckedAt: Date?
    private var installedCache = false
    private var granted = false
    private var askingPermission = false

    private static let statusSource = """
    with timeout of 2 seconds
        tell application id "com.spotify.client"
            set s to player state as string
            if s is "stopped" then return {s}
            set t to current track
            return {s, player position, id of t, name of t, artist of t, album of t, duration of t, artwork url of t}
        end tell
    end timeout
    """

    func start() {
        observer = DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name("com.spotify.client.PlaybackStateChanged"), object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer = repeatingTimer(1.0) { [weak self] in self?.poll() }
        poll()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        if let observer { DistributedNotificationCenter.default().removeObserver(observer) }
        observer = nil
    }

    private var isInstalled: Bool {
        if let t = installedCheckedAt, Date().timeIntervalSince(t) < 10 { return installedCache }
        installedCache = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.bundleID) != nil
        installedCheckedAt = Date()
        return installedCache
    }

    private var isRunning: Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleID).isEmpty
    }

    func poll() {
        guard isInstalled else { onIssue?(.notInstalled); onUpdate?(.stopped); return }
        // Never send Apple Events when Spotify is closed — that would launch it.
        guard isRunning else { onIssue?(nil); onUpdate?(.stopped); return }
        guard hasPermission() else { onUpdate?(.stopped); return }

        if statusScript == nil { statusScript = NSAppleScript(source: Self.statusSource) }
        guard let script = statusScript else { return }
        var error: NSDictionary?
        let result = script.executeAndReturnError(&error)
        if let error {
            let code = error[NSAppleScript.errorNumber] as? Int ?? 0
            // -1743: user denied Automation permission. -600: app quit meanwhile.
            onIssue?(code == -1743 ? .notAuthorized : nil)
            onUpdate?(.stopped)
            return
        }
        onIssue?(nil)
        onUpdate?(Self.snapshot(from: result))
    }

    /// Checks the Automation permission without blocking. If macOS hasn't asked yet, the
    /// "Lyrics Island wants to control Spotify" prompt is shown from a background thread —
    /// sending the first Apple Event on the main thread would freeze the app until it is answered.
    private func hasPermission() -> Bool {
        if granted { return true }
        if askingPermission { return false }
        switch Self.permissionStatus(ask: false) {
        case noErr:
            granted = true
            onIssue?(nil)
            return true
        case OSStatus(errAEEventWouldRequireUserConsent):
            askingPermission = true
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                let status = Self.permissionStatus(ask: true)
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    self.askingPermission = false
                    self.granted = status == noErr
                    self.onIssue?(self.granted ? nil : .notAuthorized)
                    self.poll()
                }
            }
            return false
        case OSStatus(errAEEventNotPermitted):
            onIssue?(.notAuthorized) // re-checked every second, so enabling it in Settings just works
            return false
        default:
            return false
        }
    }

    nonisolated private static func permissionStatus(ask: Bool) -> OSStatus {
        let target = NSAppleEventDescriptor(bundleIdentifier: "com.spotify.client")
        guard let desc = target.aeDesc else { return OSStatus(procNotFound) }
        return AEDeterminePermissionToAutomateTarget(desc, AEEventClass(typeWildCard), AEEventID(typeWildCard), ask)
    }

    static func snapshot(from d: NSAppleEventDescriptor) -> PlayerSnapshot {
        func str(_ i: Int) -> String { d.atIndex(i)?.stringValue ?? "" }
        func num(_ i: Int) -> Double {
            guard let x = d.atIndex(i) else { return 0 }
            if let f = x.coerce(toDescriptorType: DescType(typeIEEE64BitFloatingPoint)) { return f.doubleValue }
            return Double((x.stringValue ?? "").replacingOccurrences(of: ",", with: ".")) ?? 0
        }
        guard d.numberOfItems >= 8, str(1) != "stopped" else { return .stopped }
        let track = Track(
            id: str(3), name: str(4), artist: str(5), album: str(6),
            duration: num(7) / 1000, artworkURL: URL(string: str(8))
        )
        return PlayerSnapshot(
            track: track, state: str(1) == "playing" ? .playing : .paused,
            position: num(2), timestamp: Date()
        )
    }

    private func send(_ command: String) {
        guard isRunning, granted else { return }
        var error: NSDictionary?
        NSAppleScript(source: "with timeout of 2 seconds\ntell application id \"\(Self.bundleID)\" to \(command)\nend timeout")?
            .executeAndReturnError(&error)
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(150))
            self?.poll()
        }
    }

    func playPause() { send("playpause") }
    func next() { send("next track") }
    func previous() { send("previous track") }
    func seek(to seconds: Double) { send("set player position to \(String(format: "%.2f", max(0, seconds)))") }

    func activate() {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.bundleID) else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }
}

// MARK: - Demo (no Spotify needed)

@MainActor
final class DemoPlayer: PlayerSource {
    var onUpdate: ((PlayerSnapshot) -> Void)?
    var onIssue: ((PlayerIssue?) -> Void)?

    static let tracks: [Track] = [
        Track(id: "demo:streetlight-hymn", name: "Streetlight Hymn", artist: "Dusk Collective", album: "Late Hours", duration: 78, artworkURL: nil),
        Track(id: "demo:the-last-carriage", name: "The Last Carriage", artist: "Dusk Collective", album: "Late Hours", duration: 18, artworkURL: nil),
    ]

    // Fictional song written for the demo. The second demo track has no lyrics on purpose.
    private static let streetlightHymn = """
    [00:05.50]the road back home is long tonight
    [00:10.00]the city lights are fading slow
    [00:14.50]you said that everything's alright
    [00:19.50]but the night has never been this quiet
    [00:24.00]let this song keep you company
    [00:28.50]until the morning finds us here
    [00:33.00]
    [00:41.00]we stopped beneath the traffic light
    [00:45.50]counting dreams we couldn't buy
    [00:50.00]you laughed, and so did I
    [00:54.50]though tomorrow's still a haze
    [00:59.00]streetlights, please don't fade away
    [01:03.50]there's one more song to make you stay
    [01:08.00]
    """

    static func lyrics(forTrackID id: String) -> Lyrics? {
        guard id == "demo:streetlight-hymn" else { return nil }
        return Lyrics(lines: LRCParser.parse(streetlightHymn), source: "Demo")
    }

    private var index = 0
    private var playing = true
    private var base: Double = 0
    private var baseDate = Date()
    private var timer: Timer?

    private var position: Double { base + (playing ? Date().timeIntervalSince(baseDate) : 0) }

    init(startAt seconds: Double = 0) {
        base = seconds
    }

    func start() {
        baseDate = Date()
        onIssue?(nil)
        timer = repeatingTimer(0.5) { [weak self] in self?.emit() }
        emit()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func emit() {
        if position >= Self.tracks[index].duration { advance(by: 1); return }
        onUpdate?(PlayerSnapshot(track: Self.tracks[index], state: playing ? .playing : .paused, position: position, timestamp: Date()))
    }

    private func advance(by delta: Int) {
        index = (index + delta + Self.tracks.count) % Self.tracks.count
        base = 0
        baseDate = Date()
        playing = true
        emit()
    }

    func playPause() {
        base = position
        baseDate = Date()
        playing.toggle()
        emit()
    }

    func next() { advance(by: 1) }
    func previous() { position > 3 ? seek(to: 0) : advance(by: -1) }

    func seek(to seconds: Double) {
        base = max(0, seconds)
        baseDate = Date()
        emit()
    }

    func activate() {}
}
