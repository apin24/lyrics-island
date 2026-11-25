// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "LyricsIsland",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "LyricsIsland", path: "Sources/LyricsIsland")
    ]
)
