// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Later",
    platforms: [.macOS("26.0")],
    targets: [
        .target(name: "LaterCore"),
        .executableTarget(name: "Later", dependencies: ["LaterCore"]),
        .testTarget(name: "LaterCoreTests", dependencies: ["LaterCore"]),
        .testTarget(name: "LaterTests", dependencies: ["Later", "LaterCore"]),
    ]
)
