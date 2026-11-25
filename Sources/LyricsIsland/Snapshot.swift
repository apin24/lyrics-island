import SwiftUI
import AppKit

/// `LyricsIsland --snapshot <dir>` renders every island state to PNG (handy for checking the UI without a screen).
@MainActor
enum Snapshot {
    static func renderAll(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let settings = Settings.shared
        let model = IslandModel(settings: settings)
        model.loadPreview(time: 16.2)

        let modes: [IslandMode] = [.lyric, .songChange, .loading, .interlude, .noLyrics, .mini, .paused, .expanded]
        for mode in modes {
            model.forcedMode = mode
            let height: CGFloat = mode == .expanded ? 360 : 110
            let renderer = ImageRenderer(content: SnapshotCanvas(model: model, settings: settings).frame(width: 720, height: height))
            renderer.scale = 2
            guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
                  let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                print("✗ \(mode.rawValue)")
                continue
            }
            let url = dir.appendingPathComponent("\(mode.rawValue).png")
            do {
                try png.write(to: url)
                print("✓ \(url.path)")
            } catch {
                print("✗ \(mode.rawValue): \(error.localizedDescription)")
            }
        }
    }
}

private struct SnapshotCanvas: View {
    @ObservedObject var model: IslandModel
    @ObservedObject var settings: Settings

    var body: some View {
        ZStack(alignment: .top) {
            LinearGradient(
                colors: [Color(red: 0.12, green: 0.09, blue: 0.30), Color(red: 0.42, green: 0.16, blue: 0.45), Color(red: 0.93, green: 0.48, blue: 0.36)],
                startPoint: .topLeading, endPoint: .bottomTrailing
            )
            Color.white.opacity(0.16).frame(height: 24)
            IslandView(model: model, settings: settings)
        }
    }
}
