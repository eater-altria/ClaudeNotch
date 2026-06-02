# CLAUDE.md

ClaudeNotch —— macOS 状态栏 / 刘海挂件（伪「灵动岛」），显示 Claude Code 订阅额度 +
本机运行中会话的花费与上下文占用。本文件记录架构、命令，以及一些**花了功夫才搞清楚、不看就会踩坑**的知识。

## 技术栈 / 约定

- **Swift Package**（无 Xcode 工程），`swift-tools-version: 6.0`，**Swift 语言模式 5**（`swiftLanguageMode(.v5)`，刻意避开严格并发）。
- 部署目标 **macOS 14+**；发布出 **arm64 + x86_64 通用二进制**。
- AppKit + SwiftUI + WebKit，大量 `@MainActor`。无第三方依赖。
- 纯命令行可构建：`Package.swift` 也能直接在 Xcode 打开。

## 构建 / 运行 / 发布（Makefile）

```bash
make run          # swift build + 组装 .app + ad-hoc 签名 + open
make bundle       # 同上但不启动
make universal    # arm64+x86_64 通用二进制并组装 .app（发布用）
make dist DEV_ID="Developer ID Application: NAME (TEAMID)"   # 签名+公证+DMG（需开发者账号）
```

调试日志：`CLAUDENOTCH_DEBUG=1 ./ClaudeNotch.app/Contents/MacOS/ClaudeNotch`
（打印刘海几何、触发区、悬停切换、抓取结果等）。注意 `open` **不传环境变量**，调试要直接跑 bundle 内二进制。

发版流程：改 `Resources/Info.plist` 版本号 → commit → `make universal` + ad-hoc 签名 →
`ditto -c -k --keepParent ClaudeNotch.app ClaudeNotch-X.Y.Z-macOS.zip` → `gh release create`。
图标改了跑 `python3 Tools/generate_icons.py`（由 `Resources/AppIconSource.png` 生成 iconset/icns + 程序化画 MenuBarIcon）。

## 模块地图（Sources/ClaudeNotch/）

| 文件 | 职责 |
|---|---|
| `main.swift` / `AppDelegate.swift` | 入口（accessory 策略）；状态栏菜单（动态登录/退出登录、设置）；串联各 store/manager |
| `ClaudeSession.swift` | 隐藏 WKWebView：登录 claude.ai、抓 `settings/usage`、退出登录清 cookie |
| `UsageStore.swift` | 订阅额度状态机 + 5 分钟刷新 + 消耗速率投影 + 额度阈值通知 |
| `UsageModels.swift` | 额度数据/解析/颜色/投影 |
| `SessionMonitor.swift` | `SessionScanner`（扫本地 transcript 算花费/上下文）+ `SessionStore`（30s 轮询 + 上下文告警） |
| `SessionModels.swift` | `SessionInfo`、定价表 `ModelPricing`、`JumpTarget`/`TerminalKind` |
| `ProcessProbe.swift` | libproc 探测运行中的 claude 进程（cwd/启动时间/tty/env/终端类型/跳转目标） |
| `TerminalJumper.swift` | 点会话行跳到对应终端 tab |
| `NotchGeometry.swift` | 每块屏的刘海/菜单栏几何；`NSScreen.uniqueID` 显示器唯一标识 |
| `NotchWindow.swift` | 单块屏的挂件面板 + 悬停判定 |
| `NotchManager.swift` | 每个选中显示器一个挂件（增删/重定位） |
| `NotchView.swift` | SwiftUI 灵动岛视图（渐变环、会话列表） |
| `Theme.swift` | 明暗色板 + 绿→红渐变取色 `rampColor` |
| `SettingsStore.swift` / `SettingsView.swift` | 设置（配色/开机自启/灵动岛开关/通知/多屏多选） |
| `NotificationManager.swift` | UNUserNotificationCenter 封装 |

## ⚠️ 非显然知识（踩过的坑，改之前务必读）

### 1. 花费计算（最容易算错）
- **没有现成 cost 字段**，必须按 `usage` tokens × 各模型单价自己算。
- **Opus 4.x（4.5 起降价）单价**：input **$5** / output **$25** / cache_read **$0.5** / 5m_cache_write **$6.25** / 1h_cache_write **$10**（每 MTok）。Sonnet 3/15/0.3/3.75/6；Haiku 1/5/0.1/1.25/2。
- **必须按 `message.id` 去重**：同一条 API 响应会被写进 transcript **~3 行**（usage 完全相同），不去重会高估约 3 倍。
- **必须递归计入子代理** `<session>/subagents/**/*.jsonl`：通用 Task 子代理在 `subagents/agent-*.jsonl`，workflow 子代理在 `subagents/workflows/<wf>/`。两类都要算，口径才和 `/cost` 一致。
- 现版 Claude Code **不再内联** sidechain 子代理（isSidechain 带 usage 全局为 0），所以不会和 subagents 文件重复计。
- **精度缺口**：子代理可能跑**非 Claude 模型**（如 cheap-coder 的 `mimo-v2.5-pro`），价表只认 opus/sonnet/haiku，未知模型按 Sonnet 近似 → 这种花费偏差。token 数对、单价近似。
- 对订阅用户这是「**≈ API 等价花费**」，并非真实扣费，UI 已标注。

