$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket42-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 42 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T42 publication required.'
}
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json)
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
if ($taskSummary.Count -ne 7 -or $taskTotal -ne 2115 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$taskValidation = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'validation.json') -Raw | ConvertFrom-Json
$taskAbi = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'production-inspection-consumer-evidence.json') -Raw | ConvertFrom-Json
$taskCases = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'v142-case-results.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskAbi.result -cne 'Pass' -or
    $taskConsumer.Result -cne 'Pass' -or $taskCases.result -cne 'Pass') {
    throw 'Full gate, ABI, cases and new consumer must pass.'
}
$taskBody = @"
T42 已实现并提交推送：生产 Trigger 经实际准入、来源与周期去重，在采图前固定 Inspection ID 和 Admission；生产入口复用 T40/T41 的唯一共享运行及握手状态机。

- 生产 Core 固定全部激活、配方、算法、Schema、Overlay、相机、标定、成像、PLC 报文身份与哈希，保留结构化结果、实际时钟和固定 payload。Core 和适用保留义务在同一耐久事务提交，发布使用 writer 回读的真实回执。
- 每次事务完整验证中央签名审计链、事件绑定、Admission/Core 全列投影和合法状态；接受前为每个周期预留最长故障/恢复尾部容量，普通写入不能消耗这些保留。
- Core 提交失败或超时撤 Ready、清 Busy，不发送普通结果；实际相机和算法资源退休前也不能发布。Stop、发送前撤销、不确定 I/O 和重复 Trigger 均有运行时边界验证。
- 冷启动将 Core-only、Core 后故障及未完成 ACK 的周期保留为恢复阻塞，零新增 PLC/相机 I/O，不重算、不补发旧周期。T44 实物处置恢复尚未提供。
- 当前明确限定 Part Identity=None、Evidence=None、零路由。未实现的 Required/Optional 或必需政策保持拒绝。隔离测试资格证据绑定实际目标，不能替换 Runtime 门禁。

提交：$($taskPublication.head)。HEAD、origin/main 与服务器 SHA 一致，分歧 0/0。

开发组合门禁：$taskTotal/$taskTotal，7 个测试程序集，零失败、零跳过；52 个 V142 程序集实例（包含 3 项激活竞争用例）及 3 个部署证据回归通过。独立 NuGet 消费者 V142_N01 验证公开合同、只读历史拒绝边界和无配置 Runtime 不就绪，候选包和实际加载 DLL 的 SHA-256 均一致。旧 T32 消费者未重编译并通过 ABI 验证，实际加载 DLL 与本轮 NuGet 包逐字节绑定。源码零漂移，原始 TRX、历史失败和产物哈希归档，独立审查无未解决 P1/P2。

验证说明：docs/verification/v1-42.md；证据目录 $($taskPublication.validationRoot)。早期失败与调查细节保留在验证说明和 prior-run-evidence，不能将修正前运行计为通过。

真实 PLC、真实相机、生产网络、产线 Ready/Arm、正式工位资格、实际性能/存储耗尽、掉电实验和实物处置恢复均为 NOT_RUN。开发夹具不替代这些验证；独立消费者未执行生产检测。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket42-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 43 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T42 completion comment failed.' }
    & $taskGh issue close 43 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T42 issue close failed.' }
    & $taskGh issue view 43 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T42 close verification failed.' }
}
