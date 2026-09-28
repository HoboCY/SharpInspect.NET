param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-publication.json') -Raw | ConvertFrom-Json
$taskFinal = Get-Content -LiteralPath (Join-Path $Run 'ticket50-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskFinal.result -cne 'Pass' -or $taskPublication.ticket -ne 50 -or $taskPublication.issue -ne 51 -or
    $taskPublication.validationRoot -cne [IO.Path]::GetFullPath($Run) -or
    $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T50 publication is required before closing issue 51.' }
$taskBody = @"
已完成 T50，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main、远端 main 一致，分歧 0/0。

每次 AcceptedTrigger 冻结确切 All / FailOrUnknown / None 留图政策；无帧记录 NotAvailable 与实际采集原因。规范像素直接来自算法实际输入，以版本化信封定义 Mono8、Mono16 小端右对齐 10/12/16 位和 Bgr24，哈希排除 padding、显示、PNG 参数及来源元数据。必须留图先写唯一临时文件、刷盘、全量读回校验并原子改名；内部单次凭证与持续持有的文件句柄保护 Core / Pending Manifest / FinalizePng 工作项同一 SQLite 事务提交。各文件/事务边界故障和独立 StageTimeout 均终止周期且不发布普通结果；物理 I/O 退出前保留帧引用，不能用迟到 Lease 补写 Core。达到积压上限阻止下一准入，已准入周期仍可在 Pending 状态发布。冷启动发现孤儿暂存或已有 Pending 保持阻断。

可选 schema 34 经治理的 33→34 迁移启用；32→33 路径保留，已完成旧日志作为哈希链接前缀保留。迁移冻结图像配置，永久标记采用刷盘临时文件后原子替换。历史身份/审计、Recipe 和 Core 编码保留旧哈希分支。

验证：Release 完整构建及解决方案 $($taskFinal.solutionTestCount)/$($taskFinal.solutionTestCount) 通过，零失败、零跳过，包含本票 $($taskFinal.v150CaseCount) 项测试；8 个 NuGet 包、规定消费者及未重新编译的旧 ABI 消费者通过。消费者测试共 $($taskFinal.consumerTrxCount) 份 TRX、$($taskFinal.consumerTestExecutions) 次通过执行。保存的真实 schema 32 / 33 包分别运行 32→33、33→34 的 14 阶段进程终止矩阵；阶段 1–13 验证未完成操作恢复，阶段 14 验证完成后重新打开的幂等性。33→34 还验证备份真实旧包来源及文件 SHA、实际旧写入方拒绝、主数据库与 WAL 字节/审计链头不变、当前包冷读空 Pending 工作。三份独立审查及主代理问题处理记录已归档；审查快照后的变更明确列出，开发失败和子集预检证据保留。

[行为、稳定验证 ID 与适用范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-50.md)。本地完整证据根：$Run；汇总：ticket50-final-evidence.json。

限制：真实相机/PLC、安全回路、正式部署、磁盘物理断电、长期容量与工位资格验收未执行。PNG 最终化、图像保留执行及启动对账属于后续票；本票 Pending 不等于最终 PNG 完成，软件刷盘/原子改名与进程终止不替代物理断电资格验证。
"@
$taskBodyPath = Join-Path $PSScriptRoot 'ticket50-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskServer = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskServer.Count -ne 1 -or
        ($taskServer[0] -split '\s+')[0] -cne $taskPublication.head) { throw 'Remote main changed before issue closure.' }
    & $taskGh issue comment 51 --body-file $taskBodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T50 completion comment failed.' }
    & $taskGh issue close 51
    if ($LASTEXITCODE -ne 0) { throw 'T50 issue closure failed.' }
    $taskIssue = (& $taskGh issue view 51 --json number,state,title,url | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $taskIssue.state -cne 'CLOSED') { throw 'Issue 51 is not confirmed closed.' }
    $taskIssue | ConvertTo-Json
    $taskAll = @(& $taskGh issue list --state all --limit 100 --json number,title,state | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Post-closure count query failed.' }
    $taskTickets = @($taskAll | Where-Object title -Match '^\[V1-\d{2}\]')
    [ordered]@{total=$taskTickets.Count;closed=@($taskTickets | Where-Object state -CEQ 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -CEQ 'OPEN').Count} | ConvertTo-Json
}
