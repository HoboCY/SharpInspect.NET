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
pwsh -File tools/Test-Ticket10.ps1
```

脚本把每次运行的日志与环境记录保存在独立的 `artifacts/ticket10/<run>/`，
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
当前系统记录者固定为非交互的 `SharpInspect.Runtime`；人员管理命令另记录 Runtime 实际验证的人员 ID。
本机 Stop 的系统记录不会借用界面上的当前人员身份。

数据库必须位于固定本地 NTFS，路径验证会拒绝共享、已知云同步根、可移动盘和 reparse 路径。
实际写连接校验 WAL、FULL、foreign keys。未启用完整性政策的 Schema Version 为 1；
新空库显式启用审计政策时为 2，同时启用本地身份时为 5；带本地身份的 Schema5 只允许从新空库创建。
已有 schema4 在启用本地身份时只读 preflight 拒绝 `IdentityAuthorizationGovernedMigrationRequired`，
schema3 拒绝 `IdentityAuthenticationGovernedMigrationRequired`；schema1/2
及未知版本也拒绝治理写入。迁移随专票提供，不自动补链或升级。
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
帧池、OpenCV 借用、单帧执行与整包结果校验、执行期超时隔离和 Overlay 持久化仍由后续工单交付。

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
[V1-10 算法契约与准备验证映射](docs/verification/v1-10.md)。
本机 Windows 11 Pro 的测试不构成 ADR-0004 中 Windows 10 22H2 三个版本的正式矩阵，
也不构成 Framework / Provider Qualification 或 Station Production Acceptance。
完整发行兼容矩阵、真实设备与现场验收保留在各自工单。
