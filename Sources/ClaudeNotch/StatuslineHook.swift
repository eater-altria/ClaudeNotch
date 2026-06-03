import Foundation

/// 与 Claude Code 的 `statusLine` 钩子对接，取「官方/合规」的订阅额度。
///
/// 背景：消费者订阅（Pro/Max）的 5 小时 / 周额度**没有公开 API**。Claude Code 自己是从
/// 每次 `/v1/messages` 响应头 `anthropic-ratelimit-unified-*` 拿到的，并会把它整理成
/// `rate_limits.{five_hour,seven_day}`（`used_percentage` 0–100、`resets_at` Unix 秒）
/// 通过 **stdin 喂给用户配置的 statusLine 命令**——这是官方文档化的第三方钩子契约。
/// 复用 OAuth 令牌打 `/api/oauth/usage` 是 Anthropic 明令禁止（2026-02 起）且会被限流的做法；
/// 抓网页同样属自动化访问。相比之下，让 Claude Code **主动把数据交给我们**，是合规风险最低的实时来源。
///
/// 机制：
/// 1. 启用时，把本 app 自身（`<bin> --statusline`）写进 `~/.claude/settings.json` 的 `statusLine`，
///    并备份、链接用户原有的 statusline（透传，不抢占）。
/// 2. Claude Code 渲染状态栏时以 `--statusline` 调起本 app；`runHelper()` 读 stdin、
///    把 `rate_limits` 落盘到 `ratelimits.json`，再把 stdin 转发给原命令并输出其结果。
/// 3. `StatuslineProvider` 读 `ratelimits.json` 归一成 `ScrapeResult`。
///
/// 全程不读取、不复用任何令牌；只写自己的支持目录与（用户显式开启时）`~/.claude/settings.json`。
enum StatuslineHook {

    // MARK: - 共享路径

