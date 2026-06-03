# CLAUDE.md — Windows 版

ClaudeNotch 的 **Windows 原生版**：C# / .NET 8 / WPF（+ WinForms 仅用于托盘 NotifyIcon）。
与 macOS 版共享同一套数据来源与口径（见根 [`CLAUDE.md`](../CLAUDE.md)），UI 改为**置顶悬浮挂件**（无刘海）。

## ⚠️ 本地无 Windows 环境 —— 编译只能走 CI

- 任何改动**推送后由 GitHub Actions（`.github/workflows/windows.yml`，`windows-latest`）编译**，本地不要也无法 `dotnet build`。
- CI 会 `dotnet build` + `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`，产物 zip 作为 artifact 上传；推 `win-v*` tag 时发 Release。
- 验证编译 = 看 Actions 是否绿。改完务必 push 并盯 CI。

## 入口与运行形态

- `Program.Main`（`[STAThread]`，`StartupObject`，无 App.xaml）：
  - 带 `--statusline` 时只跑 `StatuslineHook.RunHelper()`（读 stdin→落盘 ratelimits.json→透传原命令）即退出，**不启动 WPF**（Claude Code 每次渲染状态栏都会调它，必须快进快出）。
  - 否则单实例锁 → `new App().Run()`，`ShutdownMode=OnExplicitShutdown`（托盘常驻、无主窗口）。
- UI 全部**纯代码构建**（不用 XAML），刻意规避本地无法编译时 XAML codegen 的 partial 配对坑。

## 模块地图（windows/ClaudeNotch/）

| 文件 | 职责 |
|---|---|
| `Program.cs` / `App.cs` | 入口 + 编排（装配 stores/托盘/挂件，串联设置/统计窗口与生命周期；退出时 `StatuslineHook.Uninstall(false)`） |
| `Core/Paths.cs` | `%USERPROFILE%\.claude`（settings.json/projects）、`%APPDATA%\ClaudeNotch`（支持目录） |
| `Core/StatuslineHook.cs` | `--statusline` 助手 + 安装/卸载（改写 settings.json，备份 + 透传原命令；Windows 透传走 `cmd /c`） |
| `Core/UsageProvider.cs` / `UsageModels.cs` | 读 ratelimits.json → 额度快照；指标/投影/颜色 |
| `Core/ModelPricing.cs` | 定价 + 归一化匹配 + LiteLLM 价表(内置快照+周刷新) + 手动覆盖（与 mac 同口径） |
| `Core/HistoryModels.cs` / `HistoryScanner.cs` | token 桶/单行解析/按天聚合 + 增量历史扫描 |
| `Core/SessionScanner.cs` | 活跃会话扫描 |
| `Core/Stores.cs` | UsageStore/SessionStore/HistoryStore + Notifier 出口 |
| `Core/Localization.cs` / `Currency.cs` | `L.Tr(中,英)` 跟随系统；`Money` $/¥ + 汇率(open.er-api.com 周刷新) |
| `Core/Settings.cs` / `StartupRegistry.cs` | 设置持久化 + 开机自启(HKCU Run) |
| `UI/WidgetWindow.cs` | 置顶可拖拽**悬浮球**：折叠态圆球(环=**订阅剩余容量**、中心大数字=剩余%) ↔ 展开现代面板(剩余环组 + 会话列表 + 操作)；右键菜单同托盘 |
| `UI/Win11.cs` | DwmSetWindowAttribute：窗口圆角 + 沉浸式深色标题栏（旧版自动忽略） |
| `UI/RingControl.cs` / `Theme.cs` | 渐变进度环 + Fluent 调色板(Segoe UI Variable / 强调色 / 卡片) |
| `UI/Tray.cs` | 托盘 NotifyIcon + 菜单（设置/数据统计/刷新/退出）+ 气泡通知 |
| `UI/SettingsWindow.cs` / `AnalyticsWindow.cs` | 设置 / 数据统计（已对齐 Mac：KPI + 热力图[月/周标签+图例+选中日明细] + 趋势柱状 + 时段打卡7×24 + 按模型/项目/缓存效率/连续&峰值 + 导出 CSV/JSON） |

## ⚠️ 与 macOS 版的有意差异

- **活跃会话判定**：Windows 拿不到进程 cwd（要读 PEB，脆弱），故不做「进程↔transcript 匹配」，改以
  **transcript 近期写入（mtime 在 `SessionScanner.ActiveWindow`，默认 8 分钟内）= 活跃**。代价：空闲会话会多留一会儿、
  响应间隔很久的会话可能短暂消失。无终端跳转（无 tty）。
- **悬浮挂件**替代刘海：置顶、可拖拽、点击展开/折叠；位置存 `settings.json`。
- 通知用托盘 **NotifyIcon 气泡**（非 UNUserNotificationCenter）。

## 共享口径（务必和 mac 保持一致，改一个要想另一个）

- 花费：按 `usage` token × 模型单价；**按 message.id 去重**；递归计入 `subagents/**`。
- 上下文窗口：opus 默认 1M、其余 200k，峰值 >200k 自动升 1M。
- LiteLLM 归一化匹配 + 覆盖优先级：覆盖 > LiteLLM 表 > 本地按族兜底。
