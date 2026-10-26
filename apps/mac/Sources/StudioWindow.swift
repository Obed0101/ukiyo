import AppKit
import WebKit

/// Records the last left mouse-down so a drag started by the page (its top bar) can hand it to the window.
final class StudioNSWindow: NSWindow {
    private(set) var lastMouseDown: NSEvent?
    override func sendEvent(_ event: NSEvent) {
        if event.type == .leftMouseDown { lastMouseDown = event }
        super.sendEvent(event)
    }
}

enum Palette {
    /// The page's own ink and paper, so nothing flashes white before the first paint.
    static let background = NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(srgbRed: 0x0a / 255, green: 0x0a / 255, blue: 0x0a / 255, alpha: 1)
            : NSColor(srgbRed: 0xfa / 255, green: 0xfa / 255, blue: 0xfa / 255, alpha: 1)
    }
}

/// The one window: a web view under a transparent titlebar, a spinner while the server starts, and a plain native
/// error screen with the log tail when it does not.
final class StudioWindowController: NSWindowController, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler, NSWindowDelegate {
    private let host: StudioHost
    private let webView: WKWebView
    private let spinner = NSProgressIndicator()
    private let errorView = ErrorView()
    private var studioURL: URL?
    private static let zoomKey = "studio.pageZoom"

    /// Marks the page as hosted by the Mac app (a CSS hook) and lets the top bar drag the window.
    private static let hostScript = """
    document.documentElement.dataset.host = "mac";
    addEventListener("mousedown", function (e) {
      if (e.button !== 0 || !e.target.closest) return;
      if (!e.target.closest(".topbar")) return;
      if (e.target.closest("button, select, input, textarea, a, label, [role=button], [tabindex]")) return;
      webkit.messageHandlers.host.postMessage(e.detail >= 2 ? "zoom" : "drag");
    }, true);
    """

    init(host: StudioHost) {
        self.host = host
        let configuration = WKWebViewConfiguration()
        let controller = WKUserContentController()
        controller.addUserScript(WKUserScript(source: Self.hostScript, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController = controller
        webView = WKWebView(frame: .zero, configuration: configuration)

        let window = StudioNSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1360, height: 860),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false)
        window.title = "Ukiyo"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isMovableByWindowBackground = false
        window.backgroundColor = Palette.background
        window.minSize = NSSize(width: 720, height: 480)
        window.isReleasedWhenClosed = false
        super.init(window: window)
        window.delegate = self
        DispatchQueue.main.async { [weak self] in self?.layoutTrafficLights() }

        controller.add(WeakScriptHandler(self), name: "host")
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.underPageBackgroundColor = Palette.background
        let saved = UserDefaults.standard.double(forKey: Self.zoomKey)
        webView.pageZoom = saved > 0 ? saved : 1

        let content = NSView()
        window.contentView = content
        for view in [webView, errorView, spinner] as [NSView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview(view)
        }
        spinner.style = .spinning
        spinner.controlSize = .regular
        spinner.isIndeterminate = true
        NSLayoutConstraint.activate([
            webView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            webView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            webView.topAnchor.constraint(equalTo: content.topAnchor),
            webView.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            errorView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            errorView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            errorView.topAnchor.constraint(equalTo: content.topAnchor),
            errorView.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            spinner.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            spinner.centerYAnchor.constraint(equalTo: content.centerYAnchor),
        ])
        errorView.onRetry = { [weak self] in self?.retry() }
        errorView.isHidden = true
        webView.isHidden = true

        if !window.setFrameUsingName("UkiyoMain") { window.center() }
        window.setFrameAutosaveName("UkiyoMain")
        host.onUnexpectedExit = { [weak self] failure in self?.show(failure) }
    }

    required init?(coder: NSCoder) { fatalError("[WINDOW]: not loadable from a nib") }

    // MARK: states

    func start() {
        showLoading()
        host.connect { [weak self] result in self?.finish(result) }
    }

    private func retry() {
        showLoading()
        host.restart { [weak self] result in self?.finish(result) }
    }

    private func finish(_ result: Result<URL, StudioFailure>) {
        switch result {
        case .success(let url):
            studioURL = url
            webView.load(URLRequest(url: url))
        case .failure(let failure):
            show(failure)
        }
    }

    private func showLoading() {
        errorView.isHidden = true
        webView.isHidden = true
        spinner.isHidden = false
        spinner.startAnimation(nil)
    }

    private func show(_ failure: StudioFailure) {
        spinner.stopAnimation(nil)
        spinner.isHidden = true
        webView.isHidden = true
        errorView.present(failure, log: StudioHost.logTail())
        errorView.isHidden = false
    }

    // MARK: menu actions

