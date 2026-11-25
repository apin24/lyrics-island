import Foundation

struct Track: Equatable, Sendable {
    var id: String
    var name: String
    var artist: String
    var album: String
    /// Seconds.
    var duration: Double
    var artworkURL: URL?
}

enum PlaybackState: Equatable, Sendable {
    case playing, paused, stopped
}

/// Player position at a moment in time. Between polls the position is extrapolated locally.
struct PlayerSnapshot: Equatable, Sendable {
    var track: Track?
    var state: PlaybackState
    var position: Double
    var timestamp: Date

    func position(at date: Date) -> Double {
        state == .playing ? position + date.timeIntervalSince(timestamp) : position
    }

    static let stopped = PlayerSnapshot(track: nil, state: .stopped, position: 0, timestamp: Date())
}

enum PlayerIssue: Equatable, Sendable {
    case notInstalled
    case notAuthorized
}

struct LyricLine: Identifiable, Equatable, Sendable {
    let id: Int
    let time: Double
    let text: String

    var isBlank: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
}

struct Lyrics: Equatable, Sendable {
    var lines: [LyricLine]
    var source: String
}

enum IslandMode: String, Equatable, CaseIterable {
    case hidden, songChange, loading, lyric, interlude, noLyrics, mini, paused, expanded
}
