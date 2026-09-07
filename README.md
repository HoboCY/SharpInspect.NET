# SharpInspect.NET

通用工业视觉检测运行框架，围绕消费方提供的托管算法组织工位状态与设备流程。
实现按 [V1 工单](https://github.com/HoboCY/SharpInspect.NET/issues/1) 顺序推进。

当前入口是未配置的开发宿主：能观察完整状态、提交命令，并在追溯页按相关 ID 或
调用方声称的主体查询持久审计。生产、设备、算法及资格能力随后续工单交付。
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
pwsh -File tools/Test-Ticket03.ps1
```

脚本把每次运行的日志与环境记录保存在独立的 `artifacts/ticket03/<run>/`，
不会覆盖前次结果。独立消费项目使用隔离包缓存，确保运行的是本次打包内容。

## 包边界

| 项目 | NuGet ID | 职责 |
| --- | --- | --- |
| SharpInspect.Abstractions | SharpInspect.NET.Abstractions | 不可变快照、类型化命令和只读追溯契约 |
| SharpInspect.Runtime | SharpInspect.NET.Runtime | 无 UI 的工位权威、SQLite 单写协调器及独立只读查询 |
| SharpInspect.Wpf | SharpInspect.NET.Wpf | Dispatcher、快照时效、MVVM、状态及追溯窗口 |

消费宿主显式调用 `services.AddSharpInspectSqliteRuntime(new ProductionStoreOptions(databasePath))`，
按应用生命期持有并异步释放服务容器。`AddSharpInspectRuntime()` 保留无存储的未配置入口，
它不会受理需要审计的命令。样例通过 `--trace-db <absolute-path>` 指定数据库；
指定路径的父目录必须已存在。未指定时使用本机 LocalApplicationData 下的 `SharpInspect.SampleHost/trace.sqlite`。
Runtime 与 Abstractions 不引用 WPF、OpenCvSharp 或厂商 SDK，不扫描插件目录。
样例支持 `UseLocalPackages=true`，从本地 NuGet 包恢复相同公共入口。
这里只生成开发包，未发布到 NuGet。

UI 心跳和时效参数只控制状态呈现，不是 PLC 或生产时序政策。
`Accepted` 表示 Runtime 接管命令，最终状态必须从后续关联快照确认。
初版停止仅确认本地禁用，仍保留 PLC 握手及启动恢复的未知状态。

命令正常返回的 `Audit=Persisted` 表示相应 Outcome 已提交；`Audit=Unavailable` 明确表示
本次返回没有持久记录的保证，并保持非 Ready。Accepted 与 Completed/Failed 是分别追加的事实，
事务失败不能伪造完成。调用方的 Principal/Session/StepUp 值只是声称的归因输入，
当前系统记录者固定为非交互的 `SharpInspect.Runtime`，认证人员字段为空。

数据库必须位于固定本地 NTFS，路径验证会拒绝共享、已知云同步根、可移动盘和 reparse 路径。
实际写连接校验 WAL、FULL、foreign keys。未启用完整性政策的 Schema Version 为 1；
新空库显式启用政策时为 2；未知版本拒绝写入。旧库启用政策要求治理迁移，不自动补链。
只读查询每页 1–200 条，以记录位置和固定上界分页。更新、删除历史事实不属于公开 API。
SQLite `FULL` 与本机测试不构成断电耐久性或审计防篡改资格。

## 审计完整性开发入口

`ProductionStoreOptions.AuditIntegrityPolicy` 显式绑定工位、版本、密钥位置、检查点频率和核验预算。
默认未启用；查询显示 NotConfigured。开发样例可对全新空数据库使用 `--audit-key <unique-name>`，
并通过 `--audit-key-directory <absolute-path>` 选择独立密钥目录。`AllowInitialKeyCreation` 仅用于
当前非生产开发 bootstrap；正式站点初始化、身份授权、迁移与资格仍由后续工单提供。

事实、站点审计序号、规范字节和 SHA-256 链在同一 SQLite 事务中追加；检查点使用 P-256 签名。
私钥文件使用 Windows DPAPI LocalMachine 加密，并限制为运行账户、SYSTEM 和 Administrators 访问。
机器级 DPAPI 本身允许同机账户解密可读取的密文，文件访问控制是必要边界；它不提供硬件不可导出保证。
参考 [Microsoft 的保护范围说明](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)。
密钥文件不会进入查询或备份接口；验证脚本结束时只清理自己创建的临时保护密钥，保留公共事实与运行证据。

只读 `IAuditIntegrityQuery` 返回实际核验范围；分页游标会回溯到可信签名检查点，前缀也计入政策预算。
启动核验最近检查点至尾部，后台按政策节流核验保留历史；段通过不代表完整历史通过或生产 Ready。
结构错误保留证据并在本次 Runtime 中锁存故障。密钥缺失、已有密钥对应空库、政策改变均拒绝自动修复。

外部锚定仅在政策明确要求且宿主显式配置 `IExternalAuditAnchor` 时启用。路由须按 checkpoint ID 幂等，
框架保存不可变回执并从独立端读取最新接受结果，核对工位、路由、序列、head、政策与密钥。
外部调用超时或回执提交失败会阻止后续受控受理；已提交的 Accepted 保留其原事实。
本仓库的路由验收使用隔离适配器，未连接生产锚定服务。

更正、证据删除、密钥旋转和退休请求当前拒绝为 AuthorizationUnavailable，并保留拒绝事实。
它们不能改写历史；后续获授权的更正与处置须通过追加事件交付。
本机、数据库和私钥同时失陷时，攻击者可能重写未锚定历史；纯本地历史回滚也不保证被检测。
哈希链是篡改可检测机制，不是不可篡改存储或合规认证。

## 验证边界

逐项证据见 [V1-01](docs/verification/v1-01.md)、[V1-02](docs/verification/v1-02.md)
与 [V1-03 验证映射](docs/verification/v1-03.md)。
本机 Windows 11 Pro 的测试不构成 ADR-0004 中 Windows 10 22H2 三个版本的正式矩阵，
也不构成 Framework / Provider Qualification 或 Station Production Acceptance。
完整发行兼容矩阵、真实设备与现场验收保留在各自工单。