    func reload() {
        guard let url = studioURL, !webView.isHidden else { retry(); return }
        // A page whose server went away gets a restart, not a blank reload.
        host.connect { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let current) where current == url: self.webView.reload()
            default: self.finish(result)
            }
        }
    }

    /// Settings is a route in the page; the menu item and Command-comma navigate to it.
    func openSettings() {
        guard !webView.isHidden else { return }
        webView.evaluateJavaScript("location.hash = '#/settings'", completionHandler: nil)
    }

    func zoom(by factor: Double?) {
        let next = factor.map { min(3, max(0.5, webView.pageZoom * $0)) } ?? 1
        webView.pageZoom = next
        UserDefaults.standard.set(next, forKey: Self.zoomKey)
    }

    // MARK: web view

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        spinner.stopAnimation(nil)
        spinner.isHidden = true
        webView.isHidden = false
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        show(StudioFailure(title: "The Studio page did not load", detail: error.localizedDescription))
    }

    /// The window stays on the Studio's origin; everything else opens in the default browser.
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url, url.scheme == "http" || url.scheme == "https" else { decisionHandler(.allow); return }
        if url.host == "127.0.0.1" && url.port == studioURL?.port { decisionHandler(.allow); return }
        NSWorkspace.shared.open(url)
        decisionHandler(.cancel)
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url { NSWorkspace.shared.open(url) }
        return nil
    }

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let window = window as? StudioNSWindow, let body = message.body as? String else { return }
        switch body {
        case "drag": if let event = window.lastMouseDown { window.performDrag(with: event) }
        case "zoom": window.zoom(nil)
        default: break
        }
    }

    // MARK: window

    func windowWillClose(_ notification: Notification) { NSApp.terminate(nil) }

    /// The page header is the titlebar row (52 px, studio.css `.topbar`): the traffic lights sit centred in it, with the
    /// breadcrumb starting 80 px in. AppKit puts them in a 28 px strip, so the titlebar container is made as tall as the
    /// header and the buttons are centred in it again after every resize.
    private static let headerHeight: CGFloat = 52
    private static let lightsLeading: CGFloat = 20
    private static let lightsSpacing: CGFloat = 20

    private func layoutTrafficLights() {
        guard let window, !window.styleMask.contains(.fullScreen),
              let close = window.standardWindowButton(.closeButton),
              let container = close.superview?.superview else { return }
        let height = Self.headerHeight
        container.frame = NSRect(x: 0, y: window.frame.height - height, width: window.frame.width, height: height)
        let buttons: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
        for (index, type) in buttons.enumerated() {
            guard let button = window.standardWindowButton(type) else { continue }
            button.setFrameOrigin(NSPoint(x: Self.lightsLeading + CGFloat(index) * Self.lightsSpacing, y: ((button.superview?.frame.height ?? height) - button.frame.height) / 2))
        }
    }

    func windowDidResize(_ notification: Notification) { layoutTrafficLights() }
    func windowDidExitFullScreen(_ notification: Notification) { layoutTrafficLights() }
    func windowDidBecomeKey(_ notification: Notification) { layoutTrafficLights() }
}

/// WKUserContentController retains its handlers; this keeps the window controller from being retained by it.
private final class WeakScriptHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ target: WKScriptMessageHandler) { self.target = target }
    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(controller, didReceive: message)
    }
}

/// Plain native failure screen: what failed, the log tail, Retry.
final class ErrorView: NSView {
    var onRetry: (() -> Void)?
    private let title = NSTextField(labelWithString: "")
    private let detail = NSTextField(wrappingLabelWithString: "")
    private let logText = NSTextView()
    private let logScroll = NSScrollView()

    override init(frame: NSRect) {
        super.init(frame: frame)
        title.font = .systemFont(ofSize: 17, weight: .semibold)
        detail.font = .systemFont(ofSize: 13)
        detail.textColor = .secondaryLabelColor
        detail.isSelectable = true

        logText.isEditable = false
        logText.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        logText.textColor = .labelColor
        logText.drawsBackground = false
        logText.textContainerInset = NSSize(width: 8, height: 8)
        let scroll = logScroll
        scroll.documentView = logText
        scroll.hasVerticalScroller = true
        scroll.borderType = .lineBorder
        scroll.drawsBackground = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.heightAnchor.constraint(equalToConstant: 200).isActive = true

        let retry = NSButton(title: "Retry", target: self, action: #selector(retryPressed))
        retry.keyEquivalent = "\r"
        retry.bezelStyle = .rounded
        let showLog = NSButton(title: "Show Log in Finder", target: self, action: #selector(showLogPressed))
        showLog.bezelStyle = .rounded
        let buttons = NSStackView(views: [retry, showLog])
        buttons.spacing = 8

        let stack = NSStackView(views: [title, detail, scroll, buttons])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            stack.widthAnchor.constraint(equalToConstant: 560),
            scroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            detail.widthAnchor.constraint(equalTo: stack.widthAnchor),
        ])
    }

    required init?(coder: NSCoder) { fatalError("[ERROR VIEW]: not loadable from a nib") }

    func present(_ failure: StudioFailure, log: String) {
        title.stringValue = failure.title
        detail.stringValue = failure.detail
        logText.string = log
        logText.scrollToEndOfDocument(nil)
        logScroll.isHidden = log.isEmpty
    }

    override func draw(_ dirtyRect: NSRect) {
        Palette.background.setFill()
        dirtyRect.fill()
    }

    @objc private func retryPressed() { onRetry?() }
    @objc private func showLogPressed() { NSWorkspace.shared.activateFileViewerSelecting([AppConfig.logFile]) }
}
