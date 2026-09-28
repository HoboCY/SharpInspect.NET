param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-publication.json') -Raw | ConvertFrom-Json
$taskFinal = Get-Content -LiteralPath (Join-Path $Run 'ticket49-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskFinal.result -cne 'Pass' -or $taskPublication.ticket -ne 49 -or $taskPublication.issue -ne 50 -or
    $taskPublication.validationRoot -cne [IO.Path]::GetFullPath($Run) -or
    $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T49 publication is required before closing issue 50.' }
$taskBody = @"
已完成 T49，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main、远端 main 一致，分歧 0/0。

公开 Startup Maintenance 入口在独占数据库且生产禁用的会话中执行已知 Schema32→33 前向迁移。先验证实际源库配置、完整审计和全部已配置子系统的未结束工作，再做一致性 SQLite Backup API 备份及哈希/逻辑验证。外部持久日志记录阶段、源/目标程序和数据库代次；单事务保留旧事实与审计前缀，完成后冷重开核验参数与目标。未知版本、损坏或缺失证据、模糊恢复、未闭合工作均拒绝。完成仍要求正常启动对账，Ready 始终为 false。

验证：Release 完整构建及解决方案 $($taskFinal.solutionTestCount)/$($taskFinal.solutionTestCount) 通过，零失败、零跳过；包含本票 $($taskFinal.v149CaseCount) 个测试。8 个 NuGet 包及规定消费者通过，消费者测试有 $($taskFinal.consumerTrxCount) 份 TRX、$($taskFinal.consumerTestExecutions) 次通过执行。保留的真实 T47 包生成 Schema32；14 个公开阶段分别强制终止独立进程后冷恢复，旧二进制均拒绝迁移后的 Schema33 且数据库字节不变，新写入方可正常追加。未重新编译的旧 ABI 消费者通过，替换库与本次包内 DLL 哈希绑定。独立审查问题均已处理，最终证据清单位于 $Run/ticket49-final-evidence.json；早期开发失败记录保留。

[行为、稳定验证 ID 与实际适用范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-49.md)。本地证据根：$Run。

限制：当前仅支持明确的 32→33 路径，要求外部审计锚点的配置保持拒绝；不自动恢复旧代次。真实 PLC/相机、硬件安全回路、正式部署、真实断电、工位资格验收、长期容量测试为 NotRun。整套部署切换和完整恢复属于 T65/T66，不由本票的软件夹具推定生产合格。
"@
$taskBodyPath = Join-Path $PSScriptRoot 'ticket49-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskServer = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskServer.Count -ne 1 -or
        ($taskServer[0] -split '\s+')[0] -cne $taskPublication.head) { throw 'Remote main changed before issue closure.' }
    & $taskGh issue comment 50 --body-file $taskBodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T49 completion comment failed.' }
    & $taskGh issue close 50
    if ($LASTEXITCODE -ne 0) { throw 'T49 issue closure failed.' }
    $taskIssue = (& $taskGh issue view 50 --json number,state,title,url | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $taskIssue.state -cne 'CLOSED') { throw 'Issue 50 is not confirmed closed.' }
    $taskIssue | ConvertTo-Json
    $taskAll = @(& $taskGh issue list --state all --limit 100 --json number,title,state | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Post-closure count query failed.' }
    $taskTickets = @($taskAll | Where-Object title -Match '^\[V1-\d{2}\]')
    [ordered]@{total=$taskTickets.Count;closed=@($taskTickets | Where-Object state -CEQ 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -CEQ 'OPEN').Count} | ConvertTo-Json
}
