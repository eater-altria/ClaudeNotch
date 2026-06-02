import AppKit

/// 把焦点切到会话所在的终端窗口/标签页。按终端类型派发：
/// - Warp：开 `warp://session/<uuid>`（精确、无需权限）
/// - Terminal.app / iTerm2：按 tty 用 AppleScript 选中对应 tab（首次需「自动化」授权）
/// - 其它：兜底把终端 app 激活到前台
enum TerminalJumper {

    static func jump(_ target: JumpTarget) {
        switch target.kind {
        case .warp:
            if let s = target.warpFocusURL, let url = URL(string: s) {
                NSWorkspace.shared.open(url)
            } else {
                activate(target.appURL)
            }
        case .terminalApp:
            if let tty = target.tty { runScript(terminalAppScript(tty: tty)) }
            else { activate(target.appURL) }
        case .iterm:
            if let tty = target.tty { runScript(itermScript(tty: tty)) }
            else { activate(target.appURL) }
        default:
            activate(target.appURL)
        }
    }

    private static func activate(_ url: URL?) {
        guard let url else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration()) { _, _ in }
    }

    /// AppleScript 放后台线程执行，避免首次「自动化」授权弹窗阻塞 UI。
    private static func runScript(_ source: String) {
        DispatchQueue.global(qos: .userInitiated).async {
            var err: NSDictionary?
            NSAppleScript(source: source)?.executeAndReturnError(&err)
            if let err { NSLog("[ClaudeNotch] 终端跳转 AppleScript 失败: %@", err) }
        }
    }

    private static func terminalAppScript(tty: String) -> String {
        """
        tell application "Terminal"
            activate
            repeat with w in windows
                repeat with t in tabs of w
                    if tty of t is "\(tty)" then
                        set selected of t to true
                        set frontmost of w to true
                        return
                    end if
                end repeat
            end repeat
        end tell
        """
    }

    private static func itermScript(tty: String) -> String {
        """
        tell application "iTerm2"
            activate
            repeat with w in windows
                repeat with t in tabs of w
                    repeat with s in sessions of t
                        if tty of s is "\(tty)" then
                            select w
                            select t
                            select s
                            return
                        end if
                    end repeat
                end repeat
            end repeat
        end tell
        """
    }
}
