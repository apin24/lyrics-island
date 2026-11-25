import AppKit
import SwiftUI

/// `--showcase`: plays a scripted demo over a clean fake wallpaper + menu bar, with an animated
/// fake cursor that "hovers" the island. Used to record the README GIF (see scripts/record-demo.sh).
@MainActor
final class ShowcaseController {
    static let canvas = NSSize(width: 760, height: 330)
    static let duration: TimeInterval = 20

    private let model: IslandModel
    private let backdrop: NSPanel
    private let cursor: NSPanel
    private let origin: CGPoint // top-left of the canvas in screen coordinates (y up)

    init(model: IslandModel, panel: NSPanel) {
        self.model = model
        let screen = NSScreen.screens.first!.frame
        origin = CGPoint(x: screen.midX - Self.canvas.width / 2, y: screen.maxY)

        backdrop = Self.makePanel(
            NSRect(x: origin.x, y: origin.y - Self.canvas.height, width: Self.canvas.width, height: Self.canvas.height),
            level: NSWindow.Level.statusBar.rawValue + 2,
            content: ShowcaseBackdrop()
        )
        cursor = Self.makePanel(NSRect(x: 0, y: 0, width: 24, height: 32), level: NSWindow.Level.statusBar.rawValue + 4, content: FakeCursor())
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 3)
    }

    func run() {
        backdrop.orderFrontRegardless()
        moveCursor(to: CGPoint(x: 600, y: 270), animated: false)
        cursor.orderFrontRegardless()
        model.simulatedHover = false
        model.fixedMaxPillWidth = MenuBarScanner.defaultMaxPillWidth

        after(8.0) { self.moveCursor(to: CGPoint(x: 395, y: 24), animated: true) }
        after(8.8) { self.model.simulatedHover = true }
        after(15.0) {
            self.moveCursor(to: CGPoint(x: 590, y: 280), animated: true)
            self.model.simulatedHover = false
        }
        after(Self.duration) { NSApp.terminate(nil) }
    }

    private func after(_ seconds: TimeInterval, _ block: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(seconds))
            block()
        }
    }

    /// `point` is in canvas coordinates (top-left origin). The arrow tip sits at (2, 2) in the cursor window.
    private func moveCursor(to point: CGPoint, animated: Bool) {
        let frame = NSRect(x: origin.x + point.x - 2, y: origin.y - point.y - 30, width: 24, height: 32)
        if animated {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.8
                context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                cursor.animator().setFrame(frame, display: true)
            }
        } else {
            cursor.setFrame(frame, display: true)
        }
    }

    private static func makePanel<V: View>(_ frame: NSRect, level: Int, content: V) -> NSPanel {
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = NSWindow.Level(rawValue: level)
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.contentView = NSHostingView(rootView: content)
        return panel
    }
}

private struct ShowcaseBackdrop: View {
    var body: some View {
        ZStack(alignment: .top) {
            // Flat color: GIFs only have 256 colors, so gradients turn into visible bands.
            Color(red: 0.25, green: 0.16, blue: 0.43)
            HStack {
                HStack(spacing: 18) {
                    Text("\u{F8FF}").font(.system(size: 15, weight: .semibold))
                    Text("Finder").font(.system(size: 13, weight: .bold))
                    Text("File").font(.system(size: 13))
                }
                Spacer()
                Text("Thu 9:41 PM").font(.system(size: 13, weight: .medium))
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 14)
            .frame(height: 24)
            .background(Color.white.opacity(0.16))
        }
    }
}

private struct FakeCursor: View {
    var body: some View {
        let arrow = Path { p in
            p.move(to: CGPoint(x: 2, y: 2))
            p.addLine(to: CGPoint(x: 2, y: 24.5))
            p.addLine(to: CGPoint(x: 8, y: 18.9))
            p.addLine(to: CGPoint(x: 12, y: 27.9))
            p.addLine(to: CGPoint(x: 15.6, y: 26.3))
            p.addLine(to: CGPoint(x: 11.6, y: 17.5))
            p.addLine(to: CGPoint(x: 20, y: 17.5))
            p.closeSubpath()
        }
        ZStack {
            arrow.fill(Color.black)
            arrow.stroke(Color.white, style: StrokeStyle(lineWidth: 1.6, lineJoin: .round))
        }
        .frame(width: 24, height: 32, alignment: .topLeading)
        .shadow(color: .black.opacity(0.3), radius: 2, y: 1)
    }
}
