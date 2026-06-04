# ClaudeNotch · Windows (WinUI 3) 设计规范

> 目标:把 Windows 版从 WPF 迁到 **WinUI 3 / Windows App SDK**,做到尽量贴近 **Windows 11 原生应用**的精致度。
> 本文是**设计规范(design spec)**——先把每个界面、每个组件、每个设计令牌(token)定清楚,再据此实现。
> 数据来源与口径不变(见根 `CLAUDE.md`);本文只规定**观感与交互**。

---

## 0. 设计原则

1. **原生优先(Fluent 2 / WinUI)**:能用系统材质、系统主题资源、系统控件就不要自造。颜色不写死,一律走 `ThemeResource`(随系统亮/暗 + 强调色变化)。
2. **材质分层**:窗口底用 **Mica**,浮层/挂件用 **Acrylic**,对话框遮罩用 **Smoke**。靠材质和层级(layer)而非边框/阴影来区分。
3. **克制的圆角与留白**:遵循 WinUI 圆角与 4px 间距网格;信息密度向 Windows 11「设置」应用看齐。
4. **跟随系统**:亮/暗主题、强调色、字号缩放、RTL 全部跟随系统;不提供 app 内主题切换(更原生)。
5. **可达性**:对比度达 WCAG AA;所有交互元素有键盘焦点态与 `AutomationProperties.Name`。

---

## 1. 设计令牌(Design Tokens)

### 1.1 材质 Materials
| 场景 | 材质 | API |
|---|---|---|
| 设置窗 / 数据统计窗 背景 | **Mica** | `Window.SystemBackdrop = new MicaBackdrop()` |
| 悬浮挂件(折叠球 / 展开面板) | **Acrylic** | `DesktopAcrylicBackdrop`(Base 档) |
| 托盘右键菜单 / 下拉浮层 | 系统 **Acrylic Flyout** | `MenuFlyout` 默认材质 |
| 对话框背后遮罩 | **Smoke** | `ContentDialog` 默认 |

### 1.2 颜色(全部用主题资源,禁止硬编码)
- 文本:`TextFillColorPrimary` / `Secondary` / `Tertiary` / `Disabled`
- 卡片/层:`CardBackgroundFillColorDefault`(卡片)、`LayerFillColorDefault`(分组容器)、`ControlFillColorDefault`(控件)
- 描边:`CardStrokeColorDefault`、`ControlStrokeColorDefault`、`DividerStrokeColorDefault`
- 强调:`AccentFillColorDefault` / `SystemAccentColor`(随系统强调色)
- **语义状态色(环 / 告警)** —— 用系统语义色,最原生:
  | 状态 | 资源 | 用途 |
  |---|---|---|
  | 充裕 / 正常 | `SystemFillColorSuccess` | 额度剩余多、上下文低 |
  | 提示 | `SystemFillColorCaution` | 接近告警阈值 |
  | 危急 | `SystemFillColorCritical` | 超阈值 |
  > 进度环的「绿→橙→红」连续渐变仍按 macOS 口径计算插值,但**端点色对齐**上述系统语义色,做到与 Win11 一致又跨端一致。

### 1.3 字体与字号(WinUI Type Ramp)
- 字族:**Segoe UI Variable**(`Display` 用于大数字/标题,`Text` 用于正文)——回退 `Segoe UI`。
- 字阶(沿用系统命名,便于直接套 `ThemeResource` 样式):
  | 角色 | 字号/行高 | 字重 | 用途 |
  |---|---|---|---|
  | Caption | 12 / 16 | Regular | 次要说明、图例、时间戳 |
  | Body | 14 / 20 | Regular | 正文、列表项副标题 |
  | Body Strong | 14 / 20 | SemiBold | 列表项标题、设置项标题 |
  | Subtitle | 20 / 28 | SemiBold | 卡片/分组标题、环中心大数字 |
  | Title | 28 / 36 | SemiBold | 页面标题(数据统计) |

### 1.4 圆角 Corner Radius
- 控件(按钮/输入/下拉):**4px**(`ControlCornerRadius`)
- 卡片 / 浮层 / 菜单:**8px**(`OverlayCornerRadius`)
- 挂件展开面板:**12px**;折叠态:**16px**(圆角矩形,见 §3)

### 1.5 间距 Spacing(4px 基准网格)
- 窗口内容边距:**24**
- 卡片内边距:**16**;卡片间距:**12**
- 分组内行间距:**8**;紧凑列表行:**6**
- 控件水平间距:**8**

### 1.6 图标
- 全部使用 **Segoe Fluent Icons** 字形(`FontIcon`),不引位图。常用:设置 ``、刷新 ``、统计 ``、关闭 ``、展开/收起 `/`、退出 ``。
- 托盘图标:沿用现有「环形」矢量,导出为 `.ico`(随主题色)。

---

## 2. 应用架构与窗口形态

