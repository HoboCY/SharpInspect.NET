# SharpInspect.NET

通用工业视觉检测运行框架，围绕消费方提供的托管算法组织工位状态与设备流程。
实现按 [V1 工单](https://github.com/HoboCY/SharpInspect.NET/issues/1) 顺序推进。

当前入口是 V1-01 的未配置宿主：能观察完整状态、提交武装请求并看到拒绝原因，
也能通过本机停止入口保持禁用。生产、设备、算法及资格能力随后续工单交付。
**当前宿主不能进入 Ready，也不能用于投产。**

## 构建与运行

需要 Windows x64、.NET 8 SDK（global.json 的 8.0.300 或同系列后续 feature band），
以及 .NET 6 Desktop Runtime。包兼容下限仍为 `net6.0` / `net6.0-windows`。
构建会保留 SDK 的 `NETSDK1138` 提示：.NET 6 已结束支持；兼容下限不延长其安全维护周期。

```powershell
dotnet build SharpInspect.NET.sln -c Release
dotnet test SharpInspect.NET.sln -c Release --no-build
dotnet run --project samples/SharpInspect.SampleHost -c Release
```

普通关窗会遮蔽页面内容并保留后台 Runtime，本机停止按钮仍可到达；
重新显示页面只恢复显示，不授予身份或权限。身份认证将在后续工单交付。
此开发宿主不提供生产启用或宿主退出旁路，调试结束可由调试器终止进程；
进程终止不代表完成了 PLC 停产握手。生产部署的受控关闭另有工单。

```powershell
# 自动测试、实际 WPF 宿主 smoke、打包及独立 NuGet 消费
pwsh -File tools/Test-Ticket01.ps1
```

脚本把每次运行的日志与环境记录保存在独立的 `artifacts/ticket01/<run>/`，
不会覆盖前次结果。独立消费项目使用隔离包缓存，确保运行的是本次打包内容。

## 包边界

| 项目 | NuGet ID | 职责 |
| --- | --- | --- |
| SharpInspect.Abstractions | SharpInspect.NET.Abstractions | 不可变快照与类型化命令 |
| SharpInspect.Runtime | SharpInspect.NET.Runtime | 无 UI 的工位权威、拒绝未满足准入的命令 |
| SharpInspect.Wpf | SharpInspect.NET.Wpf | Dispatcher、快照时效、MVVM 和窗口 |

消费宿主显式调用 `services.AddSharpInspectRuntime()`，按应用生命期持有服务容器。
Runtime 与 Abstractions 不引用 WPF、OpenCvSharp 或厂商 SDK，不扫描插件目录。
样例支持 `UseLocalPackages=true`，从本地 NuGet 包恢复相同公共入口。
这里只生成开发包，未发布到 NuGet。

UI 心跳和时效参数只控制状态呈现，不是 PLC 或生产时序政策。
`Accepted` 表示 Runtime 接管命令，最终状态必须从后续关联快照确认。
初版停止仅确认本地禁用，仍保留 PLC 握手及启动恢复的未知状态。

## 验证边界

逐项证据见 [V1-01 验证映射](docs/verification/v1-01.md)。
本机 Windows 11 Pro 的测试不构成 ADR-0004 中 Windows 10 22H2 三个版本的正式矩阵，
也不构成 Framework / Provider Qualification 或 Station Production Acceptance。
完整发行兼容矩阵、真实设备与现场验收保留在各自工单。
