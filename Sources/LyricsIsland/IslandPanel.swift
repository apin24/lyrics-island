import AppKit
import SwiftUI

/// Lets buttons react to the first click even though the panel never becomes key.
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Transparent, borderless panel pinned to the top center of the main screen, above the menu bar.
final class IslandPanel: NSPanel {
    static let size = NSSize(width: 760, height: 420)

    init<Content: View>(rootView: Content) {
        super.init(
            contentRect: NSRect(origin: .zero, size: Self.size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isFloatingPanel = true
        level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        isMovable = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        // The model turns this off only while the cursor is over the pill.
        ignoresMouseEvents = true

        let host = FirstMouseHostingView(rootView: rootView)
        host.sizingOptions = []
        contentView = host
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    /// Moves the panel to the top center of the menu-bar screen and returns how far from the top
    /// the pill should sit: just below the camera housing on notched MacBooks, 5 pt otherwise.
    @discardableResult
    func reposition() -> CGFloat {
        guard let screen = NSScreen.screens.first else { return 5 }
        let f = screen.frame
        setFrame(
            NSRect(x: f.midX - Self.size.width / 2, y: f.maxY - Self.size.height, width: Self.size.width, height: Self.size.height),
            display: true
        )
        let notch = screen.safeAreaInsets.top
        return notch > 0 ? notch + 6 : 5
    }
}
