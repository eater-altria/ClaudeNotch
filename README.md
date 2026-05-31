# ClaudeNotch

> 一个 macOS 状态栏 / 刘海挂件（伪「灵动岛」），实时显示 Claude Code 订阅额度与本机正在运行的会话花费、上下文占用。

A macOS menu-bar / notch widget that shows your Claude Code subscription usage, plus the live cost and context usage of every running Claude Code session on your machine.

![screenshot](docs/screenshot.png)

## 功能

- **订阅额度环**：当前会话 / 本周·全模型 / 本周·Sonnet 三个环，显示剩余百分比、刷新倒计时，并按消耗速率推算「预计用完时间」。
- **活跃会话列表**：自动发现本机正在运行的 `claude` 进程，逐个显示
  - **花费**（≈ 按 API 单价折算的等价成本，已按 `messageId` 去重并计入 `subagents` 子代理）
  - **上下文占用**（环形图 + token 数 / 窗口）
  - 项目名、git 分支、模型
- **灵动岛交互**：贴住屏幕顶端（有刘海贴刘海，无刘海在顶部正中模拟），鼠标移上去展开。
- **设置**：配色（日间 / 夜间 / 跟随系统）、开机自启、灵动岛开关。
- 环形颜色从绿到红连续渐变，越满越红。

## 数据来源

- **订阅额度**：用一个隐藏的 `WKWebView` 让你**正常登录 claude.ai**，加载 `claude.ai/settings/usage` 后读取页面数据。凭证全程留在系统 WebKit 里，本 app **不存储、不读取、不外传**。
- **会话花费 / 上下文**：解析本地 `~/.claude/projects/**/*.jsonl`（Claude Code 自己的会话记录），在本机完成，不上传任何数据。

## 系统要求

- macOS 14+（Apple Silicon 或 Intel，提供通用二进制）

## 安装（下载 Release）

1. 从 [Releases](../../releases) 下载 `ClaudeNotch-*.zip` 并解压。
2. 把 `ClaudeNotch.app` 拖到「应用程序」。
3. **首次打开**：由于当前 Release 为 ad-hoc 签名（未做 Apple 公证），Gatekeeper 会拦截。任选其一放行：
   - 右键点 app → 打开 → 在弹窗里再点「打开」；或
   - 终端执行：
     ```bash
     xattr -dr com.apple.quarantine /Applications/ClaudeNotch.app
     ```
4. 打开后在**状态栏**出现仪表盘图标，顶部出现挂件。把鼠标移到挂件上展开；额度区点「登录 Claude」用**邮箱**登录（内嵌浏览器登录 Google 常被判定不安全，建议邮箱登录）。

## 从源码构建

```bash
make run          # 构建 + 组装 .app + ad-hoc 签名 + 启动
make universal    # 构建通用二进制（arm64 + x86_64）
make dist DEV_ID="Developer ID Application: Your Name (TEAMID)"   # 签名+公证+DMG（需开发者账号）
```

也可直接 `open Package.swift` 在 Xcode 里打开。

## 说明 / 免责

- **「花费」是 API 等价估算**：Max/Pro 订阅并不按 token 单独计费，这里显示的是「这些 token 若按 API 单价值多少钱」（类似 [ccusage](https://github.com/ryoppippi/ccusage)）。单价为 Anthropic 公开价的近似，可能随时间变化。
- 额度抓取依赖你自己的 claude.ai 网页会话。请自行评估是否符合 Anthropic 的服务条款；本项目仅供个人学习与自用，使用风险自负。
- claude.ai 用量页结构变化可能导致抓取失效，需要相应更新选择器。

## 致谢

额度抓取思路参考自 [mnapoli/claude-usage-bar](https://github.com/mnapoli/claude-usage-bar)。

## License

[MIT](LICENSE)
