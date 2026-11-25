import SwiftUI
import AppKit

extension Color {
    static let islandGreen = Color(red: 30 / 255, green: 215 / 255, blue: 96 / 255)
}

/// Proposes at most `max` width to its child and takes the child's size (so pills hug short text).
struct CapWidth: Layout {
    var max: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let child = subviews.first else { return .zero }
        let width = Swift.min(proposal.width ?? max, max)
        return child.sizeThatFits(ProposedViewSize(width: width, height: proposal.height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
    }
}

struct AlbumArt: View {
    let image: NSImage?
    let size: CGFloat
    let radius: CGFloat

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                PlaceholderArt()
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
    }
}

/// Sunset-over-the-city placeholder shown until real artwork loads.
struct PlaceholderArt: View {
    var body: some View {
        GeometryReader { g in
            let w = g.size.width
            ZStack {
                LinearGradient(
                    colors: [Color(red: 0.98, green: 0.55, blue: 0.33), Color(red: 0.78, green: 0.16, blue: 0.42), Color(red: 0.18, green: 0.10, blue: 0.38)],
                    startPoint: .top, endPoint: .bottom
                )
                Circle()
                    .fill(Color(red: 1, green: 0.88, blue: 0.62).opacity(0.95))
                    .frame(width: w * 0.41, height: w * 0.41)
                    .position(x: w / 2, y: w * 0.48)
                Rectangle()
                    .fill(Color(red: 0.09, green: 0.05, blue: 0.18))
                    .frame(width: w, height: w * 0.3)
                    .position(x: w / 2, y: w * 0.85)
            }
        }
    }
}

/// Four bars bouncing at 20 fps. Heights are computed per frame (no layout animation) to keep CPU low.
struct Equalizer: View {
    var playing: Bool
    private static let speeds: [Double] = [7.1, 9.3, 6.2, 8.4]
    private static let phases: [Double] = [0, 1.7, 3.1, 4.6]

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 20, paused: !playing)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: 1.5) {
                ForEach(0..<4, id: \.self) { i in
                    Capsule()
                        .fill(Color.islandGreen)
                        .frame(width: 2.5, height: 14)
                        .scaleEffect(x: 1, y: playing ? Self.level(t, i) : 3 / 14, anchor: .center)
                }
            }
            .frame(width: 16, height: 14)
        }
    }

    /// 0.28…1 — two sines per bar so the motion doesn't look periodic.
    private static func level(_ t: Double, _ i: Int) -> CGFloat {
        let a = sin(t * speeds[i] + phases[i])
        let b = sin(t * speeds[(i + 2) % 4] * 0.53 + phases[i] * 2)
        return CGFloat(0.64 + 0.36 * (0.6 * a + 0.4 * b))
    }
}

struct PulsingDots: View {
    var size: CGFloat = 5
    var spacing: CGFloat = 4

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 20)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: spacing) {
                ForEach(0..<3, id: \.self) { i in
                    Circle()
                        .fill(Color.white)
                        .frame(width: size, height: size)
                        .opacity(0.25 + 0.75 * (0.5 + 0.5 * sin(t * 4 - Double(i) * 0.9)))
                }
            }
        }
    }
}

struct Chip: View {
    let text: String
    var dot: Color? = nil

    var body: some View {
        HStack(spacing: 5) {
            if let dot {
                Circle().fill(dot).frame(width: 6, height: 6)
            }
            Text(text)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.white.opacity(0.6))
                .lineLimit(1)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(Color.white.opacity(0.14), in: Capsule())
    }
}

struct IconButton: View {
    let symbol: String
    let size: CGFloat
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 30, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

enum Karaoke {
    /// Words already sung are bright, upcoming words dimmed.
    static func text(_ line: String, progress: Double) -> Text {
        let words = line.split(separator: " ")
        guard !words.isEmpty else { return Text(line) }
        let lit = Int((Double(words.count) * progress).rounded(.up))
        return words.enumerated().reduce(Text(verbatim: "")) { acc, item in
            let piece = Text(verbatim: (item.offset == 0 ? "" : " ") + String(item.element))
                .foregroundStyle(item.offset < lit ? Color.white : Color.white.opacity(0.42))
            return acc + piece
        }
    }
}
