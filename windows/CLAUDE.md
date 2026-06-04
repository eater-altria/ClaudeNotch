# CLAUDE.md — Windows 版

ClaudeNotch 的 **Windows 原生版**：C# / .NET 8。
与 macOS 版共享同一套数据来源与口径（见根 [`CLAUDE.md`](../CLAUDE.md)），UI 为**置顶悬浮挂件**（无刘海）。

## ✅ 当前方向：Avalonia 重构（`ClaudeNotch.Avalonia/`，新默认）

WinUI 3 版踩坑太多（unpackaged 启动闪退/PRI、Mica 主题白屏、**逐像素透明做不出圆球**、DPI 尺寸、拖拽错乱），
故 **Windows 版改用 Avalonia UI 重构**（mac 版不动，仍是 Swift）。Avalonia 一举解决上述痛点：

- **工程**：`ClaudeNotch.Avalonia/`，Avalonia **11.3.17**，`net8.0-windows`，链接复用 `ClaudeNotch/Core/**`（零改动）。CI：`.github/workflows/windows-avalonia.yml`（self-contained win-x64 文件夹 zip，`av-v*` tag 发 Release）。
- **本地可调试**：Avalonia 包很小，`dotnet build/run` 秒级，可本地跑+截图迭代（不像 WinUI 要下 200MB WindowsAppSDK 运行时）。
- **悬浮球**：`Window` 透明（`SystemDecorations.None` + `Background=Transparent` + `TransparencyLevelHint=Transparent`）+ `SizeToContent.WidthAndHeight` → 折叠态画个 `Ellipse` 就是**真·圆球**（无方块、无边框）；展开面板自动贴合内容（**底部按钮永不裁切**）。
- **拖拽**：手动 —— `PointerPressed` 记录 `PointToScreen` 起点 + `Window.Position`，`PointerMoved` 按增量 `Position = 起点+delta` 实时跟随光标；松手即停;小位移判定为点击(展开/折叠)。
- **主题**：`FluentTheme` + `RequestedThemeVariant = Dark`（强制深色，所有窗口/内置控件一致，无白屏）。
- **自绘**：进度环 = `Control.Render` + `StreamGeometry.ArcTo`；热力图/趋势/打卡 = `Border`/`Ellipse` 拼。趋势用**连续日期轴**(数据起始日~今天逐日)。
- **托盘**：Avalonia `TrayIcon` + `NativeMenu`（图标 `avares://ClaudeNotch/Assets/tray.ico`）。**注意:Avalonia TrayIcon 无原生气泡通知**，`Notifier.Show` 暂为 no-op，待后续用 Win32 `Shell_NotifyIcon` 或 toast 补。
- **statusline**：仍由 NativeAOT 助手 `ClaudeNotch.Statusline.exe`（同目录）承担；主 exe 带 `--statusline` 快退分支兜底。
- **⚠️ 命名坑**：设计令牌静态类**不能叫 `Theme`** —— 会与 Avalonia `StyledElement.Theme`(`ControlTheme`)属性在控件子类里冲突，编译报一堆 CS1061/CS0120。本项目用 `Palette`。

> 旧 `ClaudeNotch.WinUI/`（WinUI 3）与 `ClaudeNotch/`（WPF）暂留作参考，后续可删。下方 WinUI/WPF 说明仅供历史参考。

## 🗄️ 历史：WinUI 3 尝试（`ClaudeNotch.WinUI/`，已弃）

为更贴近 Windows 11 原生观感，正把 UI 迁到 **WinUI 3 / Windows App SDK**（设计规范见 [`WINUI3-DESIGN.md`](WINUI3-DESIGN.md)）。
- **两套并存**：旧 `ClaudeNotch/`（WPF）保留，新 `ClaudeNotch.WinUI/` 独立编译验证（CI：`.github/workflows/windows-winui3.yml`，unpackaged + 自包含 win-x64）。绿了再切默认。
- **Core 零改动复用**：`ClaudeNotch.WinUI` 链接编译 `ClaudeNotch/Core/**`（与 UI 框架解耦）。
- **关键取舍**：unpackaged WinUI 3 无法逐像素透明 → 折叠挂件改为「圆角(DWM)+ Acrylic」卡片；设置/统计窗用 Mica + 自定义标题栏；托盘用 `H.NotifyIcon.WinUI`（`MenuFlyout`）；进度环 `Path/ArcSegment`、图表 `Border` 拼；通知暂用托盘气泡（原生 toast 需安装器+AUMID 快捷方式，留待有安装器后）。
- **statusline**：仍由 NativeAOT 助手 `ClaudeNotch.Statusline.exe`（与主 exe 同目录）承担；WinUI 主 exe 也带 `--statusline` 快退分支兜底。
- **几乎纯代码 UI，但 `App.xaml` 必须保留**：UI 全部代码构建，**唯一的 .xaml 是 `App.xaml`（ApplicationDefinition）**——它是 unpackaged+自包含「能正常启动」的硬性前提：触发 markup 编译器生成 app 的 `resources.pri` 并合并框架 themeresources，否则 `ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml` 无法解析、`InitializeComponent()` 启动即 `COMException` 闪退。配套：`<ProjectPriFileName>resources.pri</ProjectPriFileName>`（默认会命名成 `<AssemblyName>.pri`，unpackaged 加载器只认 `resources.pri`，见 microsoft-ui-xaml#10856）。`App` 须 `partial` + 构造调 `InitializeComponent()`；`XamlControlsResources` 在 `App.xaml` 里声明（不再代码 merge）。**这类问题 CI 编译查不出，只在真机运行暴露。**

> 迁移完成前，下方 WPF 版的模块地图与说明仍然有效（描述的是 `ClaudeNotch/`）。

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
