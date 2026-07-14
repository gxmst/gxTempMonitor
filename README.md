# gxTempMonitor

轻量的 Windows 桌面硬件监控挂件，使用 .NET 10 / WPF 构建。项目默认采用用户态采集策略：不捆绑自有内核驱动、不注入其他进程，也不读取其他进程内存。

![gxTempMonitor preview](TempMonitor/assets/gxtmp.png)

[![CI](https://github.com/gxmst/gxTempMonitor/actions/workflows/ci.yml/badge.svg)](https://github.com/gxmst/gxTempMonitor/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](https://www.microsoft.com/windows)

## 功能

- 悬浮挂件显示 CPU、GPU、RAM、VRAM 和网络吞吐，可锁定鼠标穿透。
- Dashboard 显示仪表、趋势和硬件详情；隐藏时暂停高频 UI 刷新。
- 支持深色/浅色主题、透明度、告警、延迟启动和 CSV 导出。
- 采样间隔可选 1、2 或 5 秒。
- 全局热键和进程级 GPU 显存统计均可单独启用；默认关闭，避免占用快捷键或产生不必要的进程查询。
- 配置损坏、硬件接口缺失或厂商 API 返回异常时，相关指标显示 `--`，不会伪造为 0。

## 默认安全模式与边界

gxTempMonitor 的“默认安全模式”指它只使用 Windows 用户态接口和显卡驱动已提供的用户态 DLL：

- 不安装或加载项目自带的内核驱动；
- 不注入、挂钩或修改其他应用；
- 不读取其他进程内存；
- 以 `asInvoker` 普通用户权限运行，不请求 UAC 提权；
- 挂件始终使用不激活窗口样式；锁定后再启用鼠标穿透，尽量避免抢走前台应用焦点；
- 厂商 DLL 只应从 Windows 或显卡驱动的受保护位置加载，接口不可用时回退到 Windows 计数器。

可选的“进程级 GPU 显存”功能会读取 Windows `GPU Process Memory` 性能计数器，并按 PID 查询进程名称，用于显示显存占用最高的进程。它不会读取进程内存，但这意味着“完全不接触进程信息”的说法并不准确。

任何软件都无法承诺一定不会被安全软件或反作弊系统误报。最终判断由第三方产品、规则版本和本机策略决定。建议只从项目官方发布渠道下载，检查 Authenticode 发布者和 `SHA256SUMS.txt`；遇到误报时请保留文件哈希、产品版本和安全软件告警名称后提交 issue。

## 数据来源与兼容性

| 指标 | 首选来源 | 不可用时 |
|---|---|---|
| CPU 使用率 | Windows `GetSystemTimes` 时间差 | `--` |
| CPU 温度、功耗、频率 | 暂不采集 | 始终显示 `--` |
| RAM | `GlobalMemoryStatusEx` | `--` |
| NVIDIA GPU | NVIDIA NVML 用户态接口 | Windows GPU 计数器 |
| AMD GPU | AMD ADL 用户态接口 | Windows GPU 计数器 |
| Intel/其他 GPU | Windows GPU 计数器 | 不支持的指标显示 `--` |
| 进程显存 | Windows `GPU Process Memory`，可选 | 不显示进程名/占用 |
| 网络吞吐 | Windows 网络接口累计字节差 | `--` |

CPU Package 温度没有可靠、统一的 Windows 用户态接口。为了避免把 ACPI 热区温度误报成 CPU 温度，也为了不引入 MSR/SMBus 内核驱动，本版本不实现 CPU 温度采集。Dashboard 中相关位置保留为 `--`。

GPU 的温度、功耗和显存能力取决于显卡型号、驱动版本与厂商接口；风扇转速目前暂不采集。Windows 通用计数器通常只能提供负载和部分显存数据。多显卡环境会同时探测可用的 NVIDIA、AMD 与 Windows Provider，结合活跃度自动选择适配器；目前尚未提供手动选择界面。

运行环境受 .NET 10 的 Windows 支持范围约束，建议使用仍受支持的 Windows 10/11 版本。Windows 7/8 不在支持范围内。`win-arm64` 可以构建和运行，但厂商 GPU 指标仍取决于是否存在同架构的驱动接口；否则使用 Windows 通用回退。

程序清单启用 Per-Monitor V2 DPI，并监听显示器、缩放和任务栏工作区变化；在不同缩放比例的多显示器之间移动时会重新校正挂件位置。

## 交互与低干扰选项

| 操作或选项 | 效果 |
|---|---|
| 左键拖动 | 移动挂件 |
| 双击挂件 | 打开 Dashboard |
| 右键挂件或托盘 | 打开设置菜单 |
| 普通挂件 | 不激活前台窗口，仍可拖动和打开菜单 |
| 锁定挂件 | 在不激活基础上增加鼠标穿透 |
| 全局热键 | 可选启用 `Ctrl+Shift+M`；被其他程序占用时不会强抢 |
| 进程级 GPU 显存 | 可选启用；关闭时不执行对应的 PID 名称查询 |
| 采样频率 | 1 / 2 / 5 秒，较长间隔开销更低 |
| 开机延迟期间手动启动 | 手动启动立即生效；等待中的自启动实例随后退出 |
| 关闭 Dashboard | 隐藏窗口，不退出托盘程序 |
| 菜单“退出” | 完全退出 |

## 配置、日志与升级

配置和日志位于：

```text
%LocalAppData%\gxTempMonitor\config.json
%LocalAppData%\gxTempMonitor\TempMonitor.log
```

配置采用临时文件和原子替换写入，并会校验透明度、采样间隔、延迟时间等范围。升级后如果新位置尚无配置，程序会读取 exe 同目录的旧版 `config.json`，迁移成功后改用 `%LocalAppData%`。这也使程序可以从 `Program Files` 等只读目录运行。

开机自启动使用 Windows Shell Link 接口，不执行脚本。程序只更新可确认属于当前安装目录的 `gxTempMonitor.lnk` / 旧版 `TempMonitor.lnk`；遇到无法确认归属的同名快捷方式会拒绝覆盖或删除。

## 下载变体

| 变体 | 架构 | 是否需要运行库 | 适用场景 |
|---|---|---|---|
| Framework-dependent | x64 / ARM64 | 需要对应架构的 .NET 10 Desktop Runtime | 文件最小、由系统统一维护运行库 |
| Self-contained | x64 / ARM64 | 不需要预装 .NET | 便携部署，文件更大 |

两种变体都使用单文件发布，明确关闭 trimming，不使用混淆器、加壳器或第三方压缩壳。Self-contained 只使用 .NET 官方单文件压缩和 ReadyToRun。

如果系统已经安装 .NET 10 Desktop Runtime，优先选择 Framework-dependent：它不捆绑并压缩整套运行时，体积和启发式扫描面都更小。Self-contained 适合无法预装运行时的便携场景。

### 关于候选包和代码签名

`release-candidates.yml` 生成的 `unsigned-*` 文件称为“候选包”，表示它们是从确定源码自动构建、等待维护者冒烟测试的发布产物，并不表示程序本身不安全。个人项目没有 Authenticode 证书也可以发布，但 Windows 会显示“未知发布者”，SmartScreen 或安全软件也可能要求用户额外确认。

个人项目可以先采用以下透明发布方式：

- 从 Git 标签对应的干净提交通过 GitHub Actions 构建；
- 在 Release 页面明确标注文件尚未签名；
- 同时提供 `SHA256SUMS.txt` 和 SPDX SBOM；
- 不使用加壳器、混淆器或来源不明的二进制依赖；
- 保留可复现的构建配置，让用户能够自行核对源码与成品。

代码签名是发布者身份和防篡改能力的增强项，不是运行本项目或发布个人软件的硬性前提。将来取得证书后，应先签名并添加可信时间戳，再重新生成 SBOM 与 SHA-256 校验和。完整流程见 [release/SIGNING.md](release/SIGNING.md)。

验证下载文件：

```powershell
Get-AuthenticodeSignature .\gxTempMonitor.exe |
  Format-List Status,SignerCertificate,TimeStamperCertificate

Get-FileHash .\gxTempMonitor.exe -Algorithm SHA256
```

## 构建

需要 .NET 10 SDK。

```powershell
# 普通 RID-neutral 构建
dotnet build TempMonitor/TempMonitor.csproj -c Release

# Framework-dependent 单文件；默认 x64
.\build-light.bat
.\build-light.bat arm64

# Self-contained 单文件；默认 x64
.\build-standalone.bat
.\build-standalone.bat arm64
```

也可以直接使用四个独立发布配置：

```powershell
dotnet publish TempMonitor/TempMonitor.csproj -c Release -p:PublishProfile=FrameworkDependent-x64
dotnet publish TempMonitor/TempMonitor.csproj -c Release -p:PublishProfile=FrameworkDependent-arm64
dotnet publish TempMonitor/TempMonitor.csproj -c Release -p:PublishProfile=SelfContained-x64
dotnet publish TempMonitor/TempMonitor.csproj -c Release -p:PublishProfile=SelfContained-arm64
```

输出位于 `publish/framework-dependent/` 或 `publish/self-contained/`。CI 会执行 Release 构建、NuGet 直接/传递依赖审计，并在存在测试项目时运行测试。

## 项目结构

```text
TempMonitor/
├── App.xaml.cs                   启动、单实例和异常日志
├── AppConfig.cs                  配置校验、迁移与原子保存
├── MainWindow.xaml(.cs)          悬浮挂件、托盘和低干扰选项
├── DashboardWindow.xaml(.cs)     Dashboard
├── HardwareMonitorService.cs     共享采集、历史数据和 CSV
├── NvidiaGpuMonitor.cs           NVIDIA NVML Provider
├── AmdGpuMonitor.cs              AMD ADL Provider
├── WindowsGpuCounterMonitor.cs   Windows GPU 回退 Provider
├── Properties/PublishProfiles/   四种明确的发布配置
├── TempMonitor.csproj            产品元数据、DPI 与通用构建配置
└── app.manifest                  普通权限、长路径和 Windows 兼容声明
TempMonitor.Tests/                ABI、解析、配置与导出回归测试
```

## 贡献与许可证状态

开发与验证要求见 [CONTRIBUTING.md](CONTRIBUTING.md)。

截至目前，远端仓库没有声明开源许可证，GitHub API 也未识别到许可证。在版权所有者加入明确的 `LICENSE` 文件之前，代码默认处于“保留所有权利”状态；请不要根据徽章或仓库可见性推定为 MIT、Apache-2.0 等许可证。
