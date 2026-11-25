import Foundation
import Combine

enum TextSize: String, CaseIterable {
    case small, medium, large

    var scale: CGFloat {
        switch self {
        case .small: 0.9
        case .medium: 1
        case .large: 1.15
        }
    }

    var label: String {
        switch self {
        case .small: "Small"
        case .medium: "Medium"
        case .large: "Large"
        }
    }
}

@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()

    @Published var showIsland: Bool { didSet { UserDefaults.standard.set(showIsland, forKey: "showIsland") } }
    @Published var expandOnHover: Bool { didSet { UserDefaults.standard.set(expandOnHover, forKey: "expandOnHover") } }
    @Published var textSize: TextSize { didSet { UserDefaults.standard.set(textSize.rawValue, forKey: "textSize") } }
    @Published var demoMode: Bool { didSet { UserDefaults.standard.set(demoMode, forKey: "demoMode") } }
    @Published var hideInFullscreen: Bool { didSet { UserDefaults.standard.set(hideInFullscreen, forKey: "hideInFullscreen") } }

    private init() {
        let d = UserDefaults.standard
        d.register(defaults: [
            "showIsland": true,
            "expandOnHover": true,
            "textSize": TextSize.medium.rawValue,
            "demoMode": false,
            "hideInFullscreen": false,
        ])
        showIsland = d.bool(forKey: "showIsland")
        expandOnHover = d.bool(forKey: "expandOnHover")
        textSize = TextSize(rawValue: d.string(forKey: "textSize") ?? "") ?? .medium
        demoMode = d.bool(forKey: "demoMode")
        hideInFullscreen = d.bool(forKey: "hideInFullscreen")
    }

    /// Per-track lyric offset in seconds. Positive = lines appear earlier (fixes lyrics that lag behind).
    func offset(for trackID: String?) -> Double {
        guard let trackID else { return 0 }
        return (UserDefaults.standard.dictionary(forKey: "offsets")?[trackID] as? Double) ?? 0
    }

    func setOffset(_ value: Double, for trackID: String) {
        var dict = UserDefaults.standard.dictionary(forKey: "offsets") ?? [:]
        dict[trackID] = abs(value) < 0.05 ? nil : value
        UserDefaults.standard.set(dict, forKey: "offsets")
        objectWillChange.send()
    }
}
