import SwiftUI

/// 首次运行的知情同意页：在改写 `~/.claude/settings.json` **之前**，
/// 把「会做什么、不会做什么」摊开给用户，并展示接管前/后的 statusLine 对比。
struct OnboardingView: View {
    /// 接管前 settings.json 里现有的 statusLine 命令（nil = 没有）。
    var existingCommand: String?
    var onContinue: () -> Void
    var onSkip: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 12) {
                Image(systemName: "gauge.with.dots.needle.67percent")
                    .font(.system(size: 34)).foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 2) {
                    Text(tr("欢迎使用 ClaudeNotch", "Welcome to ClaudeNotch")).font(.title2.bold())
                    Text(tr("在刘海 / 状态栏显示 Claude Code 订阅额度与会话花费", "Show Claude Code subscription usage and session cost in the notch / menu bar"))
                        .font(.subheadline).foregroundStyle(.secondary)
                }
            }

            Divider()

            VStack(alignment: .leading, spacing: 9) {
                Text(tr("为读取额度，ClaudeNotch 需要把自己注册成 Claude Code 的 statusLine 命令：", "To read your usage, ClaudeNotch registers itself as Claude Code's statusLine command:"))
                    .font(.system(size: 12.5, weight: .medium))
                bullet(tr("写入 ~/.claude/settings.json 的 statusLine 字段（首次会整文件备份一次）", "Writes the statusLine field in ~/.claude/settings.json (the whole file is backed up once on first run)"))
                bullet(tr("你原有的 statusline 会被原样透传、不被抢占；退出 ClaudeNotch 时自动还原", "Your existing statusline is passed through untouched, not hijacked; it's restored automatically when you quit ClaudeNotch"))
                bullet(tr("只读取 Claude Code 主动经 stdin 喂来的额度 / 花费——不抓网页、不复用任何登录令牌", "Only reads the usage / cost that Claude Code feeds in via stdin — no web scraping, no reuse of any login token"))
            }

            GroupBox {
                VStack(alignment: .leading, spacing: 6) {
                    diffRow(tr("当前", "Now"), existingCommand ?? tr("（无 statusLine）", "(no statusLine)"))
                    diffRow(tr("之后", "After"), "ClaudeNotch --statusline" + (existingCommand == nil ? "" : tr("（包裹上面的命令）", " (wraps the command above)")))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Text(tr("随时可在 设置 → 通用 关闭接管，或在 集成状态 里重新接入 / 查看诊断。", "You can turn this off anytime in Settings → General, or re-connect / view diagnostics under Integration Status."))
                .font(.caption).foregroundStyle(.secondary)

            Spacer(minLength: 0)

            HStack {
                Button(tr("暂不接入", "Not Now")) { onSkip() }
                Spacer()
                Button(tr("接入并继续", "Connect and Continue")) { onContinue() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(22)
        .frame(width: 520, height: 460)
    }

    private func bullet(_ s: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(.green).font(.system(size: 12)).padding(.top, 1.5)
            Text(s).font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func diffRow(_ label: String, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(label).font(.caption).foregroundStyle(.secondary).frame(width: 36, alignment: .leading)
            Text(value).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
