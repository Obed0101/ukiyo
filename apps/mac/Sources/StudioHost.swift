import Foundation

struct StudioFailure: Error {
    let title: String
    let detail: String
}

/// Finds a running Studio or starts one, and stops the one it started. Everything stays on 127.0.0.1; the page embeds
/// its own token, so the app never handles it.
final class StudioHost {
    private let config: AppConfig
    private var process: Process?
    private var stopping = false

    /// Called on the main queue if the server this app started exits while the app is open.
    var onUnexpectedExit: ((StudioFailure) -> Void)?

    init(config: AppConfig) {
        self.config = config
        _ = Self.launchedAt // statics are lazy: pin the app's start time before any log is written
    }

    func connect(completion: @escaping (Result<URL, StudioFailure>) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let result = self.findOrStart()
            DispatchQueue.main.async { completion(result) }
        }
    }

    func stop() {
        stopping = true
        guard let process, process.isRunning else { return }
        process.terminate()
        let deadline = Date().addingTimeInterval(2)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        if process.isRunning { kill(process.processIdentifier, SIGKILL) }
    }

    func restart(completion: @escaping (Result<URL, StudioFailure>) -> Void) {
        stop()
        process = nil
        stopping = false
        connect(completion: completion)
    }

    private static let launchedAt = Date()

    /// The end of this run's server log; empty when this run never wrote one (an old log would mislead).
    static func logTail(lines: Int = 40) -> String {
        let modified = (try? AppConfig.logFile.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        guard let modified, modified >= launchedAt,
              let text = try? String(contentsOf: AppConfig.logFile, encoding: .utf8) else { return "" }
        let all = text.split(separator: "\n", omittingEmptySubsequences: true)
        return all.suffix(lines).joined(separator: "\n")
    }

    // MARK: discovery

    private func findOrStart() -> Result<URL, StudioFailure> {
        // The server this app already started stays the one it talks to (Reload must not hop to another Studio).
        if let own = process, own.isRunning, let url = announced(pid: own.processIdentifier), answers(url) { return .success(url) }
        if config.reuseRunningStudio, let url = runningStudio() { return .success(url) }
        return start()
    }

    /// A Studio announced in ~/.ukiyo/studio/<pid>.json whose process is alive and answers.
    private func runningStudio() -> URL? {
        let files = (try? FileManager.default.contentsOfDirectory(at: AppConfig.discoveryFolder, includingPropertiesForKeys: nil)) ?? []
        var sessions: [(startedAt: String, url: URL)] = []
        for file in files where file.pathExtension == "json" {
            guard let data = try? Data(contentsOf: file),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let pid = object["pid"] as? Int, let port = object["port"] as? Int,
                  kill(pid_t(pid), 0) == 0,
                  let url = URL(string: "http://127.0.0.1:\(port)/") else { continue }
            sessions.append((object["startedAt"] as? String ?? "", url))
        }
        return sessions.sorted { $0.startedAt > $1.startedAt }.first { answers($0.url) }?.url
    }

    private func answers(_ url: URL) -> Bool {
        var request = URLRequest(url: url, timeoutInterval: 1.5)
        request.httpMethod = "GET"
        let done = DispatchSemaphore(value: 0)
        var ok = false
        URLSession.shared.dataTask(with: request) { _, response, _ in
            ok = (response as? HTTPURLResponse)?.statusCode == 200
            done.signal()
        }.resume()
        _ = done.wait(timeout: .now() + 2)
        return ok
    }

    // MARK: start

    private func start() -> Result<URL, StudioFailure> {
        guard FileManager.default.fileExists(atPath: config.serverScript.path) else {
            let hint = config.repoOverridden ? "Set \"repo\" in ~/.config/ukiyo/app.json to your ukiyo checkout."
                : "Rebuild the app from the checkout (scripts/mac-app.sh) or set \"repo\" in ~/.config/ukiyo/app.json."
            return .failure(StudioFailure(title: "The Studio server was not found", detail: "\(config.serverScript.path) does not exist.\n\(hint)"))
        }
        guard let bun = AppConfig.findBun() else {
            return .failure(StudioFailure(title: "Bun was not found", detail: "Install bun (https://bun.sh) or put it at /opt/homebrew/bin/bun or ~/.bun/bin/bun."))
        }

        let logDirectory = AppConfig.logFile.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: AppConfig.logFile.path, contents: nil)
        guard let log = try? FileHandle(forWritingTo: AppConfig.logFile) else {
            return .failure(StudioFailure(title: "The Studio log could not be opened", detail: AppConfig.logFile.path))
        }

        let child = Process()
        child.executableURL = bun
        child.arguments = [config.serverScript.path]
        child.currentDirectoryURL = config.repo
        child.standardOutput = log
        child.standardError = log
        child.standardInput = FileHandle.nullDevice
        child.terminationHandler = { [weak self] finished in
            guard let self, !self.stopping else { return }
            let failure = StudioFailure(title: "The Studio server stopped", detail: "bun exited with status \(finished.terminationStatus).")
            DispatchQueue.main.async { self.onUnexpectedExit?(failure) }
        }
        do { try child.run() } catch {
            return .failure(StudioFailure(title: "The Studio server could not start", detail: error.localizedDescription))
        }
        process = child

        let deadline = Date().addingTimeInterval(20)
        while Date() < deadline {
            if !child.isRunning {
                return .failure(StudioFailure(title: "The Studio server exited while starting", detail: "bun exited with status \(child.terminationStatus)."))
            }
            if let url = announced(pid: child.processIdentifier), answers(url) { return .success(url) }
            Thread.sleep(forTimeInterval: 0.15)
        }
        stop()
        return .failure(StudioFailure(title: "The Studio server did not answer", detail: "No response within 20 seconds."))
    }

    private func announced(pid: Int32) -> URL? {
        let file = AppConfig.discoveryFolder.appendingPathComponent("\(pid).json")
        guard let data = try? Data(contentsOf: file),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let port = object["port"] as? Int else { return nil }
        return URL(string: "http://127.0.0.1:\(port)/")
    }
}
