# Contributing to gxTempMonitor

感谢你愿意改进 gxTempMonitor。项目优先级依次是：采集结果可信、对其他应用低干扰、失败时安全退化、资源开销可控，最后才是增加传感器数量。

> 当前仓库尚未声明开源许可证。提交贡献前，请确认你有权提交相关代码，并理解仓库可见不等于已授予开源许可。版权所有者应在接受外部贡献前补充明确的许可证与贡献条款。

## 开发环境

- 仍受支持的 Windows 10/11
- `global.json` 指定的 .NET 10 SDK feature band
- 支持 .NET 10 / WPF 的 Visual Studio 或其他编辑器（可选）
- 对应厂商显卡与驱动（仅在修改 NVML/ADL Provider 时需要）

```powershell
dotnet restore TempMonitor.Tests/TempMonitor.Tests.csproj --locked-mode
dotnet build TempMonitor/TempMonitor.csproj -c Release --warnaserror
```

主项目的构建目标会读取 `app.manifest`，并要求 `assemblyIdentity/@version` 与项目的 `AssemblyVersion` 完全一致；升级版本时两处必须同时更新。

## 安全与兼容性约束

以下规则属于项目边界，改变它们需要在 PR 中单独说明风险、用户授权方式和回退方案：

- 不捆绑或静默安装自有内核驱动。
- 不注入、挂钩、修改或读取其他进程内存。
- 进程级指标必须可关闭；默认关闭时不得枚举 PID 或解析进程名。
- 不从当前工作目录或程序目录按裸文件名加载厂商 DLL。原生库必须来自预期的 Windows/厂商驱动位置，并尽可能验证签名。
- P/Invoke 结构体布局、字段宽度、调用约定和函数参数必须与官方头文件一致。
- 原生 API 的异常、缺失导出和异常数值必须 fail-closed：返回不可用并回退，不能继续冒险调用。
- 不可用的传感器使用 nullable 值并在 UI 显示 `--`，不要用 0 伪装有效读数。
- 不承诺“绝不触发反作弊/安全软件”。文档必须准确描述真实行为。
- 不加入混淆器、加壳器或来源不明的二进制依赖。

CPU Package 温度暂不在实现范围内。不要用 ACPI Thermal Zone 冒充 CPU 温度，也不要为此加入 MSR/SMBus 驱动。若未来增加扩展传感器模式，必须由用户明确启用并说明依赖。

## 采集与性能准则

- `HardwareMonitorService` 是唯一共享采集核心，窗口不得各自创建重复采集器。
- Dashboard 隐藏后应停止 UI 动画和高频控件更新。
- 高频路径避免每次采样创建大量计数器、数组、几何图形或进程对象。
- 历史数据使用固定容量结构；不要在长列表头部持续 `RemoveAt(0)`。
- 驱动重启、睡眠恢复和临时接口失败应采用有界重试或退避，不能形成忙循环。
- 新的后台功能需要取消/释放路径，应用退出后不得遗留线程、计时器、热键或句柄。
- 所有 Win32 返回值都应检查；资源所有权必须清晰。

## 原生 Provider 改动

修改 NVML、ADL 或其他原生接口时，PR 至少应包含：

1. 官方 SDK/头文件的版本和链接；
2. `Marshal.SizeOf<T>()` 与关键 `Marshal.OffsetOf<T>()` 的验证；
3. x64 与 ARM64 的行为说明；
4. 缺 DLL、缺导出、不支持指标和驱动重启时的测试；
5. 多显卡场景中的适配器选择与标识方式；
6. 原生数值的范围检查和单位转换。

不要仅凭网上零散代码片段复制 ABI 定义。

## 配置、日志与导出

- 用户可写数据保存到 `%LocalAppData%\gxTempMonitor`，不要写入安装目录。
- 配置新增字段必须有安全默认值、范围校验，并兼容旧配置缺字段的情况。
- 配置结构变更必须递增 `SchemaVersion` 并提供显式迁移；导入路径必须重新执行与正常加载相同的校验。
- 配置写入使用临时文件与原子替换，避免崩溃后留下半个 JSON。
- 日志不得记录访问令牌、路径中的敏感信息或完整环境变量。
- CSV 使用固定文化格式和 RFC 4180 转义；可能被表格软件解释为公式的文本必须转义。

## 本地验证

提交前至少执行：

```powershell
dotnet restore TempMonitor.Tests/TempMonitor.Tests.csproj `
  --locked-mode `
  -p:NuGetAudit=true `
  -p:NuGetAuditMode=all

dotnet list TempMonitor/TempMonitor.csproj package `
  --vulnerable `
  --include-transitive `
  --no-restore

dotnet build TempMonitor/TempMonitor.csproj -c Release --no-restore --warnaserror

.\build-light.bat x64
.\build-light.bat arm64
.\build-standalone.bat x64
.\build-standalone.bat arm64
```

运行回归测试：

```powershell
dotnet test TempMonitor.Tests/TempMonitor.Tests.csproj -c Release --no-restore --warnaserror
```

建议手工覆盖以下场景：

- NVIDIA、AMD、Intel，以及核显 + 独显；
- 缺少厂商 DLL、性能计数器不可用或部分指标不支持；
- 中英文 Windows、不同缩放比例和多显示器；
- 睡眠/唤醒、显卡驱动重启、RDP 和用户切换；
- 从只读目录运行；
- 从最终发布 EXE 提取嵌入清单，确认 `asInvoker`、Per-Monitor V2 DPI 和 long-path 声明没有在 SDK 发布过程中丢失；
- 自启动仍在延迟时手动启动，确认手动实例立即显示且最终只有一个实例；
- 全局热键已被占用；
- 进程显存开关关闭时确认不会查询进程名；
- Dashboard 反复显示/隐藏后的 CPU、内存和句柄趋势。

## 发布

普通 `Release` 构建保持 RID-neutral。发布必须使用 `TempMonitor/Properties/PublishProfiles` 中的命名配置，避免命令行属性与项目默认值互相覆盖。

| Profile | 运行库 | 架构 |
|---|---|---|
| `FrameworkDependent-x64` | 依赖 .NET 10 Desktop Runtime | x64 |
| `FrameworkDependent-arm64` | 依赖 .NET 10 Desktop Runtime | ARM64 |
| `SelfContained-x64` | 自包含 | x64 |
| `SelfContained-arm64` | 自包含 | ARM64 |

公开发布可以选择两条明确流程：

- 个人项目暂不签名时：全新目录发布 → 冒烟测试 → 明确标注 `unsigned` → 生成发布载荷 SHA-256 与 SBOM → 保留构建来源证明 → 上传；
- 具备 Authenticode 证书时：全新目录发布 → 冒烟测试 → 签名与可信时间戳 → 验签 → 重新生成发布载荷 SHA-256、SBOM 与来源证明 → 上传。

签名会改变哈希，不能沿用签名前的 SBOM、校验和或 provenance。详见 [release/SIGNING.md](release/SIGNING.md)。

## Pull Request

PR 描述请包含：

- 用户可见结果与不在范围内的内容；
- 兼容性、安全性和性能影响；
- 验证过的硬件、Windows 版本和发布架构；
- 失败/回退路径；
- UI 改动截图（如适用）；
- 构建、测试与依赖审计结果。

保持改动边界清晰，不要把无关重构、格式化和功能混在同一个 PR 中。
