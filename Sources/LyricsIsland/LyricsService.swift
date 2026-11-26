import Foundation

/// Fetches time-synced lyrics from LRCLIB (https://lrclib.net) and caches them.
@MainActor
final class LyricsService {
    static let shared = LyricsService()

    enum Result {
        case found(Lyrics)
        case notFound
        case failed
    }

    private var memory: [String: Lyrics] = [:]
    private var missing: Set<String> = []
    private let cacheDir: URL
    private let session: URLSession

    private init() {
        cacheDir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LyricsIsland", isDirectory: true)
        try? FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 12
        config.httpAdditionalHeaders = ["User-Agent": "LyricsIsland/0.1 (macOS)"]
        session = URLSession(configuration: config)
    }

    func lyrics(for track: Track) async -> Result {
        if let demo = DemoPlayer.lyrics(forTrackID: track.id) { return .found(demo) }
        if track.id.hasPrefix("demo:") { return .notFound }
        if let cached = memory[track.id] { return .found(cached) }
        if missing.contains(track.id) { return .notFound }

        if let raw = try? String(contentsOf: fileURL(track.id), encoding: .utf8) {
            let lyrics = Lyrics(lines: LRCParser.parse(raw), source: "LRCLIB")
            if !lyrics.lines.isEmpty {
                memory[track.id] = lyrics
                return .found(lyrics)
            }
        }

        do {
            var raw = try await fetchExact(track)
            if raw == nil { raw = try await fetchSearch(track) }
            if let raw {
                let lines = LRCParser.parse(raw)
                if !lines.isEmpty {
                    try? raw.write(to: fileURL(track.id), atomically: true, encoding: .utf8)
                    let lyrics = Lyrics(lines: lines, source: "LRCLIB")
                    memory[track.id] = lyrics
                    return .found(lyrics)
                }
            }
            missing.insert(track.id)
            return .notFound
        } catch {
            return .failed
        }
    }

    /// Free-text search; returns the top result that has synced lyrics.
    func search(query: String) async throws -> String? {
        let records = try await get("/api/search", [URLQueryItem(name: "q", value: query)], type: [LRCLibRecord].self)
        return records?.first { !($0.syncedLyrics ?? "").isEmpty }?.syncedLyrics
    }

    func storeManual(_ raw: String, for track: Track) -> Lyrics {
        try? raw.write(to: fileURL(track.id), atomically: true, encoding: .utf8)
        let lyrics = Lyrics(lines: LRCParser.parse(raw), source: "LRCLIB")
        memory[track.id] = lyrics
        missing.remove(track.id)
        return lyrics
    }

    // MARK: - LRCLIB

    private func fetchExact(_ t: Track) async throws -> String? {
        var items = [
            URLQueryItem(name: "track_name", value: t.name),
            URLQueryItem(name: "artist_name", value: t.artist),
        ]
        if !t.album.isEmpty { items.append(URLQueryItem(name: "album_name", value: t.album)) }
        if t.duration > 0 { items.append(URLQueryItem(name: "duration", value: String(Int(t.duration.rounded())))) }
        let record = try await get("/api/get", items, type: LRCLibRecord.self)
        guard let synced = record?.syncedLyrics, !synced.isEmpty else { return nil }
        return synced
    }

    private func fetchSearch(_ t: Track) async throws -> String? {
        let items = [
            URLQueryItem(name: "track_name", value: Self.cleanTitle(t.name)),
            URLQueryItem(name: "artist_name", value: Self.primaryArtist(t.artist)),
        ]
        guard let records = try await get("/api/search", items, type: [LRCLibRecord].self) else { return nil }
        let synced = records.filter { !($0.syncedLyrics ?? "").isEmpty }
        let closest = synced.first { t.duration > 0 && abs(($0.duration ?? 0) - t.duration) <= 4 }
        return (closest ?? synced.first)?.syncedLyrics
    }

    private func get<T: Decodable>(_ path: String, _ items: [URLQueryItem], type: T.Type) async throws -> T? {
        var components = URLComponents(string: "https://lrclib.net")!
        components.path = path
        components.queryItems = items
        // URLComponents leaves "+" unescaped, which servers read as a space ("C++" → "C  ").
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        let (data, response) = try await session.data(from: components.url!)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return nil }
        guard (200..<300).contains(status) else { throw URLError(.badServerResponse) }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func fileURL(_ trackID: String) -> URL {
        let safe = trackID.replacingOccurrences(of: "[^A-Za-z0-9_-]", with: "_", options: .regularExpression)
        return cacheDir.appendingPathComponent(safe + ".lrc")
    }

    /// "Song - Remastered 2011" / "Song (feat. X)" → "Song"
    static func cleanTitle(_ name: String) -> String {
        var s = name.replacingOccurrences(of: #"\s+-\s+.*$"#, with: "", options: .regularExpression)
        s = s.replacingOccurrences(of: #"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]"#, with: "",
                                   options: [.regularExpression, .caseInsensitive])
        return s.trimmingCharacters(in: .whitespaces)
    }

    static func primaryArtist(_ artist: String) -> String {
        String(artist.split(separator: ",").first ?? Substring(artist)).trimmingCharacters(in: .whitespaces)
    }
}

private struct LRCLibRecord: Decodable {
    let id: Int?
    let trackName: String?
    let artistName: String?
    let duration: Double?
    let instrumental: Bool?
    let syncedLyrics: String?
}
