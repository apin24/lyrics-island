import AppKit
import SwiftUI
import ServiceManagement
import Carbon.HIToolbox

/// NSMenuItem that runs a closure.
final class ActionItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, key: String = "", modifiers: NSEvent.ModifierFlags = [.command], state on: Bool? = nil,
         handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: key)
        target = self
        keyEquivalentModifierMask = modifiers
        if let on { state = on ? .on : .off }
    }

    required init(coder: NSCoder) { fatalError("not used") }

    @objc private func fire() { handler() }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let settings = Settings.shared
    private let forceDemo: Bool
    private let showcase: Bool
    private var model: IslandModel!
    private var panel: IslandPanel!
    private var statusItem: NSStatusItem!
    private var hotKey: HotKey?
    private var showcaseController: ShowcaseController?

    init(forceDemo: Bool, showcase: Bool = false) {
        self.forceDemo = forceDemo || showcase
        self.showcase = showcase
        super.init()
    }

    private var useDemo: Bool { forceDemo || settings.demoMode }

    func applicationDidFinishLaunching(_ notification: Notification) {
        model = IslandModel(settings: settings)
        model.onManualSearch = { [weak self] in self?.manualSearch() }

        panel = IslandPanel(rootView: IslandView(model: model, settings: settings))
        model.panel = panel
        model.topInset = panel.reposition()
        panel.orderFrontRegardless()
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.model.topInset = self.panel.reposition()
            }
        }

        setupStatusItem()
        model.statusItemMinX = { [weak self] in self?.statusItem.button?.window?.frame.minX }
        hotKey = HotKey(keyCode: UInt32(kVK_ANSI_L), modifiers: UInt32(cmdKey | optionKey)) { [weak self] in
            MainActor.assumeIsolated { self?.settings.showIsland.toggle() }
        }

        connectPlayer()
        model.start()

        if showcase {
            showcaseController = ShowcaseController(model: model, panel: panel)
            showcaseController?.run()
        }
    }

    private func connectPlayer() {
        if showcase {
            model.attach(DemoPlayer(startAt: 3))
        } else {
            model.attach(useDemo ? DemoPlayer() : SpotifyPlayer())
        }
    }

    // MARK: - Status menu

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            let image = NSImage(systemSymbolName: "quote.bubble", accessibilityDescription: "Lyrics Island")
            image?.isTemplate = true
            button.image = image
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let header = NSMenuItem(title: "Lyrics Island", action: nil, keyEquivalent: "")
        header.attributedTitle = NSAttributedString(string: "Lyrics Island", attributes: [.font: NSFont.boldSystemFont(ofSize: 13)])
        header.isEnabled = false
        menu.addItem(header)

        let nowPlaying: String
        if let t = model.track, model.playback != .stopped {
            nowPlaying = "Now playing: \(t.name) — \(t.artist)"
        } else {
            nowPlaying = useDemo ? "Demo mode" : "Spotify isn't playing"
        }
        let np = NSMenuItem(title: nowPlaying, action: nil, keyEquivalent: "")
        np.isEnabled = false
        menu.addItem(np)

        if !useDemo {
            switch model.playerIssue {
            case .notInstalled:
                menu.addItem(ActionItem("⚠︎ Spotify desktop app not installed — Download…") {
                    NSWorkspace.shared.open(URL(string: "https://www.spotify.com/download/mac/")!)
                })
            case .notAuthorized:
                menu.addItem(ActionItem("⚠︎ Allow Spotify Control (Privacy › Automation)…") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation")!)
                })
            case nil:
                break
            }
        }

        menu.addItem(.separator())
        menu.addItem(ActionItem("Show Island", key: "l", modifiers: [.option, .command], state: settings.showIsland) { [weak self] in
            self?.settings.showIsland.toggle()
        })
        menu.addItem(ActionItem("Expand on Hover", state: settings.expandOnHover) { [weak self] in
            self?.settings.expandOnHover.toggle()
        })
        menu.addItem(ActionItem("Hide When an App Is Fullscreen", state: settings.hideInFullscreen) { [weak self] in
            self?.settings.hideInFullscreen.toggle()
        })

        let sizeItem = NSMenuItem(title: "Text Size", action: nil, keyEquivalent: "")
        let sizeMenu = NSMenu()
        for size in TextSize.allCases {
            sizeMenu.addItem(ActionItem(size.label, state: settings.textSize == size) { [weak self] in
                self?.settings.textSize = size
            })
        }
        sizeItem.submenu = sizeMenu
        menu.addItem(sizeItem)

        let offsetItem = NSMenuItem(title: "Lyrics Timing (\(model.offsetLabel))", action: nil, keyEquivalent: "")
        let offsetMenu = NSMenu()
        offsetMenu.addItem(ActionItem("Lyrics Too Early (−0.5s)") { [weak self] in self?.model.adjustOffset(-0.5) })
        offsetMenu.addItem(ActionItem("Lyrics Too Late (+0.5s)") { [weak self] in self?.model.adjustOffset(0.5) })
        offsetMenu.addItem(ActionItem("Reset") { [weak self] in self?.model.resetOffset() })
        offsetItem.submenu = offsetMenu
        offsetItem.isEnabled = model.track != nil
        menu.addItem(offsetItem)

        menu.addItem(.separator())
        let search = ActionItem("Search Lyrics Manually…") { [weak self] in self?.manualSearch() }
        search.isEnabled = model.track != nil && !useDemo
        menu.addItem(search)
        let demo = ActionItem("Demo Mode (No Spotify Needed)", state: useDemo) { [weak self] in self?.toggleDemo() }
        demo.isEnabled = !forceDemo
        menu.addItem(demo)

        menu.addItem(.separator())
        menu.addItem(ActionItem("Open at Login", state: SMAppService.mainApp.status == .enabled) { [weak self] in
            self?.toggleLoginItem()
        })
        menu.addItem(ActionItem("Quit Lyrics Island", key: "q") { NSApp.terminate(nil) })
    }

    // MARK: - Actions

    private func toggleDemo() {
        settings.demoMode.toggle()
        connectPlayer()
    }

    private func toggleLoginItem() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            info("Couldn't change Open at Login", error.localizedDescription)
        }
    }

    private func manualSearch() {
        guard let track = model.track else { return }
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Search Lyrics"
        alert.informativeText = "Type a title and artist. The top LRCLIB result with synced lyrics will be used for this song."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.stringValue = "\(track.name) \(LyricsService.primaryArtist(track.artist))"
        alert.accessoryView = field
        alert.addButton(withTitle: "Search")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        let query = field.stringValue.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return }

        Task { @MainActor in
            do {
                if let raw = try await LyricsService.shared.search(query: query) {
                    let lyrics = LyricsService.shared.storeManual(raw, for: track)
                    self.model.setManualLyrics(lyrics, for: track.id)
                } else {
                    self.info("No Lyrics Found", "LRCLIB has no synced lyrics for “\(query)”.")
                }
            } catch {
                self.info("Lyrics Search Failed", error.localizedDescription)
            }
        }
    }

    private func info(_ title: String, _ message: String) {
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }
}
