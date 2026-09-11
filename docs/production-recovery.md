# 生产恢复消费者边界

SampleHost 只有在显式使用 `--production-recovery-ui` 时才挂载维护恢复界面。这个开关同时启用 `ProductionInspections` 和 schema30 的 `ProductionRecovery` 存储选项；它不会创建安全 provider，也不会把软件状态当作物理安全停线证据。

```powershell
dotnet SharpInspect.SampleHost.dll `
  --smoke `
  --production-recovery-ui `
  --trace-db .\station-schema30.sqlite `
  --identity-policy .\development-identity-policy.json `
  --audit-key SharpInspect.DevelopmentValidation.example `
  --audit-key-directory .\private-keys
```

恢复查询读取真实 `IProductionRecoveryHistoryQuery`，界面通过 `ShellWindow.AttachProductionRecovery` 挂载。待处理记录会显示原始 inspection、controller epoch/cycle、最后持久阶段、交付不确定性以及 Core/payload 合同哈希。执行恢复仍须通过 Runtime 权限、精确事件绑定、Step-Up 和独立的 `IProductionRecoverySafetyProvider`。

此开发消费者使用新库且未登录，因此 `CanRecover=false`，物理生产状态为 `NotRun`。在已登录且有待恢复记录的部署中，按钮可用于提交请求；缺少安全 provider 时 Runtime 仍拒绝物理恢复。应用不能通过勾选框、`Ready` 软件标志或默认实现绕过这个边界。

schema30 是显式 opt-in 的新库格式。旧 schema28/29 数据不会被本功能自动迁移或复制；启用恢复的进程必须创建一个新的 schema30 数据库，并提供对应的审计身份、密钥和配置。缺少 `ProductionRecovery` 配置时，schema30 读写应拒绝并报告配置缺失；旧 schema 数据需要经过受控的独立迁移流程后才能用于新部署。

`tools/Test-ProductionRecoveryConsumer.ps1` 会复制 SampleHost 消费者源文件并从 NuGet 包构建，不引用仓库项目。它在全新的输出路径创建 schema30 数据库，复用显式的 identity policy 和 audit key 输入，不会复制 legacy SQLite 文件；V144-N01 只验证公共 UI/存储边界和 fail-closed 行为，不宣称真实物理恢复已执行。
