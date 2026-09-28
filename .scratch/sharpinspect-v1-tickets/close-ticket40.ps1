$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket40-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 40 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T40 publication required.'
}
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json)
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
if ($taskSummary.Count -ne 7 -or $taskTotal -le 1969 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$taskValidation = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'validation.json') -Raw | ConvertFrom-Json
$taskAbi = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'abi-package-binding.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskAbi.result -cne 'Pass') { throw 'Full gate and ABI must pass.' }
$taskBody = @"
T40 已实现并提交推送：Runtime 作为真实 Modbus TCP 客户端，通过显式回环隔离 Profile 完成单帧检测与 ResultValid/ResultAck 四阶段握手。

- 唯一共享周期协调器负责规范帧、实际算法、校验编码、持久提交屏障及发布；T37 设施入口复用该路径，后续生产入口不能复制主流水线。
- 采集前冻结 Qualification Run ID、ControllerEpoch/CycleSequence 与批准政策。忙、重复、持续高请求不排队、不重放，跨会话已见 key 不能重新接纳。
- schema26 将资格 Core 与周期事实原子写入并绑定审计；完整 payload 先写，ResultValid 最后升高。Error/Timeout 及算法自行取消映射 Error 后以合法 Unknown 完成握手；持久失败不伪造普通 Unknown。
- 实际 COMMIT 后迟到回执、事务失败、Ack 两阶段超时、响应丢失、退出和冷重启均有边界验证。已提交 payload 不撤回；未解决周期阻塞恢复，冷重启无补发、无重算、无控制器恢复或隔离释放。
- 周期行数、字节预算及中央审计容量保护完整收尾；通用审计写入耗尽后，已接纳运行仍可使用预留额度完成 AckReset。

提交：$($taskPublication.head)。HEAD、origin/main 与服务器 SHA 一致，分歧 0/0。

最终开发门禁：$taskTotal/$taskTotal，7 个测试程序集，零失败、零跳过；8 个 NuGet 包及全部独立消费者通过。未重编译的 T32 消费者通过旧构造入口 ABI 检查并与包内 DLL 逐字节绑定。源码零漂移、验证产物 SHA-256 归档；独立审查问题已修复并回归。

验证说明：docs/verification/v1-40.md；证据目录 $($taskPublication.validationRoot)。早期失败证据保留。独立 NuGet 消费者验证公共 API 的拒绝边界与冷只读查询；真实 TCP 正向握手由 Runtime 集成测试覆盖。物理 PLC/相机、正式工位资格、生产 Ready、外部投递和突然断电验证均为 NOT_RUN。开发通过不构成部署或生产准入。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket40-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 41 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T40 completion comment failed.' }
    & $taskGh issue close 41 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T40 issue close failed.' }
    & $taskGh issue view 41 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T40 close verification failed.' }
}
