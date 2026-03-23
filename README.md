# gxTempMonitor

一款面向 Windows 的桌面监控挂件与主控台组合工具。  
A Windows desktop monitor widget with a full dashboard companion.

![TempMonitor Preview](TempMonitor/assets/gxtmp.png)

[![GitHub License](https://img.shields.io/github/license/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=blue)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=orange)](https://github.com/gxmst/gxTempMonitor/releases)
[![Dotnet Version](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge&logo=windows)](https://www.microsoft.com/windows)

---

## 中文简介

`gxTempMonitor` 现在由两部分组成：

- 右上角常驻的轻量悬浮挂件
- 双击挂件打开的深色主控台 `Dashboard`

悬浮挂件适合游戏、全屏或日常桌面下快速瞥一眼；主控台适合看更完整的状态、趋势和详情页。

## 当前功能

- 悬浮挂件显示 `CPU / GPU / RAM / VRAM / UP / DN`
- 悬停展开 `MAX` 历史峰值列
- 固定贴靠右侧，适合锁定后常驻使用
- 闲置时自动降低背景存在感，唤醒时恢复
- 右键菜单与托盘菜单功能同步
- 支持显示项开关：`RAM / VRAM / UP / DN`
- 支持恢复默认状态、重置最大值、开机自启、锁定穿透
- 单实例运行，重复启动会提示“已经在运行”
- 双击悬浮挂件打开 `Dashboard`

## Dashboard 特性

- 深色无边框主窗口
- 首页包含 `CPU / GPU / RAM` 环形仪表盘
- 首页包含最近趋势折线：`CPU / GPU / RAM / VRAM / UP / DN`
- `CPU / GPU / RAM / 网络` 四个详情页
- 页面切换淡入淡出动画
- 支持右下角拖拽缩放
- 高分屏和 150% 缩放场景下的布局已做专项适配

## 数据来源

- CPU 占用：Windows `PerformanceCounter`
- RAM：`GlobalMemoryStatusEx`
- GPU / VRAM / 部分温度、功耗、风扇、频率：`LibreHardwareMonitor`
- 网络：自动选择当前总流量最高的活跃网卡
- CPU 温度：优先读取硬件传感器，读不到时尝试 WMI 兜底

说明：

- CPU 温度、CPU 功耗、GPU 功耗、风扇转速等项目是否可读，取决于硬件、驱动和主板暴露的传感器能力。
- 某些机器上显示 `--` 属于正常现象，不一定是程序异常。

## 交互说明

- 左键拖动悬浮挂件：移动位置
- 双击悬浮挂件：打开主控台
- 鼠标悬停挂件：向左展开 `MAX` 列
- 右键挂件或托盘：打开控制菜单
- 关闭 `Dashboard`：只隐藏，不退出程序
- 退出程序：请使用右键菜单中的“退出”

## 配置与持久化

程序会保存以下状态到 `config.json`：

- 窗口位置
- 锁定状态
- 开机自启状态
- `RAM / VRAM / UP / DN` 显示开关

日志会写入：

- `TempMonitor.log`

日志带自动限长，不会无限增长。

## 构建与发布

项目基于：

- `.NET 10`
- `WPF`
- `LibreHardwareMonitorLib`

常用命令：

```powershell
dotnet build TempMonitor/TempMonitor.csproj -c Release
dotnet publish TempMonitor/TempMonitor.csproj -c Release -r win-x64
```

仓库根目录还提供了两个打包脚本：

- `build-light.bat`
  轻量版，依赖目标机器已安装对应的 .NET Desktop Runtime
- `build-standalone.bat`
  通用版，自带运行时，体积更大但开箱即用

默认轻量版发布目录：

- `TempMonitor/bin/Release/net10.0-windows/win-x64/publish`

## 项目结构

核心文件大致如下：

- `TempMonitor/MainWindow.xaml`
  悬浮挂件 UI
- `TempMonitor/MainWindow.xaml.cs`
  悬浮挂件交互与展示逻辑
- `TempMonitor/DashboardWindow.xaml`
  主控台 UI
- `TempMonitor/DashboardWindow.xaml.cs`
  主控台动画、趋势、详情页展示
- `TempMonitor/HardwareMonitorService.cs`
  统一采集服务，单例共享给两个窗口
- `TempMonitor/CircularProgressBar.xaml`
  环形仪表盘控件
- `TempMonitor/SparklineChart.xaml`
  首页趋势折线控件

## 已知限制

- 部分传感器在某些平台上可能不可用
- `LibreHardwareMonitor` 在不同显卡/主板/笔记本上的可读项差异较大
- 如果目标机器没有安装对应运行时，轻量版将无法直接启动

---

## English

`gxTempMonitor` is now a two-part Windows monitoring tool:

- a lightweight always-on-top floating widget
- a double-click dashboard window for richer details

### Highlights

- Floating widget for quick glance monitoring
- Dashboard with circular gauges and trend sparklines
- Detail pages for CPU, GPU, RAM, and Network
- Shared single-instance monitoring service
- Right-click and tray menus stay in sync
- Single-instance app behavior
- High-DPI friendly layout and resizable dashboard

### Data Sources

- CPU usage: Windows `PerformanceCounter`
- RAM: `GlobalMemoryStatusEx`
- GPU / VRAM / some power, fan, and thermal metrics: `LibreHardwareMonitor`
- Network: automatically tracks the most active interface

### Build

```powershell
dotnet build TempMonitor/TempMonitor.csproj -c Release
dotnet publish TempMonitor/TempMonitor.csproj -c Release -r win-x64
```

Release helpers:

- `build-light.bat`
- `build-standalone.bat`

---

Made for a compact “glance-first” Windows monitoring workflow.
