# gxTempMonitor

轻量、低干扰的 Windows 桌面状态挂件，使用 .NET 10 / WPF 构建。项目默认采用用户态采集策略：不捆绑自有内核驱动、不注入其他进程，也不读取其他进程内存。

![gxTempMonitor preview](TempMonitor/assets/gxtmp.png)

> 上图是早期视觉稿。程序实际采样间隔为 1、2 或 5 秒；“低开销”不等同于零延迟。

[![CI](https://github.com/gxmst/gxTempMonitor/actions/workflows/ci.yml/badge.svg)](https://github.com/gxmst/gxTempMonitor/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](https://www.microsoft.com/windows)

## 功能

- 悬浮挂件显示 CPU、GPU、RAM、VRAM 和网络吞吐；显示项可隐藏、排序，并可切换容量、百分比和网络单位。
- GPU 主数值默认优先显示温度；温度不可用时自动回退为 GPU 使用率，也可手动固定为温度、使用率或功耗。
- Dashboard 根据数据源能力显示卡片，不再为 CPU 温度、功耗、频率或 GPU 风扇保留永久 `--`；历史范围可选 1、10、30 或 60 分钟。
- 正式设置窗口集中管理显示、设备、告警、低干扰、配置导入导出和诊断信息；首次启动提供简短操作提示。
- 支持 GPU 固定设备以及网卡自动、汇总或固定模式；设备不可用时安全回退。
- 告警支持自定义 CPU 使用率、GPU 温度和 RAM 使用率阈值，以及持续时间、恢复滞回、冷却时间、变色、托盘通知或闪烁。
- 安全系统信息包括 CPU 名称/架构/逻辑处理器、系统运行时间、笔记本电池/交流电和系统盘容量。
- 支持深色/浅色主题、透明度、始终置顶、全屏时保持/隐藏/变暗、延迟启动和 CSV 导出。
- 采样间隔可选 1、2 或 5 秒。
- 全局热键和进程级 GPU 显存统计均可单独启用；默认关闭，避免占用快捷键或产生不必要的进程查询。进程显存按 PID 汇总全部 GPU 适配器的数据，不表示某一块固定 GPU 的单独占用。
- 现有配置损坏、过大或无法安全读取时，会以只读默认状态运行且不覆盖原文件；硬件接口缺失或厂商 API 返回异常时，相关指标显示 `--`，不会伪造为 0。
- “诊断”页显示 Provider、指标能力、采样耗时、工作集和句柄数，可复制脱敏报告；不会自动上传。
- 更新检查仅在用户点击后访问官方 GitHub Release API，只提示并打开发布页，不后台重试、不自动下载或替换 EXE。

## 默认安全模式与边界

gxTempMonitor 的“默认安全模式”指它只使用 Windows 用户态接口和显卡驱动已提供的用户态 DLL：

- 不安装或加载项目自带的内核驱动；
- 不注入、挂钩或修改其他应用；
- 不读取其他进程内存；
- 以 `asInvoker` 普通用户权限运行，不请求 UAC 提权；
- 挂件始终使用不激活窗口样式；锁定后再启用鼠标穿透，尽量避免抢走前台应用焦点；
- 厂商 DLL 只应从 Windows 或显卡驱动的受保护位置加载，接口不可用时回退到 Windows 计数器。

可选的“进程级 GPU 显存”功能会读取 Windows `GPU Process Memory` 性能计数器，按 PID 汇总全部 GPU 适配器的 Dedicated Usage，再查询进程名称，用于显示总显存占用最高的进程。它不会读取进程内存，也不按当前固定 GPU 过滤，但这意味着“完全不接触进程信息”的说法并不准确。

任何软件都无法承诺一定不会被安全软件或反作弊系统误报。最终判断由第三方产品、规则版本和本机策略决定。建议只从项目官方发布渠道下载，检查 Authenticode 发布者和 `SHA256SUMS.txt`；遇到误报时请保留文件哈希、产品版本和安全软件告警名称后提交 issue。

## 数据来源与兼容性

| 指标 | 首选来源 | 不可用时 |
|---|---|---|
| CPU 使用率 | Windows `GetSystemTimes` 时间差 | `--` |
| CPU 温度、功耗、频率 | 暂不采集 | 不显示对应卡片，诊断页注明设计边界 |
| CPU 名称、架构、逻辑处理器 | 注册表只读信息、Windows 处理器组 API | 对应信息不显示 |
| RAM | `GlobalMemoryStatusEx` | `--` |
| NVIDIA GPU | NVIDIA NVML 用户态接口 | Windows GPU 计数器 |
| AMD GPU | AMD ADL 用户态接口 | Windows GPU 计数器 |
| Intel/其他 GPU | Windows GPU 计数器 | 不支持的指标显示 `--` |
| 进程显存 | Windows `GPU Process Memory`，可选；按 PID 汇总全部 GPU | 不显示进程名/占用 |
| 网络吞吐 | Windows 网络接口累计字节差 | `--` |
| 电池和交流电 | Windows `GetSystemPowerStatus` | Dashboard 显示不可用 |
| 系统盘容量 | Windows 系统盘普通文件系统信息，60 秒刷新 | Dashboard 显示不可用 |
| 系统运行时间 | `.NET Environment.TickCount64` 系统运行时计数 | `--` |

CPU Package 温度没有可靠、统一的 Windows 用户态接口。为了避免把 ACPI 热区温度误报成 CPU 温度，也为了不引入 MSR/SMBus 内核驱动，本版本不实现 CPU 温度采集；Dashboard 不显示对应卡片，诊断页会注明这一设计边界。

GPU 的温度、功耗和显存能力取决于显卡型号、驱动版本与厂商接口；风扇转速目前暂不采集。Windows 通用计数器通常只能提供负载和部分显存数据。当 NVIDIA/AMD 原生接口持续正常供数时，Windows 通用计数器作为后备约每 10 秒刷新一次以降低采样开销；原生接口失效、被选为当前 Provider 或触发设备刷新时会立即恢复正常读取。多显卡环境会同时探测可用的 NVIDIA、AMD 与 Windows Provider，默认结合活跃度自动选择；也可以在设置中按 `Provider + 设备标识` 固定设备，目标消失时回退到自动选择。NVIDIA UUID 和 AMD PnP 标识通常可跨会话保持；Windows Provider 使用的 LUID 只保证当前系统会话内稳定，重启或驱动重置后可能变化，此时程序会安全回退到自动选择，用户可重新固定设备。

运行环境受 .NET 10 的 Windows 支持范围约束，建议使用仍受支持的 Windows 10/11 版本。Windows 7/8 不在支持范围内。`win-arm64` 可以构建和运行，但厂商 GPU 指标仍取决于是否存在同架构的驱动接口；否则使用 Windows 通用回退。

程序清单启用 Per-Monitor V2 DPI，并监听显示器、缩放和任务栏工作区变化；在不同缩放比例的多显示器之间移动时会重新校正挂件位置。

## 交互与低干扰选项

| 操作或选项 | 效果 |
|---|---|
| 左键拖动 | 移动挂件 |
| 双击挂件 | 打开 Dashboard |
| 右键挂件或托盘 | 打开设置菜单 |
| 正式设置窗口 | 调整显示顺序、设备、告警、全屏行为、隐私选项与诊断 |
| 普通挂件 | 不激活前台窗口，仍可拖动和打开菜单 |
| 锁定挂件 | 在不激活基础上增加鼠标穿透 |
| 全局热键 | 可选启用 `Ctrl+Shift+M`；被其他程序占用时不会强抢 |
| 进程级 GPU 显存 | 可选启用；按 PID 汇总全部 GPU，关闭时不执行对应的 PID 名称查询 |
| 采样频率 | 1 / 2 / 5 秒，较长间隔开销更低 |
| 自适应采样 | 挂件和 Dashboard 都不可见时可自动降到 5 秒 |
| 全屏行为 | 保持显示、自动隐藏或降低透明度；只比较窗口与显示器边界，不安装钩子 |
| GPU 设备 | 自动选择或固定设备；不可用时回退自动，Windows Provider 的固定标识可能在重启或驱动重置后变化 |
| 网络设备 | 自动选择、汇总全部或固定接口 |
| 告警 | 默认仅变色；可选择托盘通知或明确启用闪烁，并带持续时间、滞回和冷却 |
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

配置包含 `SchemaVersion` 并执行显式迁移；导入文件限制为 256 KiB，枚举、数值、设备标识和显示顺序都会重新校验。设置页可手动导出或导入 JSON，导入时保留当前挂件位置。若现有配置来自未来版本、超过限制、损坏或暂时无法读取，程序会将本次会话标记为只读并保留原文件；需要重置时请先备份或移走该文件，再重启程序。

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
- 候选包工作流先执行 locked restore、Release 测试和 NuGet 漏洞审计；
- 在 Release 页面明确标注文件尚未签名；
- 同时提供 `SHA256SUMS.txt` 和 SPDX SBOM；
- GitHub Actions 为候选 EXE 生成构建来源证明（artifact attestation）；
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

需要 `global.json` 指定的 .NET 10 SDK feature band。NuGet 使用 `packages.lock.json`；CI 和候选包流程以 locked mode 恢复，并在打包前运行 Release 测试与依赖审计。

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
├── SettingsWindow.xaml(.cs)      正式设置、设备、诊断和手动更新检查
├── WelcomeWindow.xaml(.cs)       首次启动提示
├── AlertEngine.cs                告警持续时间、滞回与冷却状态机
├── FullscreenDetector.cs         无钩子的全屏边界检测
├── HardwareMonitorService.cs     共享采集、历史数据和 CSV
├── SystemMetricsReader.cs        CPU 信息、运行时间、电池和系统盘
├── SnapshotHistory.cs            时间范围筛选与降采样
├── DiagnosticReportBuilder.cs    隐私安全的诊断报告
├── NvidiaGpuMonitor.cs           NVIDIA NVML Provider
├── AmdGpuMonitor.cs              AMD ADL Provider
├── WindowsGpuCounterMonitor.cs   Windows GPU 回退 Provider
├── Properties/PublishProfiles/   四种明确的发布配置
├── TempMonitor.csproj            产品元数据、DPI 与通用构建配置
└── app.manifest                  普通权限、长路径和 Windows 兼容声明
TempMonitor.Tests/                ABI、解析、配置、告警、历史与诊断回归测试
```

## 贡献与许可证状态

开发与验证要求见 [CONTRIBUTING.md](CONTRIBUTING.md)。

截至目前，远端仓库没有声明开源许可证，GitHub API 也未识别到许可证。在版权所有者加入明确的 `LICENSE` 文件之前，代码默认处于“保留所有权利”状态；请不要根据徽章或仓库可见性推定为 MIT、Apache-2.0 等许可证。
