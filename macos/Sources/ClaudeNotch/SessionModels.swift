import Foundation
import SwiftUI

/// 会话所属终端类型（用于跳转到对应 tab）
enum TerminalKind {
    case warp, terminalApp, iterm, kitty, wezterm, ghostty, vscode, unknown
}

/// 跳转到会话所在终端 tab 所需的信息。
struct JumpTarget {
    let kind: TerminalKind
    let tty: String?            // /dev/ttysNNN（Terminal.app / iTerm2 按此匹配 tab）
    let warpFocusURL: String?   // warp://session/<uuid>（Warp 精确跳转）
    let appURL: URL?            // 终端 .app 路径（兜底激活）
}

/// 一个活跃 Claude Code 会话的汇总信息（从 transcript JSONL 解析得出）。
struct SessionInfo: Identifiable {
    let id: String              // sessionId（缺失时退化为文件名）
    let projectName: String     // cwd 的最后一段
    let cwd: String
    let gitBranch: String?
    let model: String
    let costUSD: Double          // 按 API 单价折算的等价花费（订阅用户并不按此单独计费）
    let contextTokens: Int       // 当前上下文占用（最近一次请求的总输入）
    let peakContextTokens: Int   // 本会话历史峰值上下文（看离溢出多近 / compact 是否生效）
    let contextWindow: Int       // 上下文窗口（环形图分母）
    let lastActivity: Date
    var jump: JumpTarget? = nil  // 跳转目标（由进程匹配后附加）

    var contextPercent: Int {
        guard contextWindow > 0 else { return 0 }
        return max(0, min(100, Int((Double(contextTokens) / Double(contextWindow) * 100).rounded())))
    }

    /// 峰值上下文占比（环形上的峰值刻度用）。
    var peakContextPercent: Int {
        guard contextWindow > 0 else { return 0 }
        return max(0, min(100, Int((Double(peakContextTokens) / Double(contextWindow) * 100).rounded())))
    }

    /// 峰值是否明显高于当前（如 /compact 后回落）——决定是否值得单独展示「峰」。
    var hasMeaningfulPeak: Bool { peakContextTokens > contextTokens }

    /// 模型短名：claude-opus-4-8 -> Opus 4.8，claude-fable-5 -> Fable 5
    var modelShort: String {
        let m = model.lowercased()
        func ver() -> String {
            // 抓 “4-8” / “4-6” 这类；单段版本号（fable-5）退化抓首个数字
            if let r = model.range(of: #"\d+-\d+"#, options: .regularExpression) {
                return String(model[r]).replacingOccurrences(of: "-", with: ".")
            }
            if let r = model.range(of: #"\d+"#, options: .regularExpression) {
                return String(model[r])
            }
            return ""
        }
        let v = ver()
        if m.contains("fable") { return "Fable \(v)".trimmingCharacters(in: .whitespaces) }
        if m.contains("opus") { return "Opus \(v)".trimmingCharacters(in: .whitespaces) }
        if m.contains("sonnet") { return "Sonnet \(v)".trimmingCharacters(in: .whitespaces) }
        if m.contains("haiku") { return "Haiku \(v)".trimmingCharacters(in: .whitespaces) }
        return model
    }
}

/// 各模型的 API 单价（$/MTok）与默认上下文窗口。
/// 价格为 Anthropic 公开单价的近似值，可能随时间漂移，仅作折算估计。
struct ModelPricing {
    let input: Double
    let output: Double
    let cacheRead: Double
    let cacheWrite5m: Double
    let cacheWrite1h: Double
    let window: Int

    /// 优先用 LiteLLM 在线价表（真实单价，含第三方子代理模型）；未命中再退回本地按族近似。
    static func lookup(_ model: String) -> ModelPricing {
        PriceCatalog.shared.match(model) ?? fallback(model)
    }

    /// 本地硬编码兜底（无网络 / 价表未命中时用）。Claude 三族与 LiteLLM 一致，未知模型按 Sonnet 近似。
    static func fallback(_ model: String) -> ModelPricing {
        let m = model.lowercased()
        // OpenAI / Codex 家族（LiteLLM 命中时优先用其真实价，此处仅离线/未命中兜底）。
        // cacheWrite 对 OpenAI 无意义，置 0；window 取各族公开上下文窗口。
        if m.contains("gpt-5") || m.contains("codex") {
            if m.contains("nano") { return ModelPricing(input: 0.05, output: 0.40, cacheRead: 0.005, cacheWrite5m: 0, cacheWrite1h: 0, window: 400_000) }
            if m.contains("mini") { return ModelPricing(input: 0.25, output: 2.0, cacheRead: 0.025, cacheWrite5m: 0, cacheWrite1h: 0, window: 400_000) }
            return ModelPricing(input: 1.25, output: 10, cacheRead: 0.125, cacheWrite5m: 0, cacheWrite1h: 0, window: 400_000)
        }
        if m.hasPrefix("o3") || m.hasPrefix("o4") || m.contains("gpt-4") {
            if m.contains("mini") { return ModelPricing(input: 1.10, output: 4.40, cacheRead: 0.275, cacheWrite5m: 0, cacheWrite1h: 0, window: 200_000) }
            return ModelPricing(input: 2.0, output: 8.0, cacheRead: 0.5, cacheWrite5m: 0, cacheWrite1h: 0, window: 200_000)
        }
        if m.contains("fable") {
            // Fable 5（Opus 之上的新档）：$10 / $50，cache read $1，5m 写 $12.5，1h 写 $20，1M 上下文
            return ModelPricing(input: 10, output: 50, cacheRead: 1.0, cacheWrite5m: 12.5, cacheWrite1h: 20, window: 1_000_000)
        }
        if m.contains("opus") {
            // Opus 4.x（4.5 起降价）：$5 / $25，cache read $0.5，5m 写 $6.25，1h 写 $10
            return ModelPricing(input: 5, output: 25, cacheRead: 0.5, cacheWrite5m: 6.25, cacheWrite1h: 10, window: 1_000_000)
        }
        if m.contains("sonnet") {
            return ModelPricing(input: 3, output: 15, cacheRead: 0.30, cacheWrite5m: 3.75, cacheWrite1h: 6, window: 200_000)
        }
        if m.contains("haiku") {
            return ModelPricing(input: 1, output: 5, cacheRead: 0.10, cacheWrite5m: 1.25, cacheWrite1h: 2, window: 200_000)
        }
        return ModelPricing(input: 3, output: 15, cacheRead: 0.30, cacheWrite5m: 3.75, cacheWrite1h: 6, window: 200_000)
    }

    /// 按模型族给的默认上下文窗口（仅供价表构建时取窗口；与 `fallback` 同口径）。
    static func fallbackWindow(_ model: String) -> Int { fallback(model).window }
}

/// 上下文占用配色：越满越警示（与“剩余容量”相反）。
func contextColor(_ percent: Int) -> Color {
    if percent >= 90 { return Color(red: 0.95, green: 0.30, blue: 0.30) }
    if percent >= 75 { return Color(red: 0.98, green: 0.67, blue: 0.20) }
    return Color(red: 0.35, green: 0.72, blue: 0.95)   // teal/blue
}

/// token 数友好显示：177787 -> "178k"，1000000 -> "1M"
func formatTokens(_ n: Int) -> String {
    if n >= 1_000_000 {
        let v = Double(n) / 1_000_000
        return (v >= 10 ? String(format: "%.0fM", v) : String(format: "%.1fM", v))
    }
    if n >= 1_000 {
        return "\(Int((Double(n) / 1000).rounded()))k"
    }
    return "\(n)"
}
