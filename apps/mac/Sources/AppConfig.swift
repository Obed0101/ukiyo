import Foundation

/// Where the Studio lives and where the app keeps its files. The repo path is baked into Info.plist at build time and
/// can be overridden by ~/.config/ukiyo/app.json: {"repo": "/path/to/ukiyo"}.
struct AppConfig {
    static let configRoot = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".config/ukiyo")
    static let studioConfigFolder = configRoot.appendingPathComponent("studio")
    static let logFile = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/Ukiyo/studio.log")
    static let discoveryFolder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ukiyo/studio")

    let repo: URL
    let repoOverridden: Bool
    /// {"reuse": false} always starts its own server instead of joining a running Studio.
    let reuseRunningStudio: Bool

    static func load() -> AppConfig {
        let file = configRoot.appendingPathComponent("app.json")
        let object = (try? Data(contentsOf: file)).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] } ?? [:]
        let reuse = object["reuse"] as? Bool ?? true
        if let path = object["repo"] as? String, !path.isEmpty {
            return AppConfig(repo: URL(fileURLWithPath: (path as NSString).expandingTildeInPath), repoOverridden: true, reuseRunningStudio: reuse)
        }
        let baked = Bundle.main.object(forInfoDictionaryKey: "UkiyoRepo") as? String ?? ""
        return AppConfig(repo: URL(fileURLWithPath: baked), repoOverridden: false, reuseRunningStudio: reuse)
    }

    var serverScript: URL { repo.appendingPathComponent("studio/src/server.ts") }

    /// First bun that exists: the usual install locations, then PATH.
    static func findBun() -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        var candidates = ["/opt/homebrew/bin/bun", "\(home)/.bun/bin/bun", "/usr/local/bin/bun"]
        let path = ProcessInfo.processInfo.environment["PATH"] ?? ""
        candidates += path.split(separator: ":").map { "\($0)/bun" }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }.map { URL(fileURLWithPath: $0) }
    }

    static func ensureStudioConfigFolder() {
        try? FileManager.default.createDirectory(at: studioConfigFolder, withIntermediateDirectories: true)
    }
}