- **无主窗口、托盘常驻**:`Application` 启动后不开主窗口;靠托盘图标 + 悬浮挂件存在。
- **三个窗口**:悬浮挂件(常驻置顶)、设置窗(按需)、数据统计窗(按需)。设置/统计为标准带标题栏窗口(Mica + 自定义标题栏)。
- **托盘菜单**:WinUI `MenuFlyout`(经 `H.NotifyIcon.WinUI` 的 `TaskbarIcon` 承载)——天然 Acrylic 圆角,比 WPF/WinForms 菜单现代。
- **通知**:Windows App SDK `AppNotificationManager` 原生 toast(进通知中心、可带按钮)。

---

## 3. 悬浮挂件(Floating Widget)

> ⚠️ 技术现实:WinUI 3 目前**难以做逐像素透明 / 真圆形窗**。为「精致且原生」,折叠态改为 **圆角方卡 + Acrylic**(与 Win11 桌面小组件一致的语言),而非自由漂浮的圆球。环本身仍是圆形,绘制在卡片内。

### 3.1 折叠态 —— 紧凑卡(默认 72×72,圆角 16)
- 背景:Acrylic;1px `CardStrokeColorDefault` 描边;置顶、不在任务栏、不可缩放。
- 内容:居中**进度环**(环=订阅剩余容量,色随用量 success→caution→critical)+ 环心大数字(剩余 %,Subtitle)+ 「剩余/left」(Caption)。
- 交互:左键拖拽移动(记忆位置);**单击**展开;右键弹 `MenuFlyout`。
- 无数据态:环心显示 `…`(等待)/`·`(加载),悬浮 `ToolTip` 提示「在终端跑一次 claude」。

### 3.2 展开态 —— 面板卡(宽 320,圆角 12)
- 顶部:`ClaudeNotch` 标题(Body Strong)+ 右侧收起按钮(`FontIcon `,透明圆角按钮)。**整条顶部为拖拽区**。
- **剩余环组**:横向 `UniformGrid`,每个指标一格:大环(剩余%)+ 指标名(Caption)+ 重置时间(Caption Tertiary)。
- 「最近会话官方花费 ¥/$X」一行(Caption,居中)。
- 分隔线(`DividerStrokeColorDefault`)。
- **活跃会话列表**:每行 = 小环(上下文%,色随阈值)+ 项目名·分支(Body Strong)+ 模型·花费(Caption)+ 右侧 token 数(Caption)。最多 6 行。空态:「无运行中的会话」。
- 分隔线。
- **操作行**:三个次要按钮(`Analytics / Settings / Refresh`),Fluent 标准按钮样式(非自造药丸)。

---

## 4. 设置窗(Settings)

> 目标观感 = **Windows 11「设置」应用 / PowerToys**。用 `SettingsCard` + `SettingsExpander`(CommunityToolkit.WinUI)实现卡片式设置项,这是最原生的写法。

- 窗口:Mica;**自定义标题栏**(`ExtendsContentIntoTitleBar`),左上角标题「设置 / Settings」+ 应用图标;尺寸 ~520×720,可缩放,居中。
- 布局:`ScrollViewer` 内纵向分组;每组一个**小标题**(Body Strong,Secondary 色)+ 若干 `SettingsCard`。
- 分组与控件:
  1. **外观与语言** — `SettingsCard`「语言」+ 右侧 `ComboBox`(系统/中文/English)。(主题跟随系统,不提供切换项。)
  2. **通用** — 三个 `SettingsCard` 带 `ToggleSwitch`:启用悬浮挂件 / 开机自启 / 接管 statusLine;`statusLine` 项用 `SettingsExpander` 展开放说明文字。
  3. **通知** — `ToggleSwitch`(额度/上下文通知)+ 三个带 `Slider` 的 `SettingsCard`(提示档% / 严重档% / 上下文告警%),右侧显示当前值。
  4. **模型价格** — `SettingsCard` 显示「已载入 N 个单价(含 M 条覆盖)」+ 更新时间;`SettingsExpander` 内放「刷新价格 / 编辑覆盖」按钮 + 说明;错误用 `InfoBar`(Warning)。
  5. **货币与汇率** — 显示当前货币与汇率 + 更新时间;「刷新汇率」按钮;错误用 `InfoBar`。
  6. **集成状态** — 接入状态(✓/✗,用 `InfoBar` Success/Error 表达)、额度数据时间;按钮「重新接入 / 打开支持目录 / 复制诊断」;底部 `SettingsCard` 说明数据来源(不抓网页、不复用令牌)。
- 控件规范:`ToggleSwitch` 取代复选框(更原生);`Slider` 带 `Header` 与数值;按钮成组右对齐;所有图标用 `FontIcon`。

---

## 5. 数据统计窗(Analytics)

- 窗口:Mica;自定义标题栏;尺寸 ~960×780,可缩放,居中。
- **顶栏**:页面标题「数据统计 / Analytics」(Title)+ 右侧操作区。
  - **指标切换做成 `SelectorBar`(分段控件,即用户说的「顶部 tab」)**:`计费 / 花费 / 总量`——比下拉框更原生、更像 Win11。
  - **时间范围** 用紧凑 `ComboBox`(3/6/12 月/全部)。
  - 命令按钮:`重新扫描`(``)、`导出 CSV`、`导出 JSON`——放进 `CommandBar` 或一排标准按钮。
