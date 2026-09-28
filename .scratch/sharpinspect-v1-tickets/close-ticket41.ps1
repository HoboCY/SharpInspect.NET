$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket41-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 41 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T41 publication required.'
}
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json)
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
if ($taskSummary.Count -ne 7 -or $taskTotal -le 2013 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$taskValidation = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'validation.json') -Raw | ConvertFrom-Json
$taskAbi = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'plc-communication-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskAbi.result -cne 'Pass' -or $taskConsumer.Result -cne 'Pass') {
    throw 'Full gate, ABI and new consumer must pass.'
}
$taskBody = @"
T41 已实现并提交推送：显式通信政策、双向 PLC 心跳、ControllerEpoch 同步以及原端点有限恢复，复用 T40 唯一共享检测周期状态机。

- 版本化政策绑定所有周期、超时、陈旧、重连与同步预算；完整 RuntimeEpoch 与计数经原子 FC16 写入，PLC 必须回显实际观察到的新元组。TCP 已连接或读写成功不能单独构成健康。
- 双向心跳新鲜、当前非零 ControllerEpoch 与输入归零稳定窗共同决定健康。稳定窗口先记录观察事实，审计后再观察完整窗口；初始代次抖动不会伪称恢复故障。
- watchdog 与实际发送门同步撤销旧许可；退出、超时、代次变化及旧连接迟到响应不能发送迟到 Ready/Valid。只在原端点有限重连，旧 observer/运行/I/O 退休后才能重连，无自动 Arm 或续接旧周期。
- Core COMMIT 前固定 Cancelled/Unknown 与稳定原因；COMMIT 后保留原 Run、控制器 key 和 payload，重连后的旧 Ack 不能完成原交付。带未解决交付的冷启动维持隔离并保持 PLC 零 I/O。
- schema27 通信账本绑定固定 PlcAdapter、RuntimeEpoch、Profile/Policy/端点及原周期身份，保持旧 schema/hash 与构造入口；查询按高水位和恢复身份计算状态，UTC 回拨不改变单调排序，容量失败原子回滚。

提交：$($taskPublication.head)。HEAD、origin/main 与服务器 SHA 一致，分歧 0/0。

开发组合门禁：$taskTotal/$taskTotal，7 个测试程序集、零失败、零跳过。其中六套1833项复用此前通过结果，逐项验证源码/二进制不变；WPF在测试调度修复后重跑227项。8 个 NuGet 包及全部独立消费者通过，新消费者恢复包与本轮feed逐包SHA-256一致。未重编译的 T32 消费者通过 ABI 检查，实际 DLL 与新包逐字节绑定。最终源码零漂移、验证产物 SHA-256 归档，独立审查无未解决 P1/P2。

验证说明：docs/verification/v1-41.md；证据目录 $($taskPublication.validationRoot)。早期失败/中止证据保留。全量门禁发现的旧 V110_R08 测试时序已修正：先观察四个逻辑超时，再验证物理槽仍占用；16 项算法准备回归通过。WPF首次并发加载控件的BAML/静态初始化锁环已由线程栈确认，测试类串行后五轮227项全部通过；产品Dispatcher未改动。

真实 PLC、生产网络、实际工位时序标定、正式工位资格、生产 Arm/Ready、实物处置恢复、外部投递和突然断电验证均为 NOT_RUN。开发通过不构成部署或生产准入。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket41-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 42 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T41 completion comment failed.' }
    & $taskGh issue close 42 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T41 issue close failed.' }
    & $taskGh issue view 42 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T41 close verification failed.' }
}
