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
重新显示页面只恢复显示，不授予身份或权限。显式配置本地身份后可建立首位个人管理员并登录，
会话保护由 Runtime-owned `IInteractiveSessionService` 管理；个人账号管理通过 Permission 与动作绑定的 Step-Up 执行。
此开发宿主不提供生产启用或宿主退出旁路，调试结束可由调试器终止进程；
进程终止不代表完成了 PLC 停产握手。生产部署的受控关闭另有工单。

```powershell
# 自动测试、实际 WPF 宿主 smoke、打包及独立 NuGet 消费
pwsh -File tools/Test-Ticket18.ps1
pwsh -File tools/Test-Ticket19.ps1
pwsh -File tools/Test-Ticket20.ps1
pwsh -File tools/Test-Ticket21.ps1
pwsh -File tools/Test-Ticket22.ps1
pwsh -File tools/Test-Ticket23.ps1
```

脚本把每次运行的日志与环境记录保存在独立的 `artifacts/ticketNN/<run>/`（NN 为票号），
不会覆盖前次结果。独立消费项目使用隔离包缓存，确保运行的是本次打包内容。
空间不足时可添加 `-ArtifactRoot E:\SharpInspectEvidence\artifacts`，把验证产物与隔离包缓存
放到另一个本地卷；源码、锁文件和验证步骤保持相同。

## 包边界

| 项目 | NuGet ID | 职责 |
| --- | --- | --- |
| SharpInspect.Abstractions | SharpInspect.NET.Abstractions | 不可变快照、类型化命令和只读追溯契约 |
| SharpInspect.Runtime | SharpInspect.NET.Runtime | 无 UI 的工位权威、SQLite 单写协调器及独立只读查询 |
| SharpInspect.Wpf | SharpInspect.NET.Wpf | Dispatcher、快照时效、MVVM、状态及追溯窗口 |
| SharpInspect.OpenCvSharp | SharpInspect.NET.OpenCvSharp | 受控范围内的零拷贝 Mat 视图与显式独立副本 |
| SharpInspect.Calibration.OpenCvSharp | SharpInspect.NET.Calibration.OpenCvSharp | 类型化棋盘格内参过程、畸变系数与逐图／逐点计算证据 |
| SharpInspect.Cameras.Virtual | SharpInspect.NET.Cameras.Virtual | 使用显式场景与虚拟时间的开发相机模拟器 |
| SharpInspect.Cameras.Hikrobot | SharpInspect.NET.Cameras.Hikrobot | 独立 MVS 适配、只读依赖诊断与受控单帧开发验证 |
| SharpInspect.Cameras.Conformance | SharpInspect.NET.Cameras.Conformance | 可复用公共相机契约场景、冻结映射及开发验证证据 |

## 成像修订与标定需求

Recipe 的 `CalibrationRequirements` 只声明逻辑角色、种类、用途及精确系数格式／验收政策契约；
系数保存在独立不可变的 `CalibrationProfileContent`。当前 `CalibrationRequirementResolver` 使用显式隔离夹具，
按 Profile ID、版本、内容哈希及当前设备、成像修订、Requested／Effective 几何逐项检查。
兼容结果仍是开发诊断，不能授权依赖该标定的调试或生产激活；无需求时不会生成默认系数。

启用 `ProductionStoreOptions.ImagingSetup` 后，`IImagingSetupRuntime` 与 `ImagingSetupPanel` 可登记经授权确认的
镜头、调焦、支架、距离和传感器方向变化，并查询不可变历史。此扩展要求相机绑定与签名身份审计存储，
重连和网络维护扩展各自可选。旧数据库保持原格式；启用新账本需要显式迁移流程。
完整边界与验证映射见 [V1-23 记录](docs/verification/v1-23.md)。

## 标定会话与候选证据

`AddSharpInspectCalibrationSessions` 注册独占会话协调器，强类型 `ICalibrationProcedure<TInput>`
通过显式 DI 注册消费借用帧与不可变输入。`CalibrationSessionPanel` 提交开始、采集、整帧排除、
计算与退出命令，并读取原始图像、自动观测和候选；坐标不可编辑，候选不能发布或激活。

Schema 14 显式启用标定证据扩展，依赖签名身份审计、Camera Setup、Recovery 与 Imaging Setup。
临时设置、原配置、源图像哈希及会话结局共同保留；退出恢复并读回基线后才结束独占。
当前真实安全停线与 Released Recipe 准入仍缺证，普通入口返回 `SafetyStopUnverified`。
隔离 Virtual 开发夹具可验证流程，始终保持 `Ready=false`，不构成生产或数学过程合格。
验证范围与异常关闭边界见 [V1-24 记录](docs/verification/v1-24.md)。

可选 `SharpInspect.NET.Calibration.OpenCvSharp` 包提供 `CheckerboardIntrinsicsProcedure`。
输入明确内角点行列数、毫米方格尺寸、逻辑相机角色和完整实际配置；过程只读借用像素，
自动提取角点并使用所有选中观测计算相机矩阵、Brown–Conrady 畸变和残差。
`CheckerboardIntrinsicsResultCodec` 分别解码系数与结构化证据；后者保留逐图姿态、覆盖及逐点残差，
通过帧 ID、来源哈希和角点 ID 关联原始观测。生产验收仍由独立的项目政策决定。
原生 OpenCV 运行时由宿主明确选择；核心 Runtime／WPF 无 OpenCV 依赖。
输入、兼容及合成图片验证边界见 [V1-25 记录](docs/verification/v1-25.md)。

