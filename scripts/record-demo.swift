// Records the README demo GIF: screen-records the top-center of the main screen while
// LyricsIsland runs in --showcase mode, then converts the video to docs/demo.gif.
//
//   swift scripts/record-demo.swift build.noindex/LyricsIsland.app docs/demo.gif
//
// Needs Screen Recording permission for the terminal you run it from. No ffmpeg required.
import AppKit
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

let args = CommandLine.arguments
guard args.count >= 3 else {
    print("usage: swift scripts/record-demo.swift <LyricsIsland.app> <out.gif>")
    exit(1)
}
let appPath = args[1]
let outURL = URL(fileURLWithPath: args[2])
let canvas = CGSize(width: 760, height: 330)  // keep in sync with ShowcaseController.canvas
let showcaseSeconds = 20.0                    // keep in sync with ShowcaseController.duration
let fps = 12.0
let trimStart = 1.6                           // app launch before the island appears

let screen = NSScreen.screens.first!.frame
let rect = "\(Int(screen.midX - canvas.width / 2)),0,\(Int(canvas.width)),\(Int(canvas.height))"
let movURL = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lyrics-island-demo.mov")
try? FileManager.default.removeItem(at: movURL)

func run(_ path: String, _ arguments: [String]) -> Process {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = arguments
    try! p.run()
    return p
}

print("● recording \(rect) for \(Int(showcaseSeconds))s…")
let recorder = run("/usr/sbin/screencapture", ["-x", "-v", "-V\(Int(showcaseSeconds + 1))", "-R\(rect)", movURL.path])
Thread.sleep(forTimeInterval: 0.4)
_ = run("/usr/bin/open", ["-n", appPath, "--args", "--showcase"])
recorder.waitUntilExit()

guard FileManager.default.fileExists(atPath: movURL.path) else {
    print("✗ no recording — does this terminal have Screen Recording permission?")
    exit(1)
}

print("● converting to GIF…")
let asset = AVURLAsset(url: movURL)
let generator = AVAssetImageGenerator(asset: asset)
generator.appliesPreferredTrackTransform = true
generator.requestedTimeToleranceBefore = .zero
generator.requestedTimeToleranceAfter = .zero
generator.maximumSize = canvas

let length = CMTimeGetSeconds(asset.duration)
let end = min(length, trimStart + showcaseSeconds - 0.4)
let count = Int((end - trimStart) * fps)

guard let destination = CGImageDestinationCreateWithURL(outURL as CFURL, UTType.gif.identifier as CFString, count, nil) else {
    print("✗ can't write \(outURL.path)")
    exit(1)
}
CGImageDestinationSetProperties(destination, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
let frameProps = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: 1 / fps]] as CFDictionary

var written = 0
for i in 0..<count {
    let time = CMTime(seconds: trimStart + Double(i) / fps, preferredTimescale: 600)
    if let image = try? generator.copyCGImage(at: time, actualTime: nil) {
        CGImageDestinationAddImage(destination, image, frameProps)
        written += 1
    }
}
CGImageDestinationFinalize(destination)
try? FileManager.default.removeItem(at: movURL)

let bytes = (try? FileManager.default.attributesOfItem(atPath: outURL.path)[.size] as? Int) ?? 0
print("✓ \(outURL.path) — \(written) frames, \(bytes / 1024) KB")
