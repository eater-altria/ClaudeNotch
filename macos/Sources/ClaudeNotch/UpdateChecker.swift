import AppKit

/// 检查更新：仅在用户点「检查更新」时执行，对比 GitHub 最新 release 与本地版本。
@MainActor
enum UpdateChecker {

    static let releasesPage = URL(string: "https://github.com/eater-altria/ClaudeNotch/releases")!
    private static let apiURL = URL(string: "https://api.github.com/repos/eater-altria/ClaudeNotch/releases/latest")!

    static var currentVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0"
    }

    struct Release { let tag: String; let version: String; let url: URL }

    /// 用户点击时调用：查最新 release，自己弹 NSAlert 反馈结果。
    static func checkInteractively() {
        Task {
            do {
                let latest = try await fetchLatest()
                presentResult(latest: latest)
            } catch {
                presentError(error)
            }
        }
    }

    private static func fetchLatest() async throws -> Release {
        var req = URLRequest(url: apiURL)
        req.setValue("ClaudeNotch", forHTTPHeaderField: "User-Agent")  // GitHub API 要求 UA
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 15
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
            let code = (resp as? HTTPURLResponse)?.statusCode ?? -1
            throw NSError(domain: "UpdateChecker", code: code,
                          userInfo: [NSLocalizedDescriptionKey: tr("GitHub 返回错误（HTTP \(code)）", "GitHub returned an error (HTTP \(code))")])
        }
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String else {
            throw NSError(domain: "UpdateChecker", code: -2,
                          userInfo: [NSLocalizedDescriptionKey: tr("无法解析最新版本信息", "Failed to parse latest version info")])
        }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        let url = (obj["html_url"] as? String).flatMap { URL(string: $0) } ?? releasesPage
        return Release(tag: tag, version: version, url: url)
    }

    /// 语义版本比较：a < b 返回 true（按段数字比较，缺省段当 0）。
    static func isOlder(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x < y }
        }
        return false
    }

    private static func presentResult(latest: Release) {
        let alert = NSAlert()
        if isOlder(currentVersion, than: latest.version) {
            alert.messageText = tr("发现新版本 \(latest.tag)", "New version available: \(latest.tag)")
            alert.informativeText = tr("当前版本 v\(currentVersion)。前往下载页升级？", "Current version v\(currentVersion). Go to the download page to upgrade?")
            alert.addButton(withTitle: tr("前往下载", "Download"))
            alert.addButton(withTitle: tr("稍后", "Later"))
            run(alert) { if $0 == .alertFirstButtonReturn { NSWorkspace.shared.open(latest.url) } }
        } else {
            alert.messageText = tr("已是最新版本", "You're up to date")
            alert.informativeText = tr("当前 v\(currentVersion) 已是最新。", "v\(currentVersion) is the latest version.")
            alert.addButton(withTitle: tr("好", "OK"))
            run(alert) { _ in }
        }
    }

    private static func presentError(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = tr("检查更新失败", "Update check failed")
        alert.informativeText = error.localizedDescription
        alert.addButton(withTitle: tr("好", "OK"))
        alert.addButton(withTitle: tr("打开发布页", "Open releases page"))
        run(alert) { if $0 == .alertSecondButtonReturn { NSWorkspace.shared.open(releasesPage) } }
    }

    private static func run(_ alert: NSAlert, handler: (NSApplication.ModalResponse) -> Void) {
        NSApp.activate(ignoringOtherApps: true)
        handler(alert.runModal())
    }
}
