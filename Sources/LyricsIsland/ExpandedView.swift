import SwiftUI

/// Hover state: header, karaoke lyrics, progress and transport controls.
struct ExpandedView: View {
    @ObservedObject var model: IslandModel
    let scale: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            header
            ExpandedLyrics(model: model, scale: scale)
            ProgressRow(current: model.currentTime, duration: model.track?.duration ?? 0) { model.seek(fraction: $0) }
            controls
        }
        .padding(EdgeInsets(top: 18, leading: 20, bottom: 16, trailing: 20))
        .frame(width: 560)
    }

    private var header: some View {
        HStack(spacing: 12) {
            AlbumArt(image: model.artwork, size: 44, radius: 10)
                .onTapGesture { model.openPlayer() }
                .help("Open Spotify")
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: model.track?.name ?? "")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                Text(verbatim: model.track?.artist ?? "")
                    .font(.system(size: 12))
                    .foregroundStyle(.white.opacity(0.6))
            }
            .lineLimit(1)
            Spacer(minLength: 8)
            Equalizer(playing: model.playback == .playing)
        }
    }

    private var controls: some View {
        HStack(spacing: 0) {
            SourceChip(model: model)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 30) {
                IconButton(symbol: "backward.fill", size: 17) { model.previous() }
                IconButton(symbol: model.playback == .playing ? "pause.fill" : "play.fill", size: 22) { model.togglePlay() }
                IconButton(symbol: "forward.fill", size: 17) { model.next() }
            }
            OffsetControl(model: model)
                .frame(maxWidth: .infinity, alignment: .trailing)
        }
    }
}

struct ExpandedLyrics: View {
    @ObservedObject var model: IslandModel
    let scale: CGFloat

    var body: some View {
        Group {
            switch model.lyricsStatus {
            case .found(let lyrics):
                synced(lyrics)
            case .notFound:
                VStack(alignment: .leading, spacing: 12) {
                    Text("No lyrics found for this song.")
                        .font(.system(size: 17 * scale, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.6))
                    Button { model.onManualSearch?() } label: { Chip(text: "Search lyrics manually…") }
                        .buttonStyle(.plain)
                }
            case .idle, .loading:
                HStack(spacing: 10) {
                    Text("Finding lyrics…")
                        .font(.system(size: 17 * scale, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.6))
                    PulsingDots()
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .frame(minHeight: 118 * scale, alignment: .top)
    }

    /// Shows the previous line, the active line and the next two.
    private func synced(_ lyrics: Lyrics) -> some View {
        let idx = model.lineIndex
        let lo = max(0, (idx ?? 0) - 1)
        let hi = min(lyrics.lines.count - 1, idx.map { $0 + 2 } ?? 2)
        let visible = lo <= hi ? Array(lo...hi) : []
        return VStack(alignment: .leading, spacing: 8 * scale) {
            if idx == nil {
                PulsingDots(size: 7, spacing: 6).frame(height: 28 * scale)
            }
            ForEach(visible, id: \.self) { j in
                line(lyrics, j, current: idx)
                    .transition(.asymmetric(
                        insertion: .opacity.combined(with: .offset(y: 14)),
                        removal: .opacity.combined(with: .offset(y: -14))
                    ))
            }
        }
        .animation(.easeOut(duration: 0.3), value: idx)
    }

    private func line(_ lyrics: Lyrics, _ j: Int, current: Int?) -> some View {
        let line = lyrics.lines[j]
        return Group {
            if j == current {
                if line.isBlank {
                    PulsingDots(size: 7, spacing: 6).frame(height: 28 * scale, alignment: .leading)
                } else {
                    Karaoke.text(line.text, progress: model.lineProgress(j))
                        .font(.system(size: 22 * scale, weight: .bold))
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text(verbatim: line.isBlank ? "♪" : line.text)
                    .font(.system(size: 17 * scale, weight: .semibold))
                    .foregroundStyle(.white.opacity(dim(j, current)))
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
        .onTapGesture { model.seek(toLine: j) }
    }

    private func dim(_ j: Int, _ current: Int?) -> Double {
        let i = current ?? -1
        if j < i { return 0.3 }
        return j - i <= 1 ? 0.3 : 0.16
    }
}

struct ProgressRow: View {
    let current: Double
    let duration: Double
    let onSeek: (Double) -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text(verbatim: Self.format(current)).modifier(TimeStyle())
            GeometryReader { g in
                let fraction = duration > 0 ? min(1, max(0, current / duration)) : 0
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.white.opacity(0.14))
                    Capsule().fill(Color.islandGreen).frame(width: max(4, g.size.width * fraction))
                }
                .frame(height: 4)
                .frame(maxHeight: .infinity)
                .contentShape(Rectangle())
                .onTapGesture { location in onSeek(location.x / max(1, g.size.width)) }
            }
            .frame(height: 14)
            Text(verbatim: "-" + Self.format(max(0, duration - current))).modifier(TimeStyle())
        }
    }

    static func format(_ seconds: Double) -> String {
        let t = Int(seconds.rounded(.down))
        return String(format: "%d:%02d", t / 60, t % 60)
    }
}

struct TimeStyle: ViewModifier {
    func body(content: Content) -> some View {
        content
            .font(.system(size: 11, weight: .medium).monospacedDigit())
            .foregroundStyle(.white.opacity(0.6))
    }
}

struct SourceChip: View {
    @ObservedObject var model: IslandModel

    var body: some View {
        switch model.lyricsStatus {
        case .found(let lyrics):
            Chip(text: "Synced · \(lyrics.source)", dot: .islandGreen)
        case .notFound:
            Chip(text: "No lyrics", dot: .white.opacity(0.35))
        case .idle, .loading:
            Chip(text: "Finding lyrics…", dot: .white.opacity(0.35))
        }
    }
}

/// Nudges lyric timing for the current song: − if lyrics run ahead, + if they lag.
struct OffsetControl: View {
    @ObservedObject var model: IslandModel

    var body: some View {
        HStack(spacing: 4) {
            small("minus") { model.adjustOffset(-0.5) }
            Text(verbatim: model.offsetLabel)
                .font(.system(size: 11, weight: .medium).monospacedDigit())
                .foregroundStyle(.white.opacity(0.6))
            small("plus") { model.adjustOffset(0.5) }
        }
        .help("Adjust lyric timing: − if the lyrics run early, + if they lag behind")
    }

    private func small(_ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(.white.opacity(0.7))
                .frame(width: 18, height: 18)
                .background(Color.white.opacity(0.14), in: Circle())
        }
        .buttonStyle(.plain)
    }
}