## Camera Provider 契约验证

Provider 维护者实现 `ICameraConformanceFixtureFactory`，提供固定身份、成像配置、规范像素摘要、
外部刺激与真实资源清理完成信号，再将 `CameraConformanceSuite.Scenarios` 接入 T07 的执行设施。
套件自己从 Provider／Device／Runtime 公共接口观察结果；不引用 WPF 或厂商适配器。
可选扩展显式注册并合入冻结 Profile，缺少观察设施的必需项保持 Blocked。

```powershell
$revision = git rev-parse HEAD
dotnet run --project samples/SharpInspect.CameraConformance.Probe -c Release -- run E:\SharpInspectEvidence\camera-conformance $revision
dotnet run --project samples/SharpInspect.CameraConformance.Probe -c Release -- query E:\SharpInspectEvidence\camera-conformance $revision
```

示例仅运行 Virtual Camera，不访问真实设备。每次运行使用新的空证据目录；独立 query 校验不可覆盖记录和原始证据。
公共行为通过不构成真实厂商组合的 Hardware Qualification，正式支持清单仍由独立准入门决定。
范围与稳定验证 ID 见 [V1-22 记录](docs/verification/v1-22.md)。

## Hikrobot 适配器开发入口

Hikrobot 包仅包含托管适配器，MVS Runtime、相机驱动及厂商服务由设备集成方另行安装。
当前生产兼容清单为空，公开 Provider 的发现与打开均拒绝，不能进入 Ready。
不安装 MVS 也能使用默认诊断入口，输出安全的版本、架构、组件存在性和稳定失败原因；
它不会加载 SDK 或访问设备。

```powershell
dotnet run --project samples/SharpInspect.Hikrobot.DeviceProbe -c Release -- --diagnose
```

独立设备探针只在显式设备测试授权后运行，要求本机 Runtime 文件哈希、稳定设备身份、型号和完整配置。
候选 native backend 限定 Windows x64 / Runtime 4.8.1.2、SoftwareTrigger，以及设备实际支持的 Mono8 与 unpacked Mono10/12/16。
硬触发尚未鉴定，探针在 SDK 加载前明确拒绝。
当前并未验证该 Runtime 的真机兼容性；托管测试、头文件布局和静态 DLL 导出检查不等同于生产资格。
工程候选每个物理句柄仅采集一次；再次采集需完整 Dispose 后重新 Open，同一句柄重复采集尚不支持。
安装边界、受限测试命令和验证映射见 [V1-21 记录](docs/verification/v1-21.md)，
具体 SDK 来源与版本差异见 [SDK 来源证据](docs/verification/hikrobot-sdk-sources.md)。

## Virtual Camera 开发入口

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --virtual-camera-check D:\SharpInspectEvidence\virtual-camera
```

示例显式构造 `VirtualCameraScenario` 和 `VirtualCameraClock`，经 `ICameraProvider` 只读发现、
按稳定设备身份打开，再经 `ICameraDevice` 完整配置、启动并请求一帧。成功的
`FrameAcquisitionResult.Lease` 由调用方持有和释放；`VisionFrame` 是借用视图，
`FrameProvenance` 单独记录来源。停止设备或释放 Provider 不会提前回收已交付的租约。

图像可由固定种子合成，也可用 `VirtualCameraImage.LoadRecordedRaw` 从显式文件路径、
行布局及预期 SHA-256 加载。场景冻结图像、能力、身份、版本、种子和有序故障脚本，
不自动枚举目录或循环重放。Mono16 支持 10/12/16 有效位；其奇数源步长由共用帧池对齐。
调用方推进虚拟时间以触发采集结果，UTC 调整不会改变单调时间的截止点。
帧池仍使用真实 CPU 复制预算，基础设施失败会使验证失败。

`Test-Ticket16.ps1` 在两个独立进程中执行相同输入，比较 `replay-evidence.json` 的完整字节哈希。
证据列出帧、时间、健康状态和失败结果，另在 `summary.json` 保留执行范围。
该独立重放入口不执行 Runtime 的生产受理或硬触发 Busy 门控，也不产生真实设备资格。
本票的验证映射见 [V1-16 记录](docs/verification/v1-16.md)。

## 相机绑定与配置调试

宿主显式注册 `ICameraProvider`，调用 `AddSharpInspectCameraSetup(new CameraSetupOptions())`，
并在 `ProductionStoreOptions` 配置 `CameraSetup = new CameraSetupStoreOptions()`、本地身份和审计政策。
`ICameraSetupRuntime` 与 `IStationRuntime` 指向同一个 Runtime；`CameraSetupViewModel` 和
`CameraSetupPanel` 使用该入口完成只读发现、明确选择设备、改绑和完整配置读回。
默认不发现设备，不按列表顺序选中设备。

绑定记录保存精确 Provider 身份和 Stable Device Identity。改绑与调试配置都需要
`ManageCameraBindings` 权限及绑定本次 OperationId、逻辑角色和操作类型的 Step-Up。
相机工艺请求保持九类强类型设置；界面分别呈现 Requested、Effective、声明量化差异和健康状态。
配置失败关闭设备，清空 Effective 并显示 Configuration Unknown；重试重新应用完整配置。
重启恢复持久绑定，设备须显式重新打开和配置；成功调试仍需后续 Recipe Activation 才能参与生产准入。

`CameraProviderExtensionRequirement` 记录精确的 Provider、扩展契约版本及配置内容哈希，
带该依赖的草稿明确标记为不可跨 Provider 移植。目前没有注册扩展处理器，调试入口拒绝未知扩展。
`Test-Ticket17.ps1` 包含真实登录与 Step-Up 的独立 WPF 消费流程、Virtual Camera 成功/失败配置、
重启读取和窗口截图；真实硬件、Provider 资格与站点生产验收不在该开发验证中。
验证映射见 [V1-17 记录](docs/verification/v1-17.md)。

## 受控单帧采集开发入口

`CameraAcquisitionService` 独占一台已配置、Armed 的 `IControlledCameraDevice`，
每次受理生成一个 Qualification 身份，完成至多一次采集。被拒绝的请求没有采集身份，
公开入口拒绝 Production；该组件不替代正式 Station Qualification Session 或生产 Trigger 受理。
成功结果的 `TakeFrame()` 领取唯一 lease；未领取时释放结果归还 lease，领取后由消费者负责释放。

软件触发等待 pending 确认及 Busy 门后发出；硬触发夹具先观察 Busy，再使用精确关联身份
调用 `VirtualCameraProvider.PulseHardwareTrigger`。截止时间从 Busy 起计，用完整规范帧的
单调时间裁决。超时形成 Timeout + Unknown，断线形成 Error + Unknown，不重拍、不运行算法。
实际设备调用尚未完成时保持 CleanupPending，迟到帧不会进入下一请求。

Virtual 脚本时间仍相对接受采集请求；受控测试在 pending 到 Busy 之间不推进虚拟时间。
相同截止时刻的帧观察先于超时裁决，测试无需等待适配器的 Busy 通知 continuation。
显式注册 `AddSharpInspectCameraAcquisition(factory)` 后，服务容器负责异步释放组件，
StationRuntime 按完整报警政策映射读取协议事实并写入签名报警历史。
政策要求及稳定验证 ID 见 [V1-18 记录](docs/verification/v1-18.md)。

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --camera-acquisition-check D:\SharpInspectEvidence\controlled-acquisition
```

