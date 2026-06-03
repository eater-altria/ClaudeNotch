import AppKit

// 作为 Claude Code 的 statusLine 命令被调起时：读 stdin、落盘额度、透传原命令，立即退出——不启动 GUI。
if CommandLine.arguments.contains("--statusline") {
    StatuslineHook.runHelper()
    exit(0)
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.run()
}