- 扫描中:顶部 `ProgressBar`(`IsIndeterminate` 或带进度)+ Caption 文案。
- **内容(纵向卡片,卡片=8 圆角 + `CardBackgroundFillColorDefault`)**:
  1. **KPI 行**:4 个统计卡(今日 / 7 天 / 30 天 / 累计),每卡:标题(Caption)+ 金额(Subtitle)+ 「billable · N msgs」(Caption)。
  2. **每日用量热力图**:GitHub 风格贡献格(11px 方格,2.5 圆角),色阶用 Success 色透明度分级;月份/星期标签(Caption Tertiary);图例「少→多」;**点选某日**展开当日明细(模型/项目)。选中格描边用 `TextFillColorPrimary`。
  3. **趋势**:细柱状图(柱宽 4,顶部 1.5 圆角,Success 色),横向可滚动;悬浮 `ToolTip` 显示日期+值。
  4. **时段打卡 7×24**:点阵,点大小随活跃度;星期/小时标签。
  5. **按模型 / 按项目**(并排两卡):条形行(标签 + 进度条 + 数值);模型未收录标「估/est」。
  6. **缓存效率 / 连续&峰值**(并排两卡):`InfoRow`(键左值右,值 SemiBold)。
- 底部脚注(Caption Tertiary):花费口径说明。
- **绘图实现**:进度环用 WinUI `Path`(`ArcSegment`)即可;热力图/柱状/点阵用轻量 `Shapes`/`Border` 拼;若性能或精度不足再上 **Win2D `CanvasControl`**(待技术底座研究结论决定,见 §7)。

---

## 6. 托盘与通知

- **托盘图标**:`H.NotifyIcon.WinUI` 的 `TaskbarIcon`;左键单击 = 显示/隐藏挂件;右键 = `MenuFlyout`(设置 / 数据统计 / 显示挂件 / 立即刷新 / —— / 退出),每项带 `FontIcon`,Acrylic 圆角。
- **Tooltip**:鼠标悬浮显示「ClaudeNotch · 用量 N%」。
- **通知**:`AppNotificationManager` 原生 toast(额度/上下文告警);需注册 AUMID + 开始菜单快捷方式(unpackaged 要求,见技术底座)。toast 内容:标题 + 正文,点击聚焦挂件。

---

## 7. 待技术底座研究确认的开放项(并行研究中)

这些不影响设计,但影响实现取舍,后台研究代理正在核实(见随后结论):
1. **自包含 + unpackaged + 单文件** 在 CI 上的可行性与确切 csproj/命令。
2. **纯代码(无 .xaml)** WinUI 3 的可行度;是否需在代码里 merge `XamlControlsResources`。
3. **自定义 Main** 拦截 `--statusline` 快退的写法。
4. **挂件透明度** 的真实能力边界(决定折叠态是圆角卡还是别的)。
5. **Win2D vs Path/Shapes** 用于环与图表。
6. `H.NotifyIcon.WinUI` / `AppNotificationManager` 在 unpackaged 自包含下的确认。

---

## 8. 实现阶段计划(每步推 CI 验证编译后再继续)

> 因本地无 Windows 环境,**先让骨架在 CI 编过,再逐屏移植**,避免一次性盲写。

- **P0 设计规范**(本文)+ 技术底座研究结论。
- **P1 项目骨架**:新 WinUI 3 工程 + 自定义 Main(statusline 快退)+ 单实例 + 空托盘 + 一个 Mica 空窗;改 CI;**CI 绿**。
- **P2 设计系统层**:`Theme`/`Tokens`(主题资源映射)+ 进度环控件 + 卡片/分组工厂。
- **P3 悬浮挂件**(折叠卡 ↔ 展开面板 + 拖拽 + 右键菜单)。
- **P4 设置窗**(`SettingsCard`/`SettingsExpander` 全分组)。
- **P5 数据统计窗**(`SelectorBar` + KPI + 热力图 + 趋势 + 打卡 + 模型/项目/缓存/连续 + 导出)。
- **P6 通知**(原生 toast)+ 收尾打磨(标题栏、图标、键盘焦点、本地化校对)。
- **Core/** 业务逻辑层原样复用(与 UI 框架解耦,零改动)。

---

## 9. 与现有 WPF 版的有意取舍

- 折叠挂件由「自由圆球」改为「圆角 Acrylic 方卡」(透明度限制 + 更贴 Win11 小组件语言)。
- 复选框 → `ToggleSwitch`;设置项 → `SettingsCard`;统计页指标下拉 → `SelectorBar` 分段控件。
- 托盘气泡 → 原生 `AppNotification` toast。
- 迁移期 WPF 版保留,WinUI 3 版在新工程独立编译验证,绿后再切换默认。
