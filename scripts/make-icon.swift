// Draws the app icon and writes Resources/AppIcon.icns (+ docs/icon.png for the README).
//
//   swift scripts/make-icon.swift
//
// Layout follows the macOS icon grid: an 824×824 rounded square centered on a 1024×1024 canvas.
import SwiftUI
import AppKit

let sunsetTop = Color(red: 0.17, green: 0.11, blue: 0.38)
let sunsetMid = Color(red: 0.49, green: 0.18, blue: 0.49)
let sunsetLow = Color(red: 0.96, green: 0.50, blue: 0.37)
let green = Color(red: 30 / 255, green: 215 / 255, blue: 96 / 255)

struct MiniArt: View {
    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.98, green: 0.55, blue: 0.33), Color(red: 0.78, green: 0.16, blue: 0.42), Color(red: 0.18, green: 0.10, blue: 0.38)],
                           startPoint: .top, endPoint: .bottom)
            Circle().fill(Color(red: 1, green: 0.88, blue: 0.62)).frame(width: 44, height: 44).offset(y: -4)
            Rectangle().fill(Color(red: 0.09, green: 0.05, blue: 0.18)).frame(height: 32).frame(maxHeight: .infinity, alignment: .bottom)
        }
        .frame(width: 104, height: 104)
        .clipShape(RoundedRectangle(cornerRadius: 30, style: .continuous))
    }
}

struct Pill: View {
    var body: some View {
        HStack(spacing: 0) {
            MiniArt()
            Spacer()
            HStack(alignment: .center, spacing: 12) {
                ForEach([44.0, 82.0, 56.0, 94.0], id: \.self) { h in
                    Capsule().fill(green).frame(width: 16, height: h)
                }
            }
        }
        .padding(.leading, 23)
        .padding(.trailing, 44)
        .frame(width: 600, height: 150)
        .background(Capsule().fill(Color.black))
        .overlay(Capsule().strokeBorder(Color.white.opacity(0.09), lineWidth: 3))
        .shadow(color: .black.opacity(0.45), radius: 22, y: 14)
    }
}

struct LyricBars: View {
    var body: some View {
        VStack(spacing: 34) {
            HStack(spacing: 16) {   // current line, half "sung" like the karaoke view
                Capsule().fill(Color.white).frame(width: 300, height: 46)
                Capsule().fill(Color.white.opacity(0.42)).frame(width: 190, height: 46)
            }
            Capsule().fill(Color.white.opacity(0.30)).frame(width: 430, height: 40)
            Capsule().fill(Color.white.opacity(0.16)).frame(width: 320, height: 40)
        }
    }
}

struct AppIcon: View {
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .fill(LinearGradient(colors: [sunsetTop, sunsetMid, sunsetLow], startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(
                    RadialGradient(colors: [Color.white.opacity(0.16), .clear], center: .init(x: 0.5, y: 0.0), startRadius: 0, endRadius: 520)
                        .clipShape(RoundedRectangle(cornerRadius: 185, style: .continuous))
                )
                .overlay(RoundedRectangle(cornerRadius: 185, style: .continuous).strokeBorder(Color.white.opacity(0.10), lineWidth: 2))
                .frame(width: 824, height: 824)
                .shadow(color: .black.opacity(0.30), radius: 12, y: 8)

            VStack(spacing: 84) {
                Pill()
                LyricBars()
            }
            .offset(y: -12)
        }
        .frame(width: 1024, height: 1024)
    }
}

@MainActor
func render(_ size: Int) -> NSBitmapImageRep {
    let renderer = ImageRenderer(content: AppIcon())
    renderer.scale = 1
    let full = renderer.cgImage!
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    let context = NSGraphicsContext(bitmapImageRep: rep)!
    context.imageInterpolation = .high
    NSGraphicsContext.current = context
    NSImage(cgImage: full, size: NSSize(width: 1024, height: 1024)).draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

func write(_ rep: NSBitmapImageRep, _ path: String) {
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().path
let iconset = NSTemporaryDirectory() + "AppIcon.iconset"
try? FileManager.default.removeItem(atPath: iconset)
try! FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)

MainActor.assumeIsolated {
    for base in [16, 32, 128, 256, 512] {
        write(render(base), "\(iconset)/icon_\(base)x\(base).png")
        write(render(base * 2), "\(iconset)/icon_\(base)x\(base)@2x.png")
    }
    write(render(256), "\(root)/docs/icon.png")
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset, "-o", "\(root)/Resources/AppIcon.icns"]
try! iconutil.run()
iconutil.waitUntilExit()
print(iconutil.terminationStatus == 0 ? "✓ Resources/AppIcon.icns, docs/icon.png" : "✗ iconutil failed")
