$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 57 -or $taskPublication.issue -ne 58 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedDevelopmentFailures' -or $taskEvidence.solutionCases -ne 3204 -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T57 publication required before closure.'
}
$taskComment=@"
T57 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 与服务端 SHA 一致，分歧 0/0。

提供显式冻结的 Performance Contract、12 个单调周期分段、42 类有界资源观测和可离线复算的版本化文档；p50/p95/p99、Observed Max、失败/Unknown、丢失/重复、帧和独立业务身份均保留。预算影响后续准入，已接受周期的 ExecutionStatus、Decision 和报文不会被性能告警改写。正常与超时关闭竞争、未退休生产者和物理采样槽保持保守终态。

实际固定虚拟周期文档通过仅引用本票 NuGet 的独立消费者回读，捕获程序集与包哈希一致。文档对象字段顺序显式冻结，跨 .NET 6/10 探针输出逐字相同。新增 Ready 发布窗口的确定性交错修复，保持真实 Stop、维护变化及显式撤销保护；剪贴板烟测分开写入与 Flush 重试，搜狗进程未被关闭。

验证与原始失败记录见 [稳定 ID、实际范围及证据](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-57.md)。完整覆盖 $($taskEvidence.solutionCases) 个用例（含 T57 $($taskEvidence.v157Cases) 个），既有消费者测试 $($taskEvidence.consumerCases) 项及新增性能消费者、8 个本地包、5 个 fatal 进程模式、旧二进制 ABI、12 条迁移路径共 168 阶段通过。结果为 PassWithRecordedDevelopmentFailures，原失败与中止没有覆盖为成功。最终证据 SHA256：$($taskPublication.evidenceSha256)。

真实 PLC/相机/工位、精确原生分配器、现场压力/长稳、完整六场景正式 Profile、正式发行及公开 NuGet 发布均为 NotRun；两周期 Framework Baseline 和冷恢复片段不授予生产资格，也不保证未来硬实时最大值。
"@
$taskCommentPath=Join-Path $PSScriptRoot 'close-comment.md'
$taskComment | Set-Content -LiteralPath $taskCommentPath -Encoding utf8
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskRemote=@(git -c safe.directory=D:/projects/SharpInspect.NET ls-remote origin refs/heads/main)
    if($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1 -or ($taskRemote[0] -split '\s+')[0] -cne $taskPublication.head) {
        throw 'Remote changed before closure.'
    }
    $taskIssue=(& $taskGh issue view 58 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-57\]' -or $taskIssue.state -cne 'OPEN') {throw 'Unexpected issue state.'}
    $taskInitial=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'issue.json') -Raw | ConvertFrom-Json
    if($taskIssue.body -cne $taskInitial.body -or [regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count -ne 6) {
        throw 'Acceptance body changed; review current requirements before closing.'
    }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 58 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) {throw 'Checklist update failed.'}
    & $taskGh issue comment 58 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) {throw 'Completion comment failed.'}
    & $taskGh issue close 58 --repo HoboCY/SharpInspect.NET
    if($LASTEXITCODE -ne 0) {throw 'Issue closure failed.'}
    $taskLabels=@($taskIssue.labels.name | Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'})
    if($taskLabels.Count) {
        & $taskGh issue edit 58 --repo HoboCY/SharpInspect.NET --remove-label ($taskLabels -join ',')
        if($LASTEXITCODE -ne 0) {throw 'Closed ticket label synchronization failed.'}
    }
    $taskAfter=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) {throw 'Closure readback failed.'}
    $taskTickets=@($taskAfter | Where-Object title -Match '^\[V1-\d{2}\]')
    if(($taskTickets | Where-Object number -eq 58).state -cne 'CLOSED' -or
        @($taskTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object {$_.labels.name} |
            Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'}).Count) {throw 'Closure or label readback differs.'}
    $taskClosed=(& $taskGh issue view 58 --repo HoboCY/SharpInspect.NET --json number,state,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskClosed.state -cne 'CLOSED' -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[x\]').Count -ne 6 -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[ \]').Count -ne 0) {throw 'Closed acceptance checklist readback differs.'}
    $taskClosed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-after-close.json') -Encoding utf8
    [ordered]@{ticket=57;issue=58;state='CLOSED';head=$taskPublication.head;removedLabels=$taskLabels;
        closed=@($taskTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -ceq 'OPEN').Count;total=$taskTickets.Count} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}





