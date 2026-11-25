import AppKit

MainActor.assumeIsolated {
    let args = CommandLine.arguments
    let app = NSApplication.shared

    if let i = args.firstIndex(of: "--snapshot"), i + 1 < args.count {
        Snapshot.renderAll(to: URL(fileURLWithPath: args[i + 1]))
        exit(0)
    }

    if let i = args.firstIndex(of: "--diagnose"), i + 1 < args.count {
        // Writes what the app can see of the menu bar (run via `open … --args --diagnose <file>`).
        let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
        let owners = Set(windows.compactMap { $0[kCGWindowOwnerName as String] as? String })
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.title = "•"
        RunLoop.main.run(until: Date().addingTimeInterval(1))
        let rows = windows.map { w -> String in
            let b = w[kCGWindowBounds as String] as? [String: Any] ?? [:]
            func n(_ k: String) -> Int { (b[k] as? NSNumber)?.intValue ?? -1 }
            return "  layer \(w[kCGWindowLayer as String] ?? "?") \(w[kCGWindowOwnerName as String] ?? "?") pid \(w[kCGWindowOwnerPID as String] ?? "?") [\(n("X")),\(n("Y")) \(n("Width"))x\(n("Height"))]"
        }.joined(separator: "\n")
        let screen = NSScreen.screens.first!
        let front = NSWorkspace.shared.frontmostApplication
        let report = """
        frontmost: \(front?.localizedName ?? "nil") pid \(front?.processIdentifier ?? 0)
        screen frame: \(screen.frame) visible: \(screen.visibleFrame)
        menuBarVisible: \(NSMenu.menuBarVisible())
        \(rows)
        windows visible: \(windows.count) from \(owners.count) apps
        own status item x: \(item.button?.window?.frame.minX.description ?? "nil")
        scan: \(MenuBarScanner.scan(ownStatusItemX: item.button?.window?.frame.minX))
        """
        try? report.write(toFile: args[i + 1], atomically: true, encoding: .utf8)
        exit(0)
    }

    let delegate = AppDelegate(forceDemo: args.contains("--demo"))
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
