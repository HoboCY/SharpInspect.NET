param([Parameter(Mandatory)][int]$ExpectedCases)
$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket43-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 43 -or $taskPublication.head -cne
    (git -c core.autocrlf=true -c core.safecrlf=false rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T43 publication required.'
}
$taskRun=$taskPublication.validationRoot
$taskSummary=@(Get-Content -LiteralPath (Join-Path $taskRun 'test-summary.json') -Raw | ConvertFrom-Json)
$taskTotal=($taskSummary | Measure-Object -Property passed -Sum).Sum
if ($taskSummary.Count -ne 7 -or $taskTotal -lt (2115 + $ExpectedCases) -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count) {
    throw 'Complete passing suite evidence required.'
}
$taskValidation=Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
$taskAbi=Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer=Get-Content -LiteralPath (Join-Path $taskRun 'part-identity-consumer-evidence.json') -Raw | ConvertFrom-Json
$taskCases=Get-Content -LiteralPath (Join-Path $taskRun 'v143-case-results.json') -Raw | ConvertFrom-Json
$taskReview=Get-Content -LiteralPath (Join-Path $taskRun 'independent-review.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskAbi.result -cne 'Pass' -or
    $taskConsumer.Result -cne 'Pass' -or $taskCases.result -cne 'Pass' -or
    $taskCases.assemblyCaseCount -ne $ExpectedCases -or $taskReview.result -cne 'No unresolved P1/P2') {
    throw 'Full gate, ABI, cases, consumer and independent review must pass.'
}
$taskBody=@"
T43 已实现并提交推送：Required／Optional／None 工件身份在分配 Inspection ID 前完成来源、周期、新鲜度和格式校验，并把读取证据冻结到 Admission/Core。

- 公开 Provider 按逻辑角色唯一绑定，激活校验真实能力和程序集；最终提交核对来源 revision。None 不调用来源，Optional 真实 Missing 保存 NotProvided；不竞速、不回退、不等待人工补码。
- 扫码 stage token 按精确周期单次消费；PLC 复用现有通信 owner 执行稳定版本读取。接受事务拒绝重复来源令牌，允许不同周期/令牌使用同一业务码。Stop、超时、迟到结果和来源重启不能分配迟到 Inspection ID。
- schema29 保存独立、签名、可冷读的拒绝与更正历史，完整验证中央链、SQL 投影及原始 Core/命令/授权引用。历史更正通过真实会话、专门权限与 Step-Up，沿前序更正追加，原 Admission/Core 和 PLC payload 不变；冲突保留普通拒绝审计。
- 旧 None v1 及 schema 历史路径保持兼容，包括既有 GovernedAuditChangeCommand 的 CorrectHistoricalFact 审计。

提交：$($taskPublication.head)。HEAD、origin/main、服务器 SHA 一致，分歧 0/0。

完整开发组合门禁：$taskTotal/$taskTotal，7 个程序集零失败、零跳过；$ExpectedCases 个 V143 程序集实例、8 个 NuGet 包和完整独立消费者链通过。V143_N01 核对候选 nupkg、缓存、输出及实际加载程序集的 SHA256。未重编译的 T32 ABI 消费者保持原 SHA256 并与最终包逐字节绑定；源码验证期间零漂移，原始失败/修正结果、TRX、包和产物哈希已归档，独立审查无未解决 P1/P2。

验证说明：docs/verification/v1-43.md。证据目录：$taskRun。初始数据库转发、连续更正、备份及旧权限回归失败均原样保留。相机健康检查的退休交接与操作预算已补回归；五处 writer 使用同一份完整性快照；单次中央校验复用完整验证后的资格事件位置索引，消除重复全表解码。此前资格/标定审计失败保留诊断，不因后续通过而宣称已证明其根因。

真实 PLC、真实相机、现场扫码器、生产网络、产线 Ready/Arm、正式工位资格、实际性能/存储耗尽、掉电及实物处置恢复为 NOT_RUN。开发夹具和局部验证不替代正式工位资格；T44 人工恢复尚未交付。
"@
$taskBodyFile=Join-Path $PSScriptRoot 'ticket43-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 44 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) {throw 'T43 completion comment failed.'}
    & $taskGh issue close 44 --reason completed
    if ($LASTEXITCODE -ne 0) {throw 'T43 close failed.'}
    & $taskGh issue view 44 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) {throw 'T43 close verification failed.'}
}
