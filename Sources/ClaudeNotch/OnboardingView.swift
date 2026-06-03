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
                    Text("欢迎使用 ClaudeNotch").font(.title2.bold())
                    Text("在刘海 / 状态栏显示 Claude Code 订阅额度与会话花费")
                        .font(.subheadline).foregroundStyle(.secondary)
                }
            }

            Divider()

            VStack(alignment: .leading, spacing: 9) {
                Text("为读取额度，ClaudeNotch 需要把自己注册成 Claude Code 的 statusLine 命令：")
                    .font(.system(size: 12.5, weight: .medium))
                bullet("写入 ~/.claude/settings.json 的 statusLine 字段（首次会整文件备份一次）")
                bullet("你原有的 statusline 会被原样透传、不被抢占；退出 ClaudeNotch 时自动还原")
                bullet("只读取 Claude Code 主动经 stdin 喂来的额度 / 花费——不抓网页、不复用任何登录令牌")
            }

            GroupBox {
                VStack(alignment: .leading, spacing: 6) {
                    diffRow("当前", existingCommand ?? "（无 statusLine）")
                    diffRow("之后", "ClaudeNotch --statusline" + (existingCommand == nil ? "" : "（包裹上面的命令）"))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Text("随时可在 设置 → 通用 关闭接管，或在 集成状态 里重新接入 / 查看诊断。")
                .font(.caption).foregroundStyle(.secondary)

            Spacer(minLength: 0)

            HStack {
                Button("暂不接入") { onSkip() }
                Spacer()
                Button("接入并继续") { onContinue() }
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
