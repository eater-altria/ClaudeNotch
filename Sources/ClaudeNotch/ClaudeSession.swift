import AppKit
import WebKit

/// 抓取结果
enum FetchOutcome {
    case success(ScrapeResult)
    case loggedOut
    case failure(String)
}

/// 单个持久化 WKWebView：既用于隐藏抓取，也用于显示登录界面。
/// cookie 由 WKWebsiteDataStore.default() 持久化（按 app bundle id 落盘），
/// 凭证全程留在 WebKit 里，本 app 不读取、不存储、不外传。
@MainActor
final class ClaudeSession: NSObject {

    static let usageURL = URL(string: "https://claude.ai/settings/usage")!
    static let loginURL = URL(string: "https://claude.ai/login")!

    // 用一个隐藏的离屏窗口承载 webview；登录时把它移到屏幕上显示。
    private var hostWindow: NSWindow!
    private(set) var webView: WKWebView!

    private var navContinuation: CheckedContinuation<Void, Never>?
    private var loginPollTimer: Timer?
    var onLoginSuccess: (() -> Void)?

    override init() {
        super.init()
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()          // 持久化 cookie
        config.processPool = WKProcessPool()
        let wv = WKWebView(frame: NSRect(x: 0, y: 0, width: 520, height: 720), configuration: config)
        wv.navigationDelegate = self
        // 用 Safari UA，最大化 claude.ai 登录/渲染兼容性
        wv.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Safari/605.1.15"
        self.webView = wv

        let win = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 520, height: 720),
                           styleMask: [.titled, .closable],
                           backing: .buffered, defer: false)
        win.title = "登录 Claude"
        win.contentView = wv
        win.isReleasedWhenClosed = false
        self.hostWindow = win
    }

    // MARK: - 抓取额度

    /// 加载用量页、轮询 DOM 直到拿到数据或判定未登录。
    func fetchUsage() async -> FetchOutcome {
        await load(Self.usageURL)

        // 用量页是 SPA，数据在导航完成后才异步渲染，轮询若干次
        for attempt in 0..<14 {
            // 先看是否被重定向到登录页
            if let url = webView.url?.absoluteString,
               url.contains("/login") || url.contains("accounts.google") || url.contains("/auth") {
                return .loggedOut
            }
            if let result = await evaluateExtractor() {
                if result.sessionPercent != nil || result.weeklyAllModelsPercent != nil
                    || result.weeklySonnetPercent != nil {
                    return .success(result)
                }
            }
            try? await Task.sleep(nanoseconds: attempt < 4 ? 600_000_000 : 900_000_000)
        }

        // 超时：再判一次登录态
        if let url = webView.url?.absoluteString, url.contains("/login") {
            return .loggedOut
        }
        return .failure("未能在用量页解析到数据（页面结构可能已变化，或尚未登录）")
    }

    private func evaluateExtractor() async -> ScrapeResult? {
        await withCheckedContinuation { (cont: CheckedContinuation<ScrapeResult?, Never>) in
            webView.evaluateJavaScript(Extractor.script) { value, _ in
                guard let json = value as? String,
                      let data = json.data(using: .utf8),
                      let result = try? JSONDecoder().decode(ScrapeResult.self, from: data) else {
                    cont.resume(returning: nil)
                    return
                }
                cont.resume(returning: result)
            }
        }
    }

    /// 退出登录：清除 claude.ai / anthropic 的 cookie 与站点数据，下次抓取即变未登录。
    func logout(completion: @escaping () -> Void) {
        let store = webView.configuration.websiteDataStore
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        store.fetchDataRecords(ofTypes: types) { records in
            let targets = records.filter {
                let n = $0.displayName.lowercased()
                return n.contains("claude") || n.contains("anthropic")
            }
            store.removeData(ofTypes: types, for: targets) {
                self.webView.load(URLRequest(url: URL(string: "about:blank")!))
                completion()
            }
        }
    }

    // MARK: - 登录界面

    func presentLogin() {
        load(Self.loginURL, waitForFinish: false)
        hostWindow.setContentSize(NSSize(width: 520, height: 720))
        hostWindow.center()
        hostWindow.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        loginPollTimer?.invalidate()
        loginPollTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkLoginProgress() }
        }
    }

    private func checkLoginProgress() {
        guard let url = webView.url?.absoluteString else { return }
        let onClaude = url.contains("claude.ai")
        let stillAuth = url.contains("/login") || url.contains("accounts.google") || url.contains("/auth")
        if onClaude && !stillAuth {
            // 登录成功
            loginPollTimer?.invalidate()
            loginPollTimer = nil
            hostWindow.orderOut(nil)
            onLoginSuccess?()
        }
    }

    // MARK: - 导航

    @discardableResult
    private func load(_ url: URL, waitForFinish: Bool = true) -> Void {
        Task { await load(url) }
    }

    private func load(_ url: URL) async {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            // 若已有等待中的导航，先放行旧的，避免泄漏
            navContinuation?.resume()
            navContinuation = cont
            webView.load(URLRequest(url: url))
            // 兜底超时，避免导航卡死
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 15_000_000_000)
                if let c = self.navContinuation {
                    self.navContinuation = nil
                    c.resume()
                }
            }
        }
    }
}

extension ClaudeSession: WKNavigationDelegate {
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        navContinuation?.resume()
        navContinuation = nil
    }
    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        navContinuation?.resume()
        navContinuation = nil
    }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        navContinuation?.resume()
        navContinuation = nil
    }
}
