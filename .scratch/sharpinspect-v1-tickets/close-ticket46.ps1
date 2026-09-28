$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket46-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 46 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T46 commit and push required.' }
$taskRun = $taskPublication.validationRoot
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskValidation.distinctPassedCases -ne 2385 -or
    $taskValidation.consumerFixtureRepair.review -cne 'Pass') { throw 'Final validation and review required.' }
$taskBody = @"
T46 已实现并提交推送：默认 Local Operator Only；显式版本化 Selection Policy 与精确不可变 Selection Map；真实 PLC actor、专用系统能力与独立换方请求/应答/ACK/归零握手。

- 非零选择码绑定精确 RecipeReference、ReleaseId 和内容哈希，不使用名称/latest/通配符。Map 变更须 Ready=false、ManageRecipeSelectionMap、精确 Step-Up 及全部受影响 release 校验。
- 请求先耐久观察，激活准备前完成实际 Ready 清除；冲突立即固定为忙拒绝，不排队、不抢占、不自动重试。复用完整激活与旧配置恢复，成功后保持 Disarmed。稳定身份跨重启去重，所有专用协议转换留痕。
- SQLite 可选 Schema 31、事务内权限/版本/容量检查、中央审计链双向验证；保留旧人类激活身份与哈希。冷启动保留未确认应答，不能自动清理、伪造 ACK/reset 或重新激活。

提交：$($taskPublication.head)。HEAD、origin/main 和服务器 SHA 一致，分歧 0/0。

开发验证采用明确的组合证据：原始七程序集 2382 例中 2381 通过，旧 PLC 权限目录断言失败，原清单保持 Fail；修复后另跑 104/104，包含全部 95 个 T46 实例及授权/既有边界。按实际名称合并共 2385 个不同通过用例，未宣称单轮全量通过。8 个 NuGet 包、全部独立消费者、V146_N01 和未重编译的旧 ABI 消费者通过；原始及复验源码、二进制、TRX、包和审查证据均有哈希关联。

消费者续跑发现开发夹具给新库复用旧审计密钥，受控同二进制对照复现 AuditSigningKeyProvisioningConflict。仅修正消费者脚本的独立测试密钥作用域，未放宽产品策略、超时或断言；修复消费者及原失败二进制使用正确的新签名身份均通过。原消费者失败与早期诊断设置错误均单独保留。完成独立产品/增量及该脚本修复复核。

证据根目录：$taskRun。仓库说明：docs/verification/v1-46.md。

范围限制：真实 PLC/相机、设备断电、安全停线、生产性能和完整 Profile 资格均 NOT_RUN。退休仅验证内部精确否决夹具，真实生命周期属于 T48；自动布防属于 T47。尚无独立人工换方应答撤销命令，遗留应答保持 RecoveryRequired，不宣称恢复成功。正常 Dispose 恰逢 T46 初始同步及真实 Succeeded activation 引用的冷重启没有直接测试。
"@
$taskBodyPath = Join-Path $PSScriptRoot 'ticket46-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 47 --body-file $taskBodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T46 completion comment failed.' }
    & $taskGh issue close 47 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T46 close failed.' }
    & $taskGh issue view 47 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T46 close verification failed.' }
}