此入口输出组件证据、真实关联身份及可重复比较的规范化重放。真实设备、PLC、算法执行、
Provider Qualification、生产流程和 Station Acceptance 均未在该开发入口执行。

## 有限相机恢复开发入口

`AddSharpInspectCameraRecovery(factory)` 注册独占原相机及其专用 Provider 的恢复服务，
默认每 5 秒尝试一次、每周期最多 20 次；部署宿主可显式设置间隔和次数。
每次仅按原稳定身份打开设备，并重新完整应用配置、回读验证及武装原采集模式。
旧设备、在途调用或帧 lease 尚未实际释放时，服务不会打开下一台设备。

次数耗尽后停止自动尝试。重新启动周期使用 `StartCameraRecoveryCycleCommand`，
要求相机管理权限、绑定到本次命令的 Step-Up 和先持久化的审计受理记录。
存储通过 `ProductionStoreOptions.CameraRecovery` 显式启用 schema 11；旧库需要受控迁移，
不会在启动时自动升级。服务保持 Qualification 范围，Ready 始终为 false；
恢复成功只更新相机源健康，报警确认、复位、待确认结果及生产武装保留各自的约束。

`tools/Test-Ticket19.ps1` 会构建独立 NuGet 消费宿主，执行虚拟设备断线、失败重试、
20 次耗尽、真实身份授权重启及独立进程只读复核，保存周期、尝试次数、审计和 lease 证据。
稳定验证 ID 见 [V1-19 记录](docs/verification/v1-19.md)。真实硬件、Station Acceptance、
生产流程和原生 SDK 崩溃隔离均不属于此开发入口的验证结果。

相机网络维护通过独立的 `ICameraNetworkConfigurator` 扩展和
`ICameraNetworkMaintenanceRuntime` 入口提供；普通 `VirtualCameraProvider` 不提供该能力。
宿主显式声明 `CameraSetupOptions.StationNetwork` 并通过 `ProductionStoreOptions.CameraNetwork`
启用 schema 12 审计。维护使用稳定设备身份，要求权限、Step-Up、停产条件和独占设备会话。
修改后重新发现并读回同一设备，仍需独立完成 Recipe Activation。
开发验证入口为 `tools/Test-Ticket20.ps1`，实际范围与证据见 [V1-20 记录](docs/verification/v1-20.md)。

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
当前系统记录者固定为非交互的 `SharpInspect.Runtime`；人员管理命令另记录 Runtime 实际验证的人员 ID。
本机 Stop 的系统记录不会借用界面上的当前人员身份。

数据库必须位于固定本地 NTFS，路径验证会拒绝共享、已知云同步根、可移动盘和 reparse 路径。
实际写连接校验 WAL、FULL、foreign keys。未启用完整性政策的 Schema Version 为 1；
新空库显式启用审计政策时为 2，同时启用本地身份时为 7；再启用开发结果归档时为 8。
显式启用 Recipe Draft 历史时使用 schema 9，同时仍可选择是否启用开发结果归档。
显式启用相机绑定与调试审计时使用 schema 10，可与 Draft 和开发结果归档共存。
旧库启用相机能力会拒绝 `CameraSetupGovernedMigrationRequired`；不会自动迁移。
启用归档时旧库统一只读 preflight 拒绝 `AlgorithmResultArchiveGovernedMigrationRequired`。
未启用归档的身份路径对 schema6/5/4/3 分别要求报警、恢复、授权、认证治理迁移；
schema1/2 及未知版本也不开放身份治理写入。迁移随专票提供，不自动补链或升级。
只读查询每页 1–200 条，以记录位置和固定上界分页。更新、删除历史事实不属于公开 API。
SQLite `FULL` 与本机测试不构成断电耐久性或审计防篡改资格。

