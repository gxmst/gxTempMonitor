# gxTempMonitor

一款专为 Windows 打造的、像素级精致的双语系统监控挂件。
A pixel-perfect system monitoring widget designed for Windows with dual-language support.

[![License](https://img.shields.io/github/license/gxmst/gxTempMonitor?style=flat-square)](LICENSE)
[![Release](https://img.shields.io/github/v/release/gxmst/gxTempMonitor?style=flat-square)](https://github.com/gxmst/gxTempMonitor/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/framework-.NET%2010.0-purple?style=flat-square)](https://dotnet.microsoft.com/download)

---

## 🎯 核心亮点 / Key Features

*   **🎨 原生交互 (Native Experience)**：
    *   深度集成 Windows 视觉风格，采用 60% 透明度深空灰磨砂背景与 1px 微光白边。
    *   支持**边缘磁吸吸附**与**智能双向生长**（靠右时向左展开）。
*   **⚡ 极致性能 (Extreme Performance)**：
    *   基于 Win32 API (P/Invoke) 和 PerformanceCounter，实现 0 延迟数据采集，极低系统资源占用。
*   **🔍 真实数据 (True Data)**：
    *   采用底层物理级内存探测算法，拒绝虚拟内存虚标。
    *   专用显存 (Dedicated VRAM) 精确过滤，还原最真实的显卡占用。
*   **🌈 动态交互 (Dynamic Interaction)**：
    *   **智能红绿灯**：根据负载自动切换颜色（白色正常、橙色预警、红色高载）。
    *   **悬浮自动扩展**：鼠标悬停即平滑展开查看历史最高峰值 (MAX)。
*   **🚀 开机即用 (Portable & Ready)**：
    *   单文件绿色版，无环境依赖，支持一键设置开机自启。

---

## 🔍 交互指南 / Usage Guide

*   **吸附 (Snap)**：将窗口拖动到屏幕左右边缘 50px 范围内自动贴合。
*   **展开 (Expand)**：鼠标悬停查看详细 MAX 数据（支持智能边缘避让）。
*   **快捷控制 (Shortcuts)**：
    *   **点击 CPU 行**：瞬间启动任务管理器 (Task Manager)。
    *   **右键菜单**：切换“开机自启”、重置最大值、退出。
    *   **双击窗口**：快速退出。

---

## 🛠️ 技术栈 / Under the Hood

*   **Framework**: .NET 10.0 (WPF)
*   **Core Logic**: `LibreHardwareMonitor` (Visitor Pattern Implementation)
*   **Memory Tech**: **Win32 P/Invoke (`GlobalMemoryStatusEx`)** for 100% physical accuracy.
*   **Data Source**: `PerformanceCounter` API for low-overhead CPU/Net monitoring.
*   **Animation**: WPF Storyboard + DoubleAnimation for zero-lag transitions.
*   **Persistence**: JSON serialization with cross-monitor coordinate validation.

---

## 🚀 快速开始 / Quick Start

1.  从 [Releases](https://github.com/gxmst/gxTempMonitor/releases) 下载最新的 `TempMonitor.exe`。
2.  直接双击运行（无需安装，无需管理员权限）。
3.  享受极致精致的监控体验。

Download the latest `TempMonitor.exe` from [Releases](https://github.com/gxmst/gxTempMonitor/releases) and enjoy!

---

*Made with ❤️ by Cline for the community.*
