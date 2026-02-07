# gxTempMonitor

一款专为 Windows 打造的、像素级精致的系统监控挂件。
A pixel-perfect system monitoring widget designed for Windows.

[![GitHub License](https://img.shields.io/github/license/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=blue)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/gxmst/gxTempMonitor?style=for-the-badge&logo=github&color=orange)](https://github.com/gxmst/gxTempMonitor/releases)
[![Dotnet Version](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge&logo=windows)](https://www.microsoft.com/windows)

---

## 简体中文版

### 🎯 核心亮点

*   **🎨 原生交互体验**：深度适配 Windows 视觉规范，采用 60% 透明度深空灰磨砂玻璃效果与 1px 微光白边，视觉精致通透。
*   **磁力边缘吸附**：支持窗口边缘自动贴合（Snapping），拖动到屏幕左右边缘 50px 内即可自动归位。
*   **智能生长动画**：具备“避让边缘”的智慧逻辑。当窗口吸附在右边缘时，悬停会自动向左“生长”，确保 MAX 列始终在屏幕内。
*   **⚡ 极致性能架构**：基于 **Win32 API (P/Invoke)** 和内核级 `PerformanceCounter`，数据采集 0 延迟，资源占用几乎可以忽略。
*   **🔍 真实物理数据**：
    *   **RAM**：直接通过 OS 物理内存结构计算，彻底排除虚拟内存/提交内存的虚假读数。
    *   **VRAM**：专用显存 (Dedicated Memory) 过滤算法，精确反馈显卡物理占用。
*   **🌈 动态视觉反馈**：
    *   **红绿灯配色**：根据负载（80%/90% 阈值）自动切换白色、橙色、红色，警报一目了然。
    *   **等宽字体锁定**：数值区强制锁定 **Consolas** 字体，彻底解决数字跳动导致的界面抖动。
*   **🚀 绿色免安**：单文件 EXE 发布，不依赖外部 DLL，支持一键设置开机自启。

### 🔍 交互指南

*   **吸附 (Snap)**：拖动到屏幕边缘自动贴合。
*   **展开 (Expand)**：鼠标悬停查看各指标的 **MAX (历史峰值)** 数据。
*   **控制 (Control)**：
    *   **点击 CPU 行**：快速呼出 Windows 任务管理器。
    *   **右键菜单**：切换开机自启、重置最大值、安全退出。
    *   **双击窗口**：闪速退出。

### 🛠️ 技术实现 (Under the Hood)

*   **UI Engine**: .NET 10.0 WPF (Windows Presentation Foundation)
*   **Hardware Logic**: `LibreHardwareMonitor` (基于 Visitor 模式深度刷新)
*   **Memory Discovery**: **Win32 P/Invoke (`GlobalMemoryStatusEx`)**
*   **Data Aggregation**: Windows `PerformanceCounter` API
*   **Persistence**: 基于 JSON 的配置记忆与跨显示器坐标校验算法

---

## English Version

### 🎯 Key Features

*   **🎨 Native Windows Experience**: Deeply integrated with Windows aesthetics, featuring a 60% transparent space-gray frosted glass effect and 1px micro-glow borders.
*   **Magnetic Edge Snapping**: Supports automatic window snapping. Drag within 50px of the screen edges to snap instantly.
*   **Smart Growth Animation**: Intelligent "Edge-Aware" logic. When docked to the right edge, the panel expands to the left to ensure the MAX column remains visible.
*   **⚡ High-Performance Architecture**: Powered by **Win32 API (P/Invoke)** and kernel-level `PerformanceCounter` for zero-latency data collection and minimal resource usage.
*   **🔍 Accurate Physical Data**:
    *   **RAM**: Computed directly via OS physical memory structures, eliminating false readings from Virtual Memory/Commit size.
    *   **VRAM**: Dedicated Memory filtering algorithm provides exact feedback on actual GPU usage.
*   **🌈 Dynamic Visual Feedback**:
    *   **Status Colors**: Automatically toggles between White, Orange (80%), and Red (90%) based on load levels.
    *   **Monospaced Font**: Numerical areas are locked to **Consolas**, preventing UI jitter caused by varying character widths.
*   **🚀 Portable & Ready**: Single-file EXE release, no dependencies required, supports one-click auto-start at boot.

### 🔍 Usage Guide

*   **Snap**: Drag to any screen edge for automatic docking.
*   **Expand**: Hover over the widget to reveal **MAX (Historical Peak)** data.
*   **Control**:
    *   **Click CPU row**: Instant launch of Windows Task Manager.
    *   **Right-click**: Toggle "Auto-start", reset MAX values, or Exit.
    *   **Double-click**: Quick exit.

### 🛠️ Technical Stack

*   **UI Engine**: .NET 10.0 WPF
*   **Hardware Logic**: `LibreHardwareMonitor` (Visitor Pattern Implementation)
*   **Memory Discovery**: **Win32 P/Invoke (`GlobalMemoryStatusEx`)**
*   **Data Aggregation**: Windows `PerformanceCounter` API
*   **Animation**: WPF Storyboard + DoubleAnimation
*   **Persistence**: JSON-based config with multi-monitor coordinate validation

---

*Made with ❤️ by Cline for the community.*