### 2. 识别 claude 进程 / 匹配会话（别用想当然的方式）
- claude 可执行文件名是**版本号**（如 `2.1.158`，装在 `~/.local/share/claude/versions/`），不叫 `claude`。
  识别用 **argv[0]==`claude`**（KERN_PROCARGS2）为主，路径含 `/claude/` 兜底——对版本/安装方式免疫。
- 进程 cwd 用 `proc_pidinfo(PROC_PIDVNODEPATHINFO)`；启动时间用 `PROC_PIDTBSDINFO.pbi_start_tvsec`；
  tty 用 `e_tdev` + `devname`；env 从 KERN_PROCARGS2 跳过 argc 个 argv 后解析。
- **claude 不持续持有 transcript 句柄**（lsof 抓不到），所以没法靠打开的文件映射会话。
- **进程↔transcript 匹配用「进程启动时间 ↔ 文件创建(birth)时间」配对**（容差 5 分钟，贪心唯一分配；resume 的用 mtime 兜底）。
  **绝不能用 mtime 排序取最近 N 个**——同目录两个会话时，关掉刚写过的那个会错杀仍在运行的另一个。
- 「活跃会话」= 有匹配到的活进程，不是「最近写过」。

### 3. 刘海 / 悬停（多次迭代的结论）
- 刘海宽度 = `frame.width - auxiliaryTopLeftArea.width - auxiliaryTopRightArea.width`（夹紧到 100–400，异常回退 200）；
  折叠态宽度在真刘海设备**用检测到的刘海宽**，无刘海用 220。菜单栏高 = `safeAreaInsets.top`(刘海) 或 `frame.maxY - visibleFrame.maxY`。
- 悬停判定**用定时轮询 `NSEvent.mouseLocation`（45ms），不要依赖 mouseMoved 事件**——快速甩动时系统合并/丢事件，会漏掉"停在顶部"那一刻。
- 判定用**固定屏幕矩形**（triggerRect/expandedRect），和窗口动画解耦 → 杜绝"放大→边缘扫过光标→抖动"的反馈环。
- `inTopZone` 用**闭区间 `p.y <= r.maxY`**（maxY=本屏顶）：既能接住 y==maxY 的甩动（CGRect.contains 会把上边当开区间漏掉），又封住上界 → **上方堆叠的另一块屏的光标不会误触发下屏挂件**。

### 4. 多显示器
- **显示器身份必须用 `CGDisplayID`（→ `CGDisplayCreateUUIDFromDisplayID`）**，绝不能用 `localizedName`——同型号两台显示器名字相同会键碰撞、漏屏、无法独立勾选。`localizedName` 只做 UI 标签（同名加序号消歧）。
- `NotchManager` 每个选中屏一个 `NotchWindowController`，按 `uniqueID` 增删。设置里 `selectedScreens` 存 uniqueID 集合；空 = 自动（刘海/主屏）。

### 5. 其它
- **上下文窗口**（环形图分母）transcript 里没记录：按模型默认（opus 1M、sonnet/haiku 200k）+ 启发式（观测峰值 >200k 自动升 1M），并始终显示原始 token 数。
- **终端跳转**：Warp 读 env `WARP_FOCUS_URL` 开 `warp://`（精确、无需授权）；Terminal.app/iTerm2 按 tty 用 AppleScript（首次需「自动化」TCC 授权）；其它兜底激活 app。
- **额度抓取**靠用户自己的 claude.ai 网页会话（WKWebView）。凭证只在 WebKit 里，app 不存不传。
  注意 Anthropic 2026-02 起的消费者条款对第三方工具用账号会话收紧——公开分发有 ToS 风险。
- 通知用 UNUserNotificationCenter；**ad-hoc 签名下可能被系统静默**，Developer ID 公证后最稳。

## 已知待办 / 限制

- 每块屏一个 22Hz 轮询定时器（功耗）——可合并成单定时器驱动所有挂件。
- 非 Claude 子代理模型的计价近似（见上）——可接 LiteLLM 在线价表解决。
- 同目录多个 `--resume` 会话的进程↔文件匹配靠 mtime 兜底，极端情况下仍可能配错。
- 分发为 ad-hoc 签名；要免 Gatekeeper 警告需 `make dist`（Developer ID + 公证）。