    static var supportDir: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("ClaudeNotch", isDirectory: true)
    }
    /// 钩子落盘的额度数据，`StatuslineProvider` 读它。
    static var ratelimitsFile: URL { supportDir.appendingPathComponent("ratelimits.json") }
    /// 接管前用户原有的 statusLine **整个对象**（用于透传 + 卸载时原样还原，保留 padding 等所有字段）。
    static var innerStatusLineFile: URL { supportDir.appendingPathComponent("inner-statusline.json") }
    static var claudeSettings: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude/settings.json")
    }

    /// 判断某条 statusLine 命令是否是我们自己（避免把自己存成「原命令」造成自我递归）。
    private static func isOurs(_ command: String) -> Bool {
        command.contains("--statusline") && command.contains("ClaudeNotch")
    }

    // MARK: - 作为 statusLine 命令运行（main.swift 检测到 --statusline 时调用）

    /// 读 stdin、落盘额度、透传原 statusline。必须快进快出，不启动任何 GUI。
    static func runHelper() {
        let input = FileHandle.standardInput.readDataToEndOfFile()
        let root = (try? JSONSerialization.jsonObject(with: input)) as? [String: Any]
        let rateLimits = root?["rate_limits"] as? [String: Any]

        if let rl = rateLimits { persist(rateLimits: rl) }

        // 透传：有原命令就转发同一份 stdin 并原样输出其状态栏；否则打印一行简洁默认。
        if let inner = innerCommand() {
            forward(input: input, command: inner, rateLimitsFallback: rateLimits)
        } else {
            FileHandle.standardOutput.write(Data(defaultLine(root: root, rateLimits: rateLimits).utf8))
        }
    }

    private static func persist(rateLimits: [String: Any]) {
        let payload: [String: Any] = [
            "capturedAt": Date().timeIntervalSince1970,
            "rate_limits": rateLimits,
        ]
        try? FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
        if let data = try? JSONSerialization.data(withJSONObject: payload) {
            try? data.write(to: ratelimitsFile, options: .atomic)
        }
    }

    /// 接管前的原 statusLine 对象（整体，用于卸载还原）。
    private static func innerStatusLine() -> [String: Any]? {
        guard let data = try? Data(contentsOf: innerStatusLineFile),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return obj
    }

    /// 从原 statusLine 对象里取出可执行命令（用于透传）。
    private static func innerCommand() -> String? {
        guard let cmd = innerStatusLine()?["command"] as? String else { return nil }
        let t = cmd.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? nil : t
    }

    /// 把同一份 stdin 转发给原 statusline 命令，原样输出其 stdout。
    private static func forward(input: Data, command: String, rateLimitsFallback: [String: Any]?) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", command]
        let inPipe = Pipe(), outPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = FileHandle.standardError
        do {
            try p.run()
            inPipe.fileHandleForWriting.write(input)
            try? inPipe.fileHandleForWriting.close()
            let out = outPipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            FileHandle.standardOutput.write(out)
        } catch {
            FileHandle.standardOutput.write(Data(defaultLine(root: nil, rateLimits: rateLimitsFallback).utf8))
        }
    }

    /// 没有原 statusline 时的默认行：模型名 · 5h% · 7d%（避免 CLI 状态栏变空）。
    private static func defaultLine(root: [String: Any]?, rateLimits: [String: Any]?) -> String {
        var parts: [String] = []
        if let model = root?["model"] as? [String: Any], let name = model["display_name"] as? String {
            parts.append(name)
        }
        func pct(_ key: String, _ label: String) {
            if let w = rateLimits?[key] as? [String: Any], let p = (w["used_percentage"] as? NSNumber)?.intValue {
                parts.append("\(label) \(p)%")
            }
        }
        pct("five_hour", "5h")
        pct("seven_day", "7d")
        return parts.joined(separator: " · ")
    }

    // MARK: - 安装 / 卸载

    /// 幂等确保已注册（启动时调用）：未安装、或已安装但指向的二进制不是当前 app（app 被移动/换构建）时，(重新)安装。
    static func ensureInstalled() {
        guard let bin = Bundle.main.executablePath else { return }
        if isInstalled, currentCommand()?.contains(bin) == true { return }
        install()
    }

    private static func currentCommand() -> String? {
        guard let data = try? Data(contentsOf: claudeSettings),
              let s = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let sl = s["statusLine"] as? [String: Any] else { return nil }
        return sl["command"] as? String
    }

    /// 把本 app 注册为 Claude Code 的 statusLine 命令。会先整文件备份一次，并链接用户原有 statusline。
    static func install() {
        let fm = FileManager.default
        try? fm.createDirectory(at: supportDir, withIntermediateDirectories: true)
        guard let binPath = Bundle.main.executablePath else { return }

        var settings: [String: Any] = [:]
        if let data = try? Data(contentsOf: claudeSettings) {
            settings = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
            // 整文件备份一次（仅首次，不覆盖已有备份）
            let backup = claudeSettings.appendingPathExtension("claudenotch-bak")
            if !fm.fileExists(atPath: backup.path) { try? data.write(to: backup) }
        }

        // 记下接管前的原 statusLine 整个对象（仅当它不是我们自己），供透传与卸载原样还原
        if let sl = settings["statusLine"] as? [String: Any], !isOurs(sl["command"] as? String ?? ""),
           let data = try? JSONSerialization.data(withJSONObject: sl) {
            try? data.write(to: innerStatusLineFile, options: .atomic)
        }

        settings["statusLine"] = [
            "type": "command",
            "command": "\"\(binPath)\" --statusline",
            "padding": 0,
        ]
        writeSettings(settings)
    }

    /// 撤销注册：把 statusLine 恢复成用户原有命令（若无则移除该键）。
    /// `purgeData=false`（退出前还原用）保留已抓到的 `ratelimits.json`，下次启动可秒显上次额度。
    static func uninstall(purgeData: Bool = true) {
        let fm = FileManager.default
        if let data = try? Data(contentsOf: claudeSettings),
           var settings = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let sl = settings["statusLine"] as? [String: Any],
           let cmd = sl["command"] as? String, isOurs(cmd) {       // 只有当前确实是我们才动它
            if let inner = innerStatusLine() {
                settings["statusLine"] = inner          // 整对象原样还原（保留 padding 等所有字段）
            } else {
                settings.removeValue(forKey: "statusLine")
            }
            writeSettings(settings)
        }
        try? fm.removeItem(at: innerStatusLineFile)   // 已还原进 settings.json，链接文件不再需要
        if purgeData { try? fm.removeItem(at: ratelimitsFile) }
    }

    /// 当前 `~/.claude/settings.json` 的 statusLine 是否已是我们。
    static var isInstalled: Bool {
        guard let data = try? Data(contentsOf: claudeSettings),
              let settings = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let sl = settings["statusLine"] as? [String: Any],
              let cmd = sl["command"] as? String else { return false }
        return isOurs(cmd)
    }

    private static func writeSettings(_ settings: [String: Any]) {
        let dir = claudeSettings.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if let out = try? JSONSerialization.data(withJSONObject: settings, options: [.prettyPrinted, .sortedKeys]) {
            try? out.write(to: claudeSettings, options: .atomic)
        }
    }
}
