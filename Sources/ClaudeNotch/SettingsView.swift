import SwiftUI
import AppKit

struct SettingsView: View {
    @ObservedObject var settings: SettingsStore

    var body: some View {
        Form {
            Section("外观") {
                Picker("配色", selection: $settings.appearance) {
                    ForEach(AppearanceMode.allCases) { mode in
                        Text(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
            }

            Section("通用") {
                Toggle("开机自启动", isOn: $settings.launchAtLogin)
                Toggle("启用灵动岛", isOn: $settings.islandEnabled)
                Toggle("接管 Claude Code 的 statusLine", isOn: $settings.manageStatusline)
                Text("关闭后不再改写 ~/.claude/settings.json，额度停留在最后一次（会标记为过期）。")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("集成状态") {
                IntegrationStatusSection(settings: settings)
            }

            Section("通知") {
                Toggle("额度 / 上下文通知", isOn: $settings.notificationsEnabled)
                Group {
                    // 两档不可交叉：提示档上界 = 严重档-5，严重档下界 = 提示档+5，恒满足 提示 < 严重。
                    Stepper("提示档（静默）\(settings.quotaWarnThreshold)%",
                            value: $settings.quotaWarnThreshold,
                            in: 50...max(55, settings.quotaCriticalThreshold - 5), step: 5)
                    Stepper("严重档（出声）\(settings.quotaCriticalThreshold)%",
                            value: $settings.quotaCriticalThreshold,
                            in: min(99, settings.quotaWarnThreshold + 5)...99, step: 1)
                    Stepper("会话上下文告警 \(settings.contextThreshold)%",
                            value: $settings.contextThreshold, in: 70...99, step: 5)
                    Toggle("严重档提示音", isOn: $settings.criticalSoundEnabled)
                }
                .disabled(!settings.notificationsEnabled)
                Text("额度跨过提示档静默提醒、跨过严重档出声；会话上下文到达阈值时建议 /compact。")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("显示器") {
                Text("勾选在哪些显示器上显示挂件；都不选 = 自动（刘海屏 / 主屏）。")
                    .font(.caption).foregroundStyle(.secondary)
                ForEach(settings.screenOptions) { opt in
                    Toggle(opt.label, isOn: Binding(
                        get: { settings.selectedScreens.contains(opt.id) },
                        set: { on in
                            if on { settings.selectedScreens.insert(opt.id) }
                            else { settings.selectedScreens.remove(opt.id) }
                        }
                    ))
                }
            }

            Section {
                Text("「花费」按 API 单价折算估计，订阅用户并不按此单独计费；"
                     + "若 Claude Code 提供了官方花费，会在挂件里以「最近会话官方花费」展示。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 420, height: 560)
    }
}

/// 集成状态 / 诊断：把 statusLine 钩子的真实状态摊开，让「它没工作」可自查、可复制。
struct IntegrationStatusSection: View {
    @ObservedObject var settings: SettingsStore
    @State private var diag: StatuslineHook.Diagnostics?
    @State private var liveCount: Int = 0
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let d = diag {
                row("接入状态", d.installed ? "已接入 ✓" : "未接入 ✗",
                    color: d.installed ? .green : .orange)
                row("额度数据", capturedText(d))
                row("运行中的 claude", "\(liveCount) 个")
                if let inner = d.wrappedInner {
                    row("已透传原命令", inner, mono: true)
                }
                if let cmd = d.command {
                    row("当前命令", cmd, mono: true)
                }
            } else {
                Text("读取中…").font(.caption).foregroundStyle(.secondary)
            }

            HStack(spacing: 10) {
                Button("重新接入") {
                    settings.statuslineConsented = true
                    settings.manageStatusline = true   // didSet → AppDelegate 重新 ensureInstalled
                    reload()
                }
                Button("在 Finder 中显示") {
                    if let p = diag?.supportDirPath {
                        try? FileManager.default.createDirectory(atPath: p, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(URL(fileURLWithPath: p))
                    }
                }
                Button(copied ? "已复制 ✓" : "复制诊断信息") {
                    if let t = diag?.copyText {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(t, forType: .string)
                        copied = true
                    }
                }
                Spacer()
                Button { reload() } label: { Image(systemName: "arrow.clockwise") }
                    .help("刷新")
            }
            .controlSize(.small)

            Text("额度来自 Claude Code 的 statusLine 钩子（不抓网页、不复用令牌）；仅在 Claude Code 运行时更新。")
                .font(.caption).foregroundStyle(.secondary)
        }
        .onAppear(perform: reload)
    }

    private func reload() {
        copied = false
        diag = StatuslineHook.diagnostics()   // 本地小文件读，便宜
        // 进程表全量扫描较重（遍历所有 PID + 多次 sysctl），放后台，回主线程赋值。
        DispatchQueue.global(qos: .userInitiated).async {
            let n = ProcessProbe.liveClaudeProcesses().count
            DispatchQueue.main.async { liveCount = n }
        }
    }

    private func capturedText(_ d: StatuslineHook.Diagnostics) -> String {
        guard d.ratelimitsExists, let c = d.capturedAt else { return "尚无（去终端跑一次 claude）" }
        let secs = Date().timeIntervalSince(c)
        let f = DateFormatter(); f.dateFormat = "MM-dd HH:mm"
        let stale = secs > 30 * 60
        return f.string(from: c) + (stale ? "（可能已过期）" : "")
    }

    private func row(_ key: String, _ value: String, color: Color? = nil, mono: Bool = false) -> some View {
        HStack(alignment: .top) {
            Text(key).font(.caption).foregroundStyle(.secondary).frame(width: 100, alignment: .leading)
            Text(value)
                .font(mono ? .system(size: 10, design: .monospaced) : .caption)
                .foregroundStyle(color ?? .primary)
                .textSelection(.enabled)
                .lineLimit(mono ? 2 : 1).truncationMode(.middle)
            Spacer(minLength: 0)
        }
    }
}