## 审计完整性开发入口

`ProductionStoreOptions.AuditIntegrityPolicy` 显式绑定工位、版本、密钥位置、检查点频率和核验预算。
默认未启用；查询显示 NotConfigured。开发样例可对全新空数据库使用 `--audit-key <unique-name>`，
并通过 `--audit-key-directory <absolute-path>` 选择独立密钥目录。`AllowInitialKeyCreation` 仅用于
当前非生产开发 bootstrap；正式站点安装、身份授权、迁移与资格仍由后续工单提供。

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

更正、证据删除、密钥旋转和退休请求检查专用权限及 Step-Up；尚未交付实际治理能力时仍拒绝
为 `GovernedCapabilityUnavailable` 并保留拒绝事实。未配置本地身份的入口保持 `AuthorizationUnavailable`。
它们不能改写历史；后续获授权的更正与处置须通过追加事件交付。
本机、数据库和私钥同时失陷时，攻击者可能重写未锚定历史；纯本地历史回滚也不保证被检测。
哈希链是篡改可检测机制，不是不可篡改存储或合规认证。

## 本地身份开发入口

新空库可通过 `ProductionStoreOptions.LocalIdentity` 显式提供 `LocalIdentityOptions`，同时绑定相同
Station 的审计政策。配置必须包含完整离线 `PasswordBlocklist` 的 ID、版本、内容 hash 和值集合，
以及版本化的 `LocalPasswordPolicy` / `PasswordHashBaseline`。缺失配置不创建默认账号。
`ILocalAdministratorBootstrap.ProvisionBootstrapTokenAsync` 由宿主安装流程调用；Runtime 根据实际
Windows 管理员令牌、活动物理控制台会话和远程会话检测判定资格，不信任调用方的布尔声明。
Token 使用 256 位随机量，默认 15 分钟有效，绑定 Station 和安装密钥，仅能成功消费一次。
`CreateFirstAdministratorAsync` 必须在物理控制台提交个人用户名、显示名和完整密码。

密码按 NFC 后的 15–128 个 Unicode 码点检查，不 trim、改大小写、截断或附加成分规则；WPF
PasswordBox 允许粘贴。离线 blocklist 比较完整值，并拒绝站点、用户名等可预测派生值。
V1 本地库只接受内置的 PBKDF2-HMAC-SHA256 实现，记录包含算法、格式、参数版本、实际工作因子、
随机盐及输出。当前开发下限为 600000 次、16 字节盐、32 字节输出，参考
[OWASP 密码存储建议](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)。
提高目标工作因子后，旧记录只在成功登录时与成功审计一起原子升级，升级不会降低旧参数。
此开发基线尚未经过正式发行平台的性能资格评估。

