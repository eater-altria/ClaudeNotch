# CLAUDE.md

ClaudeNotch —— 显示 Claude Code 订阅额度 + 本机运行中会话花费/上下文占用的桌面挂件。
**双平台 monorepo**：

| 目录 | 平台 | 技术栈 | 详细文档 |
|---|---|---|---|
| `macos/` | macOS 14+ | Swift / SwiftUI / AppKit（无 Xcode 工程，SwiftPM + Makefile） | [`macos/CLAUDE.md`](macos/CLAUDE.md) |
| `windows/` | Windows 10/11 | C# / .NET 8 / WPF（原生，非 Electron） | [`windows/CLAUDE.md`](windows/CLAUDE.md) |

两端共享同一套**数据来源与口径**，行为尽量对齐：

- **额度来源 = Claude Code 的 `statusLine` 钩子**：app 把自己注册成 Claude Code 的 statusLine 命令，
  Claude Code 渲染状态栏时把 `rate_limits` 经 stdin 喂给 `--statusline` 助手，落盘后读取。**不抓网页、不复用 OAuth 令牌**（合规结论见 `macos/CLAUDE.md`）。
- **花费**：transcript 无 cost 字段，按 `usage` token × 模型单价自算；按 `message.id` 去重；递归计入 `subagents/**`。
  价表对接 **LiteLLM** 公开数据（内置快照 + 每周刷新）+ 用户手动覆盖；详见 `macos/CLAUDE.md` 的「非显然知识」。
- **Claude 数据目录**：macOS `~/.claude/`，Windows `%USERPROFILE%\.claude\`（`projects/**/*.jsonl`、`settings.json` 同构）。
- **i18n**：中/英双语，默认跟随系统、匹配不到默认英语；货币英文 `$`、中文 `¥`（实时汇率换算）。

## 构建

- **macOS**：`cd macos && make run`（详见 `macos/CLAUDE.md`）。
- **Windows**：本仓库**无本地 Windows 环境**，编译走 **GitHub Actions**（`.github/workflows/windows.yml`，`windows-latest` 上 `dotnet publish`，产物为自包含 exe）。本地只能改代码 + 推送，靠 CI 验证编译。

## 平台差异（Windows 版的有意取舍）

- **无刘海**：Windows 把 macOS 的「刘海挂件」改成**置顶可拖拽的悬浮窗**，点击同样展开 Mac 风格面板（渐变环 + 会话列表）。
- **托盘图标**功能与 Mac 菜单一致：设置 / 数据统计 / 立即刷新 / 检查更新 / 退出。
