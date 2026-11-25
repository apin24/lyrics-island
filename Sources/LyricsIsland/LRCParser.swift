import Foundation

/// Parses LRC text ("[mm:ss.xx] line") into time-sorted lines.
enum LRCParser {
    static func parse(_ raw: String) -> [LyricLine] {
        var offset = 0.0
        var entries: [(time: Double, order: Int, text: String)] = []
        var order = 0

        for rawLine in raw.components(separatedBy: .newlines) {
            var rest = Substring(rawLine.trimmingCharacters(in: .whitespaces))
            var times: [Double] = []
            while rest.hasPrefix("["), let close = rest.firstIndex(of: "]") {
                let tag = rest[rest.index(after: rest.startIndex)..<close]
                rest = rest[rest.index(after: close)...]
                if let t = parseTime(tag) {
                    times.append(t)
                } else if tag.lowercased().hasPrefix("offset:"),
                          let ms = Double(tag.dropFirst(7).trimmingCharacters(in: .whitespaces)) {
                    offset = ms / 1000
                }
            }
            let text = rest.trimmingCharacters(in: .whitespaces)
            for t in times {
                entries.append((t, order, text))
                order += 1
            }
        }

        entries.sort { $0.time == $1.time ? $0.order < $1.order : $0.time < $1.time }
        return entries.enumerated().map { i, e in
            LyricLine(id: i, time: max(0, e.time - offset), text: e.text)
        }
    }

    /// "01:23.45", "1:23", "01:23:45" → seconds. Metadata tags like "ar:Name" return nil.
    static func parseTime(_ tag: Substring) -> Double? {
        let parts = tag.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count >= 2, let minutes = Int(parts[0]) else { return nil }
        let secText = parts.count == 3 ? "\(parts[1]).\(parts[2])" : String(parts[1])
        guard let seconds = Double(secText.replacingOccurrences(of: ",", with: ".")) else { return nil }
        return Double(minutes) * 60 + seconds
    }
}
