# gxTempMonitor

轻量 Windows 桌面监控挂件 — 游戏兼容，零内核驱动。  
A lightweight Windows desktop monitor widget — game-safe, zero kernel drivers.

![TempMonitor Preview](TempMonitor/assets/gxtmp.png)

[![GitHub License](https://img.shields.io/github/license/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=blue)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=orange)](https://github.com/gxmst/gxTempMonitor/releases)
[![Dotnet Version](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge&logo=windows)](https://www.microsoft.com/windows)

---

## 为什么做这个

市面上的硬件监控工具（如 HWMonitor、LibreHardwareMonitor）依赖内核驱动读取传感器，运行游戏时容易被反作弊系统（Vanguard、EAC、BattlEye 等）标记或踢出。

gxTempMonitor 的设计原则：**只用用户态 API，不加载任何内核驱动，不扫描任何进程**。在游戏全屏时也能安全常驻。

## 功能一览

### 悬浮挂件

- 右上角常驻，显示 `CPU / GPU / RAM / VRAM / UP / DN`
- 鼠标悬停展开 `MAX` 历史峰值列
- 闲置时自动降低背景存在感，唤醒时恢复
- 可锁定鼠标穿透，游戏时不遮挡操作
- 右键菜单与托盘菜单功能同步

### 告警闪烁

- GPU 温度 ≥ 85°C 或 RAM 使用率 ≥ 90% 时挂件背景闪烁提醒
- 深色/浅色主题各有匹配的闪烁配色
- 恢复正常后自动停止

### 全局热键

- `Ctrl+Shift+M` 一键切换挂件显示/隐藏
- 游戏全屏时快速呼出或隐藏

### 主题与透明度

- 深色主题（默认）/ 浅色主题，右键切换
- 4 档透明度：50% / 65% / 80% / 95%
- 主题色覆盖标签、数值、最大值、指示条、网络、边框

### 托盘悬浮提示

- 鼠标悬停托盘图标直接显示 CPU / GPU / RAM 数值
- 显示最占显存的进程名及大小
- 无需打开挂件即可快速查看

### 数据导出

- 右键「导出 CSV」将历史数据保存为 CSV 文件
- 包含时间戳、CPU、GPU 温度/使用率、RAM、VRAM、网络、最占显存进程
- 保留最近 3600 条快照（约 1 小时）

### 延迟启动

- 支持 0 / 10 / 20 / 30 秒延迟启动
- 避免开机时与游戏平台抢资源
- 开机自启快捷方式自动附加 `--delay` 参数

### 进程级 GPU 显存

- 通过 `GPU Process Memory` 性能计数器追踪各进程显存占用
- 显示最占显存的进程名及占用大小
- 每 5 秒刷新一次

### Dashboard 主控台

- 双击挂件打开深色主控台
- 环形仪表盘：CPU / GPU / RAM
- 趋势折线：CPU / GPU / RAM / VRAM / UP / DN
- 详情页：CPU / GPU / RAM / 网络
- 页面切换淡入淡出动画
- 右下角拖拽缩放
- 高分屏适配

## 数据来源

GPU 监控按优先级自动选择，无需手动配置：

| 优先级 | 方案 | GPU 温度 | GPU 使用率 | 显存 | 功耗 | 适用显卡 |
|--------|------|---------|-----------|------|------|---------|
| 1 | NVIDIA NVML (`nvml.dll`) | ✅ | ✅ | ✅ | ✅ | NVIDIA |
| 2 | AMD ADL (`atiadlxx.dll`) | ✅ | ✅ | ✅* | ✅ | AMD |
| 3 | Windows GPU PerformanceCounter | ❌ | ✅ | ✅ | ❌ | 全部（含 Intel） |

\* AMD ADL 返回显存总量，显存使用量由 Windows PerformanceCounter 补充。

其他指标：

| 指标 | 数据源 | 内核驱动 |
|------|--------|---------|
| CPU 使用率 | Windows PerformanceCounter | ❌ |
| 内存使用 | `GlobalMemoryStatusEx` | ❌ |
| 网络流量 | Windows PerformanceCounter | ❌ |
| 进程显存 | `GPU Process Memory` PerformanceCounter | ❌ |

所有数据通过用户态 API 获取，不加载 WinRing0 等内核驱动，不触发反作弊检测。

> NVIDIA GPU 需要系统已安装 NVIDIA 驱动。AMD GPU 需要系统已安装 AMD 驱动（自带 `atiadlxx.dll`）。

## 交互

| 操作 | 效果 |
|------|------|
| 左键拖动挂件 | 移动位置 |
| 双击挂件 | 打开 Dashboard |
| 鼠标悬停挂件 | 展开 MAX 列 |
| 右键挂件或托盘 | 控制菜单 |
| `Ctrl+Shift+M` | 切换挂件显示/隐藏 |
| 关闭 Dashboard | 仅隐藏，不退出 |
| 菜单「退出」 | 退出程序 |

## 配置

程序保存以下状态到 `config.json`（与 exe 同目录）：

- 窗口位置
- 锁定状态
- RAM / VRAM / UP / DN 显示开关
- 主题（深色 / 浅色）
- 透明度
- 延迟启动秒数

日志写入 `TempMonitor.log`，自动限长不无限增长。

## 构建

```powershell
# Debug
dotnet build TempMonitor/TempMonitor.csproj -c Debug

# Release（单文件 + ReadyToRun + 压缩）
dotnet publish TempMonitor/TempMonitor.csproj -c Release -r win-x64
```

Release 发布输出为单文件自包含程序，无需安装 .NET Runtime。

## 项目结构

```
TempMonitor/
├── App.xaml.cs                  应用入口，单实例互斥，延迟启动
├── MainWindow.xaml(.cs)         悬浮挂件（主题/透明度/热键/告警/CSV导出）
├── DashboardWindow.xaml(.cs)    主控台
├── HardwareMonitorService.cs    硬件数据采集服务（单例，历史缓冲，CSV导出）
├── IGpuMonitor.cs               GPU 监控接口 + GpuReading 结构体
├── NvidiaGpuMonitor.cs          NVIDIA NVML 用户态 API 封装
├── AmdGpuMonitor.cs             AMD ADL 用户态 API 封装
├── WindowsGpuCounterMonitor.cs  Windows GPU PerformanceCounter 兜底
├── UiHelper.cs                  UI 工具类（格式化、告警色）
├── CircularProgressBar.xaml(.cs) 环形仪表盘控件
├── SparklineChart.xaml(.cs)     趋势折线控件
└── app.manifest                 以当前用户权限运行（asInvoker）
```

## 技术栈

- .NET 10 / WPF / Windows Forms（NotifyIcon）
- NVIDIA NVML（用户态 GPU 监控）
- AMD ADL（用户态 GPU 监控）
- Windows GPU PerformanceCounter（通用 GPU 兜底）
- Windows PerformanceCounter（CPU / 网络 / 进程显存）
- `GlobalMemoryStatusEx`（内存）
- Win32 `RegisterHotKey`（全局热键）
- 无第三方内核驱动依赖

## 安全说明

- **不加载内核驱动**：所有硬件数据通过用户态 API 获取
- **不扫描进程列表**：不检测游戏进程，不隐藏功能
- **不注入任何程序**：纯读取性能计数器和厂商 API
- **以普通用户权限运行**：asInvoker，不触发 UAC
- **单实例互斥**：防止重复启动

---

Made for a compact, game-safe Windows monitoring workflow.
