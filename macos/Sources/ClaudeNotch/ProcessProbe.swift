import Foundation
import Darwin

/// 一个运行中的 claude 进程。
struct LiveClaude {
    let pid: pid_t
    let cwd: String
    let dirName: String     // cwd 对应的 Claude Code 项目目录名
    let startTime: Date     // 进程启动时间——用于与 transcript 创建时间配对
    let jump: JumpTarget?   // 跳转到该会话所在终端 tab 的信息
}

/// 通过 libproc 探测运行中的 Claude Code 进程。
/// 用于把“活跃会话”从 mtime 启发式（会误含刚关闭的会话）收紧为“确有进程在跑”，
/// 并通过“启动时间 ↔ transcript 创建时间”把每个进程精确对应到它自己的会话文件。
enum ProcessProbe {

    /// 进程的 argv[0]（启动命令名）。与版本号、安装路径无关——这是识别 claude 最稳的判据。
    /// KERN_PROCARGS2 缓冲区布局：[argc:Int32][exec_path\0][\0…对齐][argv0\0][argv1\0]…
    static func argv0(_ pid: pid_t) -> String? {
        var mib = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 4 else { return nil }
        var buf = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0 else { return nil }
        var i = 4
        while i < size, buf[i] != 0 { i += 1 }   // 跳过 exec_path
        while i < size, buf[i] == 0 { i += 1 }    // 跳过对齐的 \0
        let start = i
        while i < size, buf[i] != 0 { i += 1 }    // argv[0]
        guard start < i else { return nil }
        return String(decoding: buf[start..<i], as: UTF8.self)
    }

