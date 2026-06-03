import SwiftUI
import AppKit

struct SettingsView: View {
    @ObservedObject var settings: SettingsStore
    @ObservedObject var priceStore: ModelPriceStore
    @ObservedObject var rateStore: ExchangeRateStore
    @ObservedObject private var loc = Localization.shared   // 语言一变即重建整个设置界面

    var body: some View {
        Form {
            Section(tr("外观", "Appearance")) {
                Picker(tr("配色", "Theme"), selection: $settings.appearance) {
                    ForEach(AppearanceMode.allCases) { mode in
                        Text(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
                Picker(tr("语言", "Language"), selection: $loc.preference) {
                    ForEach(LanguagePreference.allCases) { p in Text(p.label).tag(p) }
                }
            }

            Section(tr("通用", "General")) {
                Toggle(tr("开机自启动", "Launch at login"), isOn: $settings.launchAtLogin)
                Toggle(tr("启用灵动岛", "Enable Dynamic Island"), isOn: $settings.islandEnabled)
                Toggle(tr("接管 Claude Code 的 statusLine", "Manage Claude Code's statusLine"), isOn: $settings.manageStatusline)
                Text(tr("关闭后不再改写 ~/.claude/settings.json，额度停留在最后一次（会标记为过期）。",
                        "When off, ~/.claude/settings.json is left untouched and quota stays at the last value (marked stale)."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section(tr("集成状态", "Integration")) {
                IntegrationStatusSection(settings: settings)
            }

            Section(tr("通知", "Notifications")) {
                Toggle(tr("额度 / 上下文通知", "Quota / context alerts"), isOn: $settings.notificationsEnabled)
                Group {
                    // 两档不可交叉：提示档上界 = 严重档-5，严重档下界 = 提示档+5，恒满足 提示 < 严重。
                    Stepper(tr("提示档（静默）", "Warning (silent) ") + "\(settings.quotaWarnThreshold)%",
                            value: $settings.quotaWarnThreshold,
                            in: 50...max(55, settings.quotaCriticalThreshold - 5), step: 5)
                    Stepper(tr("严重档（出声）", "Critical (sound) ") + "\(settings.quotaCriticalThreshold)%",
                            value: $settings.quotaCriticalThreshold,
                            in: min(99, settings.quotaWarnThreshold + 5)...99, step: 1)
                    Stepper(tr("会话上下文告警 ", "Session context alert ") + "\(settings.contextThreshold)%",
                            value: $settings.contextThreshold, in: 70...99, step: 5)
                    Toggle(tr("严重档提示音", "Critical alert sound"), isOn: $settings.criticalSoundEnabled)
                }
                .disabled(!settings.notificationsEnabled)
                Text(tr("额度跨过提示档静默提醒、跨过严重档出声；会话上下文到达阈值时建议 /compact。",
                        "Crossing the warning level notifies silently, the critical level with sound; at the context threshold it suggests /compact."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section(tr("显示器", "Displays")) {
                Text(tr("勾选在哪些显示器上显示挂件；都不选 = 自动（刘海屏 / 主屏）。",
                        "Pick which displays show the widget; none selected = automatic (notch / main screen)."))
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

            Section(tr("模型价格", "Model pricing")) {
                ModelPriceSection(priceStore: priceStore)
            }

            Section(tr("货币与汇率", "Currency & exchange rate")) {
                CurrencySection(rateStore: rateStore)
            }

            Section {
                Text(tr("「花费」按 API 单价折算估计，订阅用户并不按此单独计费；"
                        + "若 Claude Code 提供了官方花费，会在挂件里以「最近会话官方花费」展示。",
                        "“Cost” is an estimate from API list prices; subscription users aren’t billed this way. "
                        + "If Claude Code reports an official cost, the widget shows it as “latest session official cost.”"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 420, height: 580)
    }
}

/// 模型价格：展示 LiteLLM 价表状态（来源/更新时间/模型数）并提供手动刷新。
struct ModelPriceSection: View {
    @ObservedObject var priceStore: ModelPriceStore
    @ObservedObject private var loc = Localization.shared
    @State private var awaitingReload = false   // 点过「编辑」后置真，提醒去刷新；刷新后清掉

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(tr("已载入 \(priceStore.modelCount) 个模型单价", "\(priceStore.modelCount) model prices loaded")
                     + (priceStore.overrideCount > 0
                        ? tr("（含 \(priceStore.overrideCount) 条手动覆盖）", " (incl. \(priceStore.overrideCount) manual override(s))")
                        : ""))
                    .font(.caption)
                Spacer()
                if priceStore.isRefreshing {
                    ProgressView().controlSize(.small)
                } else {
                    Button(tr("刷新价格", "Refresh prices")) { priceStore.refresh(); awaitingReload = false }
                        .controlSize(.small)
                        .tint(awaitingReload ? .accentColor : nil)
                }
            }
            if let err = priceStore.lastError {
                Text(tr("刷新失败：", "Refresh failed: ") + err).font(.caption).foregroundStyle(.orange)
            }
            Text(updatedText).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 10) {
                Button(tr("编辑价格覆盖…", "Edit price overrides…")) { priceStore.openOverridesForEditing(); awaitingReload = true }
                    .controlSize(.small)
                Spacer()
            }
            if awaitingReload {
                Label(tr("改完保存后，请点上面的「刷新价格」才会生效。",
                         "After saving, click “Refresh prices” above to apply."), systemImage: "arrow.up.circle")
                    .font(.caption).foregroundStyle(.orange)
            }
            Text(tr("第三方子代理模型（如 mimo）已用 LiteLLM 真实单价；表里没有的型号（如 deepseek-v4-pro）"
                    + "默认按 Sonnet 近似（标「估」），可在「价格覆盖」里手填真实单价。价表来自 BerriAI/litellm 公开数据，每周自动刷新。",
                    "Third-party subagent models (e.g. mimo) now use real LiteLLM prices; models not in the table (e.g. deepseek-v4-pro) "
                    + "default to a Sonnet approximation (marked “est.”) — set their real price under “price overrides.” The table comes from BerriAI/litellm public data, auto-refreshed weekly."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var updatedText: String {
        guard let d = priceStore.lastUpdated else { return tr("来源：内置快照（尚未联网刷新）", "Source: bundled snapshot (not yet refreshed online)") }
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm"
        return tr("已联网更新于 ", "Updated online at ") + f.string(from: d)
    }
}

/// 货币与汇率：显示当前货币口径、USD→CNY 汇率与更新时间，提供手动刷新。
struct CurrencySection: View {
    @ObservedObject var rateStore: ExchangeRateStore
    @ObservedObject private var loc = Localization.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(LocalizationState.currentLanguage == .zh
                     ? "金额按人民币（¥）显示，1 USD = ¥\(String(format: "%.4f", rateStore.rate))"
                     : "Amounts shown in US dollars ($)")
                    .font(.caption)
                Spacer()
                if rateStore.isRefreshing {
                    ProgressView().controlSize(.small)
                } else {
                    Button(tr("刷新汇率", "Refresh rate")) { rateStore.refresh() }.controlSize(.small)
                }
            }
            if let err = rateStore.lastError {
                Text(tr("刷新失败：", "Refresh failed: ") + err).font(.caption).foregroundStyle(.orange)
            }
            Text(rateUpdatedText).font(.caption).foregroundStyle(.secondary)
            Text(tr("中文界面用人民币、英文界面用美元。汇率来自 open.er-api.com 公开数据，每周自动刷新。",
                    "Chinese UI uses CNY, English UI uses USD. Exchange rate from open.er-api.com public data, auto-refreshed weekly."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var rateUpdatedText: String {
        guard let d = rateStore.lastUpdated else { return tr("汇率：内置默认值（尚未联网刷新）", "Rate: built-in default (not yet refreshed online)") }
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm"
        return tr("汇率更新于 ", "Rate updated at ") + f.string(from: d)
    }
}

/// 集成状态 / 诊断：把 statusLine 钩子的真实状态摊开，让「它没工作」可自查、可复制。
struct IntegrationStatusSection: View {
    @ObservedObject var settings: SettingsStore
    @ObservedObject private var loc = Localization.shared
    @State private var diag: StatuslineHook.Diagnostics?
    @State private var liveCount: Int = 0
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let d = diag {
                row(tr("接入状态", "Status"), d.installed ? tr("已接入 ✓", "Installed ✓") : tr("未接入 ✗", "Not installed ✗"),
                    color: d.installed ? .green : .orange)
                row(tr("额度数据", "Quota data"), capturedText(d))
                row(tr("运行中的 claude", "Running claude"), tr("\(liveCount) 个", "\(liveCount)"))
                if let inner = d.wrappedInner {
                    row(tr("已透传原命令", "Wrapped command"), inner, mono: true)
                }
                if let cmd = d.command {
                    row(tr("当前命令", "Current command"), cmd, mono: true)
                }
            } else {
                Text(tr("读取中…", "Loading…")).font(.caption).foregroundStyle(.secondary)
            }

            HStack(spacing: 10) {
                Button(tr("重新接入", "Reinstall")) {
                    settings.statuslineConsented = true
                    settings.manageStatusline = true   // didSet → AppDelegate 重新 ensureInstalled
                    reload()
                }
                Button(tr("在 Finder 中显示", "Show in Finder")) {
                    if let p = diag?.supportDirPath {
                        try? FileManager.default.createDirectory(atPath: p, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(URL(fileURLWithPath: p))
                    }
                }
                Button(copied ? tr("已复制 ✓", "Copied ✓") : tr("复制诊断信息", "Copy diagnostics")) {
                    if let t = diag?.copyText {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(t, forType: .string)
                        copied = true
                    }
                }
                Spacer()
                Button { reload() } label: { Image(systemName: "arrow.clockwise") }
                    .help(tr("刷新", "Refresh"))
            }
            .controlSize(.small)

            Text(tr("额度来自 Claude Code 的 statusLine 钩子（不抓网页、不复用令牌）；仅在 Claude Code 运行时更新。",
                    "Quota comes from Claude Code's statusLine hook (no web scraping, no token reuse); updates only while Claude Code runs."))
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
        guard d.ratelimitsExists, let c = d.capturedAt else { return tr("尚无（去终端跑一次 claude）", "None yet (run claude once in a terminal)") }
        let secs = Date().timeIntervalSince(c)
        let f = DateFormatter(); f.dateFormat = "MM-dd HH:mm"
        let stale = secs > 30 * 60
        return f.string(from: c) + (stale ? tr("（可能已过期）", " (may be stale)") : "")
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
