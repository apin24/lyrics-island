import SwiftUI

/// Root view inside the transparent panel. Draws the pill at the top center.
struct IslandView: View {
    @ObservedObject var model: IslandModel
    @ObservedObject var settings: Settings

    var body: some View {
        let mode = model.mode
        VStack(spacing: 0) {
            if mode != .hidden {
                pill(mode)
                    .transition(.scale(scale: 0.4, anchor: .top).combined(with: .opacity))
            }
        }
        .padding(.top, model.topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(.spring(response: 0.42, dampingFraction: 0.82), value: mode)
        .animation(.spring(response: 0.35, dampingFraction: 0.86), value: model.lineIndex)
        .animation(.spring(response: 0.35, dampingFraction: 0.86), value: settings.textSize)
    }

    private func radius(_ mode: IslandMode) -> CGFloat {
        switch mode {
        case .expanded: 32
        case .songChange: 26
        default: 17
        }
    }

    private func pill(_ mode: IslandMode) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius(mode), style: .continuous)
        let isExpanded = mode == .expanded
        return content(mode)
            .clipShape(shape)
            // Shadow lives on a separate static shape so animating content doesn't re-blur it every frame.
            .background {
                shape.fill(Color.black)
                    .shadow(color: .black.opacity(isExpanded ? 0.45 : 0.35), radius: isExpanded ? 25 : 12, y: isExpanded ? 20 : 8)
            }
            .overlay(shape.strokeBorder(Color.white.opacity(0.09), lineWidth: 1))
            .contentShape(shape)
            .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { frame in
                model.pillFrameInWindow = frame
            }
            .onTapGesture {
                if !isExpanded { model.expandNow() }
            }
    }

    @ViewBuilder
    private func content(_ mode: IslandMode) -> some View {
        let scale = settings.textSize.scale
        let playing = model.playback == .playing
        switch mode {
        case .expanded:
            ExpandedView(model: model, scale: scale)
        case .songChange:
            SongChangeView(model: model)
        case .loading:
            CompactRow(trailing: 14) {
                art
                Text("Finding lyrics…")
                    .font(.system(size: 13 * scale, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.6))
                PulsingDots()
            }
        case .lyric:
            CompactRow {
                art
                CompactLyric(model: model, scale: scale)
                Equalizer(playing: playing)
            }
        case .interlude:
            CompactRow {
                art
                PulsingDots(size: 7, spacing: 6)
                Equalizer(playing: playing)
            }
        case .noLyrics:
            CompactRow(trailing: 6) {
                art
                CapWidth(max: max(80, min(300, model.maxPillWidth - 150))) {
                    (Text(verbatim: model.track?.name ?? "").foregroundStyle(.white)
                        + Text(verbatim: " · " + (model.track?.artist ?? "")).foregroundStyle(.white.opacity(0.6)))
                        .font(.system(size: 13 * scale, weight: .semibold))
                        .lineLimit(1)
                }
                Chip(text: "No lyrics")
            }
        case .mini:
            CompactRow {
                art
                Equalizer(playing: playing)
            }
        case .paused:
            CompactRow(trailing: 14) {
                art.opacity(0.55)
                Image(systemName: "pause.fill")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.white.opacity(0.6))
                Text("Paused")
                    .font(.system(size: 13 * scale, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.6))
            }
        case .hidden:
            EmptyView()
        }
    }

    private var art: some View {
        AlbumArt(image: model.artwork, size: 22, radius: 6)
    }
}

struct CompactRow<Content: View>: View {
    var trailing: CGFloat = 12
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: 10) { content }
            .padding(.leading, 6)
            .padding(.trailing, trailing)
            .frame(height: 34)
    }
}

/// Current lyric line; the old line slides up and out, the new one slides in from below.
struct CompactLyric: View {
    @ObservedObject var model: IslandModel
    let scale: CGFloat

    var body: some View {
        // Pill = 6 + art 22 + 10 + text + 10 + equalizer 16 + 12 → 76 pt besides the text.
        CapWidth(max: max(100, min(380 * scale, model.maxPillWidth - 76))) {
            ZStack(alignment: .leading) {
                if let line = model.currentLine {
                    Text(verbatim: line.text)
                        .font(.system(size: 13 * scale, weight: .semibold))
                        .foregroundStyle(.white)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .id(line.id)
                        .transition(.asymmetric(
                            insertion: .move(edge: .bottom).combined(with: .opacity),
                            removal: .move(edge: .top).combined(with: .opacity)
                        ))
                }
            }
            .frame(height: 18 * scale)
            .clipped()
        }
    }
}

struct SongChangeView: View {
    @ObservedObject var model: IslandModel

    var body: some View {
        HStack(spacing: 12) {
            AlbumArt(image: model.artwork, size: 36, radius: 9)
            CapWidth(max: 260) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(verbatim: model.track?.name ?? "")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(.white)
                    Text(verbatim: model.track?.artist ?? "")
                        .font(.system(size: 12))
                        .foregroundStyle(.white.opacity(0.6))
                }
                .lineLimit(1)
            }
            Color.clear.frame(width: 28, height: 1)
            Equalizer(playing: model.playback == .playing)
        }
        .padding(.leading, 8)
        .padding(.trailing, 16)
        .frame(height: 52)
    }
}
