import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let config = AppConfig.load()
    private lazy var host = StudioHost(config: config)
    private var windowController: StudioWindowController?
    private var signalSources: [DispatchSourceSignal] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppConfig.ensureStudioConfigFolder()
        NSApp.mainMenu = MainMenu.build(target: self)
        let controller = StudioWindowController(host: host)
        windowController = controller
        controller.showWindow(nil)
        controller.start()
        NSApp.activate(ignoringOtherApps: true)
        stopServerOnSignals()
    }

    func applicationWillTerminate(_ notification: Notification) { host.stop() }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// A killed app (SIGTERM from `kill`, logout) still takes the server it started down with it.
    private func stopServerOnSignals() {
        for signalNumber in [SIGTERM, SIGINT, SIGHUP] {
            signal(signalNumber, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: signalNumber, queue: .main)
            source.setEventHandler { NSApp.terminate(nil) }
            source.resume()
            signalSources.append(source)
        }
    }

    // MARK: menu actions

    @objc func reload(_ sender: Any?) { windowController?.reload() }
    @objc func zoomIn(_ sender: Any?) { windowController?.zoom(by: 1.1) }
    @objc func zoomOut(_ sender: Any?) { windowController?.zoom(by: 1 / 1.1) }
    @objc func actualSize(_ sender: Any?) { windowController?.zoom(by: nil) }

    @objc func openConfigFolder(_ sender: Any?) {
        AppConfig.ensureStudioConfigFolder()
        NSWorkspace.shared.open(AppConfig.studioConfigFolder)
    }

    @objc func showLog(_ sender: Any?) {
        NSWorkspace.shared.activateFileViewerSelecting([AppConfig.logFile])
    }

    @objc func openGuide(_ sender: Any?) {
        NSWorkspace.shared.open(config.repo.appendingPathComponent("studio/README.md"))
    }
}