    /// 进程启动时间。
    static func startTime(_ pid: pid_t) -> Date? {
        var bsd = proc_bsdinfo()
        let r = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bsd, Int32(MemoryLayout<proc_bsdinfo>.size))
        guard r > 0 else { return nil }
        return Date(timeIntervalSince1970: Double(bsd.pbi_start_tvsec) + Double(bsd.pbi_start_tvusec) / 1_000_000)
    }

    /// cwd -> Claude Code 项目目录名：非字母数字一律替换为 '-'。
    /// 例：/Users/altria/projects -> -Users-altria-projects
    static func projectDirName(for cwd: String) -> String {
        String(cwd.map { ($0.isLetter || $0.isNumber) ? $0 : "-" })
    }

    /// 进程的控制终端设备名，如 /dev/ttys001。
    static func tty(_ pid: pid_t) -> String? {
        var bsd = proc_bsdinfo()
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bsd, Int32(MemoryLayout<proc_bsdinfo>.size)) > 0 else { return nil }
        let tdev = bsd.e_tdev
        guard tdev != 0, tdev != UInt32.max else { return nil }
        guard let namePtr = devname(dev_t(bitPattern: tdev), mode_t(S_IFCHR)) else { return nil }
        return "/dev/" + String(cString: namePtr)
    }

    /// 解析进程环境变量（从 KERN_PROCARGS2 缓冲区，跳过 argc 个 argv 后即为 env）。
    static func envVars(_ pid: pid_t) -> [String: String] {
        var mib = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 4 else { return [:] }
        var buf = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0 else { return [:] }
        let argc = Int(buf.withUnsafeBytes { $0.load(as: Int32.self) })
        var i = 4
        while i < size, buf[i] != 0 { i += 1 }   // 跳过 exec_path
        while i < size, buf[i] == 0 { i += 1 }    // 跳过对齐
        var read = 0                              // 跳过 argc 个参数
        while i < size, read < argc {
            while i < size, buf[i] != 0 { i += 1 }
            i += 1; read += 1
        }
        var env: [String: String] = [:]           // 余下为 env
        while i < size {
            while i < size, buf[i] == 0 { i += 1 }
            if i >= size { break }
            let start = i
            while i < size, buf[i] != 0 { i += 1 }
            let s = String(decoding: buf[start..<i], as: UTF8.self)
            if let eq = s.firstIndex(of: "=") {
                env[String(s[..<eq])] = String(s[s.index(after: eq)...])
            }
            i += 1
        }
        return env
    }

    /// 沿父进程链向上找到第一个 .app 祖先（即承载的终端 app）。
    static func terminalAppURL(_ pid: pid_t) -> URL? {
        var cur = pid
        for _ in 0..<8 {
            var bsd = proc_bsdinfo()
            guard proc_pidinfo(cur, PROC_PIDTBSDINFO, 0, &bsd, Int32(MemoryLayout<proc_bsdinfo>.size)) > 0 else { return nil }
            let ppid = pid_t(bsd.pbi_ppid)
            guard ppid > 1 else { return nil }
            var pathBuf = [CChar](repeating: 0, count: 4096)
            if proc_pidpath(ppid, &pathBuf, 4096) > 0 {
                let path = String(cString: pathBuf)
                if let r = path.range(of: ".app/Contents/MacOS/") {
                    return URL(fileURLWithPath: String(path[..<r.lowerBound]) + ".app")
                }
            }
            cur = ppid
        }
        return nil
    }

    /// 综合 env + tty + 父链，构造跳转目标。
    static func makeJump(pid: pid_t) -> JumpTarget {
        let env = envVars(pid)
        let termProgram = env["TERM_PROGRAM"] ?? ""
        let kind: TerminalKind
        switch termProgram {
        case "WarpTerminal":   kind = .warp
        case "iTerm.app":      kind = .iterm
        case "Apple_Terminal": kind = .terminalApp
        case "vscode":         kind = .vscode
        case "ghostty":        kind = .ghostty
        case "WezTerm":        kind = .wezterm
        default:
            if env["KITTY_WINDOW_ID"] != nil || (env["TERM"] ?? "").contains("kitty") { kind = .kitty }
            else { kind = .unknown }
        }
        return JumpTarget(kind: kind, tty: tty(pid),
                          warpFocusURL: env["WARP_FOCUS_URL"],
                          appURL: terminalAppURL(pid))
    }

    /// 所有运行中的 claude CLI 进程（含 cwd 与启动时间）。
    static func liveClaudeProcesses() -> [LiveClaude] {
        let needed = proc_listpids(UInt32(PROC_ALL_PIDS), 0, nil, 0)
        guard needed > 0 else { return [] }
        let capacity = Int(needed) / MemoryLayout<pid_t>.size + 16
        var pids = [pid_t](repeating: 0, count: capacity)
        let got = proc_listpids(UInt32(PROC_ALL_PIDS), 0, &pids, Int32(capacity * MemoryLayout<pid_t>.size))
        guard got > 0 else { return [] }
        let count = Int(got) / MemoryLayout<pid_t>.size

        var result: [LiveClaude] = []
        for i in 0..<count {
            let pid = pids[i]
            guard pid > 0 else { continue }

            // 主判据：argv[0] 命令名为 claude（不依赖版本/安装路径）。
            // 兜底：可执行路径含 "/claude/"（覆盖 argv[0] 读不到的情况）。
            let cmd = argv0(pid).map { ($0 as NSString).lastPathComponent }
            var isClaude = (cmd == "claude")
            if !isClaude {
                var pathBuf = [CChar](repeating: 0, count: 4096)
                if proc_pidpath(pid, &pathBuf, 4096) > 0 {
                    let path = String(cString: pathBuf).lowercased()
                    isClaude = path.contains("/claude/")
                }
            }
            guard isClaude else { continue }

            var info = proc_vnodepathinfo()
            let r = proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, Int32(MemoryLayout<proc_vnodepathinfo>.size))
            guard r > 0 else { continue }
            let cwd = withUnsafeBytes(of: &info.pvi_cdir.vip_path) {
                String(cString: $0.bindMemory(to: CChar.self).baseAddress!)
            }
            guard !cwd.isEmpty else { continue }

            result.append(LiveClaude(pid: pid, cwd: cwd,
                                     dirName: projectDirName(for: cwd),
                                     startTime: startTime(pid) ?? .distantPast,
                                     jump: makeJump(pid: pid)))
        }
        return result
    }
}