`AuthenticationPolicy` 必须带显式的 `Id`、`Version` 和内容 hash。开发默认每个账号最多 10 次、每个工位最多
50 次连续失败，配置上下限均为 1–100；延迟从 1 秒指数增加，最高 15 分钟。未知用户名使用等效 dummy
PBKDF2 路径、固定未知账号桶和受保护的尝试标识；达到限制只禁用已知凭据，不删除其 Human Principal。
100 次是 [NIST SP 800-63B 的禁用上限](https://pages.nist.gov/800-63-4/sp800-63b.html#rate-limiting-throttling)，
不是本项目的推荐默认值；上述 10/50 与 1 秒、15 分钟是开发政策值。会话管理参考 NIST 的
[Session Management](https://pages.nist.gov/800-63-4/sp800-63b/session/) 章节。

管理员、Token 消费和恢复码验证器与安全身份事件共用 SQLite 单写事务。机密状态使用 DPAPI，
并由数据库外的受限审计密钥签名，绑定工位、安装、状态版本和审计位置。审计仅包含安全元数据，
不会写入密码、Token、恢复码、盐、密码派生结果、机密密文或其签名。Schema5 的链 hash 同时覆盖
事件类型、两类序号及规范载荷；有界核验仍只证明报告中的实际覆盖范围。

成功创建后返回 `OneTimeSecret` 恢复包，只能领取一次。界面不会自动显示：切页/隐私遮蔽清除可见
内容，已提交但尚未领取的包保留在当前进程供显式领取；显示后可复制并清除。权威库只保存恢复码的
单向验证器和消费/撤销状态。进程终止不会重新显示恢复码；恢复和受控重新发行属于后续工单。
`IIdentityProvider` 返回稳定、不可变的个人身份及显示 claims，不授予配方、设备或生产权限。
具备管理员和有效恢复码只是后续准入前提，当前 Runtime 仍拒绝 Ready。

Runtime-owned `IInteractiveSessionService` 独立管理登录、锁定、注销、活动报告和会话读取：空闲判断使用
单调时间，成功登录生成新的 SessionId，锁定或注销清除当前身份和 SessionId，旧 SessionId 不能续用。
会话变化不会停止健康 Runtime、清除 Ready 或 PLC 状态，也不会把后续 System Principal 工作归因给旧人员。
锁定的 WPF 视图仍可重新登录，并保留只降低生产能力的本机 Stop；站点恢复执行仍由后续工单交付。

`LocalIdentityOptions` 同时要求显式 `AuthorizationPolicy`，包含 ID、版本、角色权限集合与规范内容 hash。
Operator / Technician / Administrator 只是分配权限的便捷集合，每个账号必须归属具体个人。
维护页可创建个人账号、禁用凭据、由其他获授权人员解锁或重绑，以及调整具体权限。
创建带角色的账号需要 `ManageAccounts + ManagePermissions`；禁用需要 `ManageAccounts`，解锁、重绑和权限调整
分别使用 `UnlockCredential`、`RebindCredential`、`ManagePermissions`，上述管理动作均强制 Step-Up。
常规管理不能删除最后一个可用管理员；全部凭据已被失败限额禁用时仍需后续 Recovery Kit 流程。

UI 消费只读 `IIdentityAdministrationQuery`，通过 `IStepUpAuthentication` 使用当前人的密码向已配置
`IIdentityProvider` 重新认证，再向 `IStationRuntime` 提交强类型命令。Grant 只在本进程保留，绑定人员、
SessionId、权限、具体命令类型、命令相关 ID、目标、凭据 ID、授权修订及政策 hash，使用单调时间判定新鲜度。
Runtime 在 SQLite 写事务内重新核对全部条件，持有短期会话授权锁直至提交；权限变更、锁定、换人、超期和
重复消费不能沿用旧凭证。账号状态、权限变更前后、实际人员、Step-Up 消费及命令 Outcome/Completed 在同一事务落盘。
当前实现最多 16 个个人账号、32 个未过期 Grant，并受 32 KiB 机密身份状态上限约束。
多账号使用独立失败状态；认证遍历全部已保存的工作参数组合，未知和禁用账号执行等量 dummy 验证。
非交互服务使用独立的 `SystemPermission` 与固定 System Principal 目录，不能登录或借用人的 Step-Up。

开发样例使用 `--identity-policy <absolute-json-path>` 加载显式政策，格式示例由
`tools/Test-Ticket06.ps1` 写入本次隔离 artifacts；该小型 blocklist 仅为测试夹具，不是发行名单。
验证另启动独立 WPF 进程，通过私有标准输入传递测试密码，实际走 PasswordBox 登录，并核对重启前后
PrincipalId 和审计链。密码不会作为进程参数、环境变量或报告内容写出。

## 本机管理员恢复

`ILocalAdministratorRecovery` 提供恢复状态、一次性恢复码重绑、Recovery Kit 轮换和保管确认。
恢复不会自动登录。恢复后的个人管理员须使用新密码登录，再为本次轮换重新认证；新包仅交付一次，
还需提交其中一枚码确认保管。确认码被消费，余下有效码才计入生产身份准入。页面关闭或锁定会清除秘密显示。

物理控制台、Runtime 排他状态、实际 Session 与密封身份状态均由服务重新校验。
Windows 管理员或 UI 提供的身份、状态不能代替恢复授权。现有 Runtime 尚无真实 PLC 安全停线证据，
即使本地 Stop 已完成也保持 `SafetyStopUnverified`；受控正向恢复场景仅属于内部开发测试。
真实产线恢复能力须等待后续停线/恢复核实实现及其验收。

## 报警政策、确认与复位

`ProductionStoreOptions.AlarmPolicy` 显式配置版本化报警政策。每个 Code 绑定受信 Source、严重度、独立的
生产影响、锁存与通知行为、Reset 前置以及 PLC 摘要映射。同一次发生保留 InstanceId，清除后复发使用新实例。
完整未清除集合出现在同一 Runtime 快照中；PLC 摘要只截取配置数量，同时报告总数、隐藏数和全部实例的生产影响。
未知 Code 或来源不匹配会留下持久边界事件并关闭报警准入，已有实例仍保留。

报警页通过当前个人会话提交强类型 `AcknowledgeAlarmCommand` 或 `ResetAlarmCommand`。ACK 只记录看见；
Reset 在写事务中检查同一实例的健康证据、Runtime Epoch、单调接收年龄和专属前置，默认还需精确绑定实例的
Step-Up。两者均不会 Arm、确认 PLC 结果或完成站点恢复。权限、实际人员、报警转换与命令事实在同一签名事务落盘。
`IAlarmHistoryQuery` 提供固定上界分页；UI 的选中项和筛选不会改变完整实例集或生产影响。

当前实际来源是 Runtime 自身的启动恢复状态，样例通过 `--alarm-policy <absolute-json-path>` 显式加载政策。
设备健康正向测试使用内部夹具；真实 Provider、活跃周期中断和 PLC 写入留在后续工单。报警观测独立限流，
排队观测让本机 Stop 先完成。新身份存储使用 schema 7，旧 schema 6 需要受治理迁移。

## 托管算法注册与准备

宿主显式注册自己的 `IVisionAlgorithmFactory`，再调用
`AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(maximumPreparationTimeout))`。
Factory 公开不可变的算法身份、配置 Schema、结果 Schema 和 Overlay Contract；准备请求精确绑定这些版本及内容 hash。
`AlgorithmConfigurationSnapshot.Create` 验证全部字段并生成版本化 canonical SHA-256，保留 Int64 精度和类型，
拒绝未知键、重复键、缺必填值、错误单位、越界及隐式转换。authoring defaults 只供后续草稿工具使用。

`AlgorithmPreparationService.PrepareAsync` 依次执行结构验证、Factory 语义验证、Create 和 WarmUp，全部成功后
交付 `PreparedAlgorithm`。句柄不公开原始实例；配置、相关身份、借用帧和有界诊断组成最小执行上下文。
模型、许可证和计算资源由消费方 Factory 在准备阶段加载；两个演示算法只做托管计算。
准备成功不激活 Recipe，也不会改变站点 Ready 或启动恢复要求。

准备期限包含等待和实际准备工作，与后续 Recipe 的执行期限分别配置。相同 Factory 串行调用，
超时或取消后仍运行的创建、预热、取消回调和释放过程继续占用实际工作槽，直到真正退出。
实例只能被一个句柄拥有；正在被拥有或已释放的对象均不能再次被 Factory 交付。异步释放有幂等入口，
关闭等待到期只限制宿主等待，不会宣称仍在运行的工作已退出。

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --algorithm-prepare-check
```

该入口覆盖两套不同 Factory、错误 Schema/hash、缺必填值、语义失败和依赖准备失败。
单帧计算、版本化执行政策与 Hung 恢复见下文；Overlay 开发历史可显式启用。

## 单帧计算与完整结果校验

`AddSharpInspectAlgorithmExecution(options)` 显式注册计算引擎。`ExecuteAsync(prepared, frameLease, request, runtimeCancellationToken)`
消费帧令牌；成功、拒绝、取消和异常都不会把原令牌留给调用方再次释放新 owner。
同一引擎只持有一个执行占位，同一 prepared 跨引擎也不能并发执行；Busy 请求直接拒绝。
正常帧复用已准备实例，完成清理后才返回正常结果，不重新创建或预热模型。

Runtime 在精确测量 Schema 与 Overlay Contract 全部校验通过后赋予 Success。
正常 Pass、Fail、带允许原因码的 Unknown 都可以是 Success；任何返回违约整体成为
`Error + Unknown + AlgorithmResultContractViolation`，`ValidatedResult=null`，不保留部分测量或图形。
允许的类型化 `AlgorithmExecutionException` 原因码经绑定 Schema 核对后保留；其他异常只返回 `AlgorithmExecutionError`。
原始异常不进入结果、日志或 UI；本阶段不保存异常详情，也不提供受保护诊断存储的旁路。

结果携带不可变配置指纹、精确结果 Schema、Frame Metadata 和相关 ID。Overlay 保留 Frame Pixel 坐标及 painter 顺序；
有限的越界几何不改写，渲染裁剪仅影响显示。填充只能用于支持填充的封闭图元，文本采用保守的纯标签安全规则。

每次调用通过 `AlgorithmExecutionRequest` 显式给出 Recipe 身份、版本、hash 和有限期限，并在完整校验后再次检查单调时间。
`AlgorithmExecutionOptions` 必须提供版本化 `AlgorithmExecutionPolicy`，包含部署最小/最大时限与取消宽限期，没有隐式生产数值。
结果绑定不可变策略 hash、Recipe、有效期限、宽限期和单调起点；新策略只能影响后续新接纳的请求。
Runtime 中止令牌用于取消计算，不能用 UI 放弃等待令牌代替。
超时或取消固定非成功终态，迟到数据不能覆盖；帧和实例仍保留到真实执行及其取消回调退出后才安全归还/退休。
宽限期结束仍未退出会永久锁存进程级 `AlgorithmExecutionGuard.CurrentProcess`：Runtime 保持 Ready=false，
所有准备和新执行拒绝 `AlgorithmHung`。迟到退出能归还资源，但重建服务、ACK 或 Reset 都不能清除该锁存。
恢复需要受控重启宿主，再经过普通启动恢复与生产武装门禁；库不强杀线程、不强制 Dispose 算法，也不把 token 取消当作执行已停止。
故意挂起的开发验证在独立测试子进程运行，父测试只终止自己创建的子进程；这不提供进程外算法宿主。
诊断政策尚未配置时，独立的每次执行 sink 丢弃全部事件，终态后封闭；不对任意对象做格式化或扇出。

这是 Manual/Qualification 类型的计算开发入口，不提交权威 Inspection Record，也不签发资格。
Production 类型被明确拒绝。版本化时限、Cancellation Grace 与进程 Hung 边界已由 T13 交付；当前 Ready 仍为 false。

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --algorithm-execution-check
```

## 通用配方草稿编辑

为新空库显式配置 `ProductionStoreOptions.RecipeDrafts = new RecipeDraftStoreOptions(executionPolicy)`，
并提供本地身份与审计政策；授权策略必须显式授予 `EditRecipeDraft`，并使用自己的 Id/Version。
原有 `AuthorizationPolicy.Development` 保留旧角色集合和 hash，不自动添加草稿权限。
`IRecipeDraftEditor` 只提供算法目录、作者默认值、校验、保存和有界历史；
独立 `IRecipeDraftHistoryQuery` 不创建写连接，也不需要算法 Factory。
旧库不自动升级，保存只追加草稿修订，不修改活动配方、设备配置或 Ready。

配方页面按 Schema 显示 Boolean、Int64、Float64、String、Enum，包括可选值、单位、界限和帮助文本。
未声明候选集合的 Enum 仍可编辑；可选 false、空字符串与缺失分别保存。
默认值只在显式新建或选择作者默认值时物化，并保留与显式编辑不同的来源。
保存及重开使用完整配置和精确原 Schema，不借用当前默认值修复缺值。

草稿包含一个原子算法、九类便携请求相机设置、执行超时，以及类型化资产和政策要求。
设备地址、凭据和存储路径没有草稿字段。保存要求真实人员会话与独立 `EditRecipeDraft` Permission；
存储再次核验权限、完整配置、规范 hash、修订前序与操作身份，冲突不会覆盖已有历史。
语义验证调用注册 Factory 的验证方法，与准备流程共用 Factory 串行入口；不会创建或运行算法。

`RecipeDraftEditorViewModel` / `RecipeDraftEditorPanel` 已接入配方页。开发宿主可用 `--recipe-drafts`
选择草稿能力，并显式提供审计和身份参数；测试入口由 `Test-Ticket15.ps1` 建立隔离账号夹具，
验证真实控件编辑、追加两条修订、锁定后拒绝，以及另一进程按原 Schema 只读重开。
依赖满足、发布、激活和生产资格仍未提供；有效草稿不构成生产许可。

## 开发结果归档与 Frame Pixel 查看器

新空库显式设置 `ProductionStoreOptions.AlgorithmResultArchive = new AlgorithmResultArchiveOptions()`，
同时配置本地身份与审计政策，启用 schema 8 开发归档。现有库须经后续受治理迁移，不自动升级。
`AlgorithmResultArchive.AppendAsync(recordId, outcome)` 仅保存完整验证成功的开发计算；
原结果 schema、Overlay contract、FrameMetadata、painter 顺序和执行时限证据共同进入规范载荷与签名绑定。
同 ID 同内容幂等，冲突或容量超限整体拒绝。缺失结果与有效空 Overlay 不混淆。

`IAlgorithmResultQuery` 提供固定上界的有界只读分页，读取时验证原合同和签名绑定。
`AlgorithmResultHistoryViewModel` / `AlgorithmResultPanel` 已接入追溯页，未配置时明确显示不可用。
`FrameOverlayPresenter` 按像素中心坐标、顺时针角度与 painter 顺序显示十类闭集图元，
缩放、平移、DPI 及裁剪不会回写几何。Text 使用自身 AnchorKind，Marker 使用自身 Size。
无法安全绘制时整幅显示不可用，原图未留存时仍可读取结构化标注。

`FramePreviewImage.CopyFromFrame` 复制冻结的未标注开发预览，绑定原帧元数据与像素摘要；
`RenderPreview()` 产生独立的 `RenderedOverlayPreview`，保留源、结果与渲染身份。
该预览摘要不替代正式图像 EvidenceHash；派生预览不能传入原图入口。
默认上限为单记录 1 MiB、单页 4 MiB、总归档 256 MiB 和 10000 条，可显式配置更低容量。
这些限制与共享审计核验预算共同生效；归档另保留 64 条控制事件空间，所有追加在超过预算前拒绝并回滚。

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --overlay-check <new-local-directory>
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --overlay-query <same-directory>
```

两个入口分别执行隔离开发归档与独立进程重启查询。正常宿主可使用 `--algorithm-result-archive`
并配合 `--identity-policy` / `--audit-key` 开关启用相同能力。此开发归档不构成正式 Inspection Record 或生产资格。

## 帧池与 OpenCvSharp 借用

`FrameBufferPool` 在创建时分配固定容量的 pinned 像素缓冲。`TryCopyFrame` 只接受规范 Mono8、
小端右对齐 Mono16（10/12/16 Valid Bits）或交错 Bgr24，验证正 Stride 和完整缓冲覆盖，
复制有效像素并清零 padding。耗尽返回 `FrameBufferExhausted` 与 `Error + Unknown`，不临时分配替代像素缓冲。
源只需覆盖末行有效像素；Mono16 的奇数源 Stride 在池中归一为下一偶数，输出元数据报告真实新 Stride。
池槽必须覆盖输出 `FrameMetadata.FullBufferLayoutLength`（包括末行 padding）；源 Stride=5 的 2×2 Mono16
需要 9 字节源和 12 字节槽。结构化 `PoolCopyEvidence` 保留源/输出步长、预分配复制和 padding 清零证据。
生产帧超过槽容量也返回 `FrameBufferExhausted` 并锁存故障，即使当前槽空闲。
这些是采集失败数据；权威逐件执行记录仍由后续执行器和生产流程建立。

框架/适配器持有 `FrameBufferLease`，算法只得到 `VisionFrame`。算法侧元数据包含公共有效相机设置；
Provider、SDK、物理设备、原生格式、规范化过程、设备计数和 UTC/单调里程碑保存在单独的 `FrameProvenance`。
时间和设备计数不代替类型化相关 ID。保留数据须复制，不能把 borrowed span 留到调用结束后。

`frame.WithMat(mat => ...)` 使用真实 row step，在同步回调结束时释放 Mat header，再释放原生读取占位。
`CloneToMat()` 产生独立副本，`CloneToDisplayMat()` 显式按 Valid Bits 缩放 Mono16 显示值。
owner 归还或池关闭与读取并发时，仍在读取的缓冲继续保留，直到真实回调退出才可复用。
Mat 本身有可写 API；只读、不保留及不派生长寿命 header 是消费方必须遵守并接受审查的契约，不能当作安全沙箱。

`FrameCallbackHandoff` 使用有界队列且不在发布栈执行消费者 continuation。`TryPublish` 无论成功或失败
均消费原 owner 令牌：失败会释放，成功后由读取者的新令牌持有；关闭会释放未领取项。
像素池及移交队列都不调用 Runtime、算法、UI 或持久化回调。
显式注册 `AddSharpInspectFrameBufferPool(options)` 后，Runtime 同步观察耗尽锁存并保持 Ready=false，
后台再通过受信报警来源持久化版本化 `FrameBufferExhausted`。归还缓冲不自动清锁存；受控恢复和 Arm 仍需后续工单。

```powershell
dotnet run --project samples/SharpInspect.SampleHost -c Release -- --frame-consumer-check
```

桥包仅引用 OpenCvSharp4 托管包，消费宿主另行选择本机 native runtime；本仓库 Windows x64 样例和测试
固定使用 `4.11.0.20250506`。四个框架开发包通过隔离缓存消费，不发布到 NuGet。

## 验证记录开发入口

`ConformanceDocuments` 冻结版本化 Profile、逐不变量双向映射、Release Candidate 与 Qualification Context。
产品文件和测试设施、数据、阈值、计算规则及实际运行环境分别绑定 SHA-256；空分组必须显式声明，
规范 JSON 包含 schema/canonicalization 版本。同一 Profile ID/版本不能替换内容。
候选目录的完整文件清单纳入指纹，新增未列出的文件也会失配；源版本由调用宿主声明，正式源历史核验仍未交付。

`ConformanceFacility` 是显式注册可信 `IConformanceScenario` 的开发执行设施，使用独立的本地 NTFS SQLite
账本及 DPAPI/ACL 保护签名根。它先持久化 Test Execution ID、输入、环境、预期和前序关系，再运行场景。
场景只返回观测；当前执行规则 `exact-text-v1` 按冻结预期作 ordinal 比较，裁决 Pass/Fail。
未支持的方法或环境保持 Blocked；场景普通异常不自动认定为 InvalidHarness。实际夹具或上下文指纹失配
才产生附带预期/实际 hash 证据的 InvalidHarness。所有 Outcome 来自封闭的六值集合。

输入和原始输出采用显式 `PublicTestData` 封套，每项最多 32 KiB，拒绝敏感分类；不自动捕获环境变量、
进程输出或凭据。原始证据和终态同事务提交，唯一 ID 与只追加接口防止覆盖。外置签名 head 核验完整链，
可检测单独修改或回滚数据库；提交后 head 写入失败保留记录并阻止继续聚合。整套目录及密钥同时回滚的
外部不可回滚保护尚未交付，不声称 WORM 存储。当前账本最多 10000 条、128 MiB 载荷。

`ConformanceQuery` 只读核验、分页查询执行谱系及原始证据。聚合会保留同一候选的所有历史 Product Fail，
更换测试上下文或后续跑绿不能清除该失败；产品修复必须形成新的候选指纹。未完成的预约显示 NotRun，
取消后的迟到观测不能覆盖已保存的 Blocked。执行设施保留实际占用槽，未退出的场景不能被第二个场景替换。

SampleHost 提供 `--conformance-demo <absolute-directory> --conformance-source <source-revision>`，以及另一进程的
`--conformance-query <same-directory>`。`tools/Test-Ticket07.ps1` 从本次三个 NuGet 开发包构建独立消费者后执行
这两个入口。演示明确记录受控的 Pass、Product Fail 和后续尝试，证明失败聚合拒绝，不签发资格。
所有本票记录固定为 `DevelopmentOnly`；摘要的 `CanIssueQualification` 始终为 false。可信场景仍在进程内运行，
此契约不构成沙箱或正式检查授权；完整 Profile、正式测试设施和各层资格留在后续工单。

## 验证边界

逐项证据见 [V1-01](docs/verification/v1-01.md)、[V1-02](docs/verification/v1-02.md)、
[V1-03](docs/verification/v1-03.md)、[V1-04 验证映射](docs/verification/v1-04.md) 与
[V1-05 认证节流与会话验证映射](docs/verification/v1-05.md)、[V1-06 权限与 Step-Up 验证映射](docs/verification/v1-06.md)、
[V1-07 不可覆盖验证记录映射](docs/verification/v1-07.md)、
[V1-08 本机管理员恢复验证映射](docs/verification/v1-08.md)、
[V1-09 报警政策与生命周期验证映射](docs/verification/v1-09.md)、
[V1-10 算法契约与准备验证映射](docs/verification/v1-10.md)、
[V1-11 帧池与 OpenCvSharp 借用验证映射](docs/verification/v1-11.md)、
[V1-12 单帧执行与完整结果校验映射](docs/verification/v1-12.md)、
[V1-13 时限、取消与 Hung 恢复映射](docs/verification/v1-13.md)、
[V1-14 Frame Pixel 归档与查看器映射](docs/verification/v1-14.md)、
[V1-15 通用草稿编辑与历史映射](docs/verification/v1-15.md)、
[V1-16 Virtual Camera 映射](docs/verification/v1-16.md)、
[V1-17 相机绑定与配置映射](docs/verification/v1-17.md)、
[V1-18 受控采集映射](docs/verification/v1-18.md)。
本机 Windows 11 Pro 的测试不构成 ADR-0004 中 Windows 10 22H2 三个版本的正式矩阵，
也不构成 Framework / Provider Qualification 或 Station Production Acceptance。
完整发行兼容矩阵、真实设备与现场验收保留在各自工单。
