# gxTempMonitor Dashboard

**不止于监控，更是桌面的艺术品。**  
**A pixel-perfect, hyper-optimized system monitoring suite designed exclusively for Windows.**

---

## 中文

`gxTempMonitor` 不是那种“能跑就行”的监控小工具。  
它的目标很明确：

- 要比传统硬件监控软件更轻
- 要比默认系统面板更美
- 要比一堆臃肿壳子软件更快、更稳、更克制

如果你也认同这种产品观，那欢迎一起把它打磨成真正拿得出手的 Windows 监控作品。

## 核心亮点

### 🎯 双擎驱动 (Dual-Mode Engine)

一边是极致轻量、常驻右上角的隐形悬浮 HUD。  
一边是双击即开的深色数据主控台 `Dashboard`。  

它们不是两个割裂的窗口，而是同一套监控心脏驱动下的双形态界面：

- 悬浮窗负责“余光一瞥”
- 主控台负责“完整洞察”

### 🚀 深空霓虹 (Deep Space Neon UI)

我们不接受生硬、廉价、像后台工具一样的默认控件。  
`gxTempMonitor` 追求的是一种更锋利的桌面视觉：

- 深空渐变背景
- 霓虹感环形仪表盘
- 微渐变磨砂卡片
- 趋势折线
- 高对比数字排版

这不是监控面板的附属品，这是桌面视觉系统的一部分。

### ⚡ 性能至上 (Zero-Bloat)

拒绝 Electron。  
拒绝多层壳子。  
拒绝为了一个监控面板吃掉整块内存。

项目基于：

- `.NET 10`
- `WPF`
- `LibreHardwareMonitor`
- Windows 原生 `PerformanceCounter`
- Win32 `P/Invoke`

目标只有一个：  
在游戏、渲染、压测、日常桌面这些高压场景里，依然保持低开销、低打扰、稳定输出。

### 🛠️ 底层嗅探 (Hardware Radar)

这不是简单读几个 UI 数字。  
当前版本已经具备：

- CPU 占用
- GPU 温度 / 负载 / 功耗 / 风扇
- RAM 占用 / 可用容量 / 峰值
- VRAM 占用
- 当前活跃网卡上下行
- 历史 `MAX` 记录
- 首页趋势折线

同时对 CPU 温度采用：

- 优先读取硬件传感器
- 读不到时尝试 WMI 兜底

我们接受现实：不同硬件平台暴露的传感器能力并不一致。  
所以项目追求的不是“伪装成全能”，而是“能读的尽量读准，读不到就诚实显示”。

## 当前架构

项目现在已经从单悬浮挂件，升级成了完整双窗结构：

- `MainWindow`
  轻量悬浮 HUD
- `DashboardWindow`
  深色主控台
- `HardwareMonitorService`
  共享采集核心，统一推送数据快照
- `CircularProgressBar`
  自定义环形仪表盘
- `SparklineChart`
  轻量趋势折线控件

这意味着两个窗口不会重复初始化硬件监控，不会出现“双开采样”的混乱状态。

## 适合贡献的方向

如果你准备参与这个项目，最值得投入的方向是：

- 传感器兼容性增强
- Dashboard 图表与详情页继续深化
- 主题系统 / 样式预设
- 多显示器与高 DPI 场景继续打磨
- 发布流程与构建脚本优化
- 诊断日志与错误可观测性增强

## 开发准则

这里的代码风格不是“能堆就堆”，而是：

- 功能必须真实有效
- UI 必须有层级、有克制
- 监控逻辑必须共享，避免重复采样
- 不为了花哨牺牲可读性
- 不为了炫技引入沉重依赖

一句话：

**做真正好用、真正好看、真正轻量的 Windows 监控套件。**

## 本地开发

### 构建

```powershell
dotnet build TempMonitor/TempMonitor.csproj -c Release
```

### 轻量版发布

```powershell
dotnet publish TempMonitor/TempMonitor.csproj -c Release -r win-x64
```

或者直接使用：

```powershell
.\build-light.bat
```

### 通用版发布

```powershell
.\build-standalone.bat
```

## 提交前建议

在发起提交前，至少确认这些事情：

- 项目可以正常构建
- 悬浮挂件和 Dashboard 都能打开
- 右键菜单、托盘菜单状态一致
- 单实例逻辑正常
- 首页和详情页没有明显布局截断
- README / 文档没有落后于功能现状

## Pull Request 风格

我们欢迎这样的改动：

- 解决真实问题
- 提升真实体验
- 有清晰边界
- 不引入无意义复杂度

我们不鼓励这样的改动：

- 只改表面、不解决根因
- 引入庞大依赖只为一处小效果
- 没验证就改构建或发布策略
- 让项目从“轻量监控”滑向“臃肿平台”

---

## English

`gxTempMonitor` is not “just another monitor overlay.”  
It is built around a sharper standard:

- lighter than traditional monitoring suites
- cleaner than default system panels
- faster and leaner than bloated shell-based desktop tools

If that sounds like your kind of project, welcome aboard.

## Highlights

### 🎯 Dual-Mode Engine

An ultra-light floating HUD for glance-first monitoring.  
A cyber-styled dark dashboard for full inspection.  

Same monitoring core.  
Two very different viewing modes.

### 🚀 Deep Space Neon UI

No lazy default controls.  
No cheap-looking utility panel aesthetics.

This project pushes toward:

- deep-space gradients
- neon circular gauges
- frosted micro-gradient cards
- sparkline trends
- high-contrast numerical typography

### ⚡ Zero-Bloat

No Electron.  
No unnecessary runtime bulk.  
No pretending heavy is acceptable.

Built with:

- `.NET 10`
- `WPF`
- `LibreHardwareMonitor`
- native Windows counters
- Win32 interop where it matters

### 🛠️ Hardware Radar

The app currently tracks:

- CPU usage
- GPU temperature / load / power / fan
- RAM usage / available memory / peak
- VRAM usage
- active network throughput
- historical max values
- dashboard trend lines

When CPU thermal data is unavailable from hardware sensors, the app attempts a WMI fallback instead of faking confidence.

## Architecture

- `MainWindow`
  Floating HUD
- `DashboardWindow`
  Full desktop control panel
- `HardwareMonitorService`
  Shared data engine and snapshot broadcaster
- `CircularProgressBar`
  Custom circular gauge
- `SparklineChart`
  Lightweight trend line control

## Contributing Direction

The highest-value areas to push forward are:

- better hardware compatibility
- richer dashboard data views
- theme presets and styling depth
- multi-monitor / high-DPI polish
- packaging and release automation
- better observability and diagnostics

## Local Workflow

```powershell
dotnet build TempMonitor/TempMonitor.csproj -c Release
dotnet publish TempMonitor/TempMonitor.csproj -c Release -r win-x64
```

Helper scripts:

```powershell
.\build-light.bat
.\build-standalone.bat
```

## Contribution Standard

We value changes that are:

- grounded in real UX or technical gains
- visually intentional
- lightweight in dependency footprint
- verified before being merged

We avoid changes that:

- add bulk without clear payoff
- chase style without solving real friction
- duplicate monitoring logic
- weaken the “fast, lean, native Windows tool” identity

---

**Build bold. Keep it lean. Make Windows monitoring feel premium.**
