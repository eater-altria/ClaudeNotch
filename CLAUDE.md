# CLAUDE.md

ClaudeNotch —— 显示 Claude Code 订阅额度 + 本机运行中会话花费/上下文占用的桌面挂件。
**双平台 monorepo**：

| 目录 | 平台 | 技术栈 | 详细文档 |
|---|---|---|---|
| `macos/` | macOS 14+ | Swift / SwiftUI / AppKit（无 Xcode 工程，SwiftPM + Makefile） | [`macos/CLAUDE.md`](macos/CLAUDE.md) |
| `windows/` | Windows 10/11 | C# / .NET 8 / WPF（原生，非 Electron） | [`windows/CLAUDE.md`](windows/CLAUDE.md) |

两端共享同一套**数据来源与口径**，行为尽量对齐：

- **双代理（Claude Code / Codex）**：设置页可切换「监控对象」。两个代理落到**同一套额度/会话/历史模型**上。
  当前代理存全局 `AgentContext.current`（Swift）/ `AgentContext.Current`（C#，默认 Claude Code），扫描线程只读它，各 reader 按它分支。
  Codex 专属逻辑集中在 `CodexSupport.swift` / `Core/Codex.cs`，其余文件只做最小分支。
- **Claude 额度来源 = `statusLine` 钩子**：app 把自己注册成 Claude Code 的 statusLine 命令，
  Claude Code 渲染状态栏时把 `rate_limits` 经 stdin 喂给 `--statusline` 助手，落盘后读取。**不抓网页、不复用 OAuth 令牌**（合规结论见 `macos/CLAUDE.md`）。
- **Codex 额度来源 = 会话 JSONL 内嵌的 `rate_limits`**：Codex **无 statusLine 钩子**，额度随每轮响应写进
  `~/.codex/sessions/**/rollout-*.jsonl` 的 `token_count` 事件（`rate_limits.{primary,secondary}`：`used_percent` 0–100、`resets_at` Unix 秒、`window_minutes` 区分 5h/周）。
  取最新 mtime 文件里最后一条带 rate_limits 的事件，按 `window_minutes` 升序映射为「当前会话(5h)/本周」。**Codex 模式不装任何钩子，直接读文件。**
- **花费**：两端 transcript 都无 cost 字段，按 `usage` token × 模型单价自算。
  Claude 按 `message.id` 去重、递归计入 `subagents/**`；Codex 每条 `token_count.info.last_token_usage` 即每轮增量(无需去重)，模型取最近 `turn_context.model`，
  `input_tokens` 含缓存→非缓存输入 = input−cached、缓存读 = cached。价表对接 **LiteLLM**（已含 OpenAI 型号）+ 离线兜底(Claude 三族 + gpt-5/codex/o3/o4) + 手动覆盖。
- **数据目录**：Claude macOS `~/.claude/`、Windows `%USERPROFILE%\.claude\`（`projects/**/*.jsonl`、`settings.json`）；
  Codex `~/.codex/`（`CODEX_HOME` 优先），会话在 `sessions/**/rollout-*.jsonl`。历史缓存按代理分文件（`usage-history.json` / `usage-history-codex.json`）。
- **i18n**：中/英双语，默认跟随系统、匹配不到默认英语；货币英文 `$`、中文 `¥`（实时汇率换算）。

## 构建

- **macOS**：`cd macos && make run`（详见 `macos/CLAUDE.md`）。
- **Windows**：本仓库**无本地 Windows 环境**，编译走 **GitHub Actions**（`.github/workflows/windows.yml`，`windows-latest` 上 `dotnet publish`，产物为自包含 exe）。本地只能改代码 + 推送，靠 CI 验证编译。

## 平台差异（Windows 版的有意取舍）

- **无刘海**：Windows 把 macOS 的「刘海挂件」改成**置顶可拖拽的悬浮窗**，点击同样展开 Mac 风格面板（渐变环 + 会话列表）。
- **托盘图标**功能与 Mac 菜单一致：设置 / 数据统计 / 立即刷新 / 检查更新 / 退出。
