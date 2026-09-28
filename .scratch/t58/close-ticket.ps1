$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 58 -or $taskPublication.issue -ne 59 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedDevelopmentFailures' -or $taskEvidence.solutionCases -ne 3279 -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T58 publication required before closure.'
}
$taskComment=@"
T58 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 与服务端 SHA 一致，分歧 0/0。

新增独立权限、一次性 Step-Up 和先行签名审计的受限诊断捕获。组件、等级、单调时限和事件尝试数绑定命令；锁屏、撤权、取消、故障及重启恢复基线，实际 I/O 退出前保持 Busy。Ready/Armed 等未获精确性能资格的负载拒绝准入，原有 producer 和 sink 上限不变。

默认支持包使用显式 UTC/身份范围，只重新分类并脱敏 safe 历史，附有限覆盖清单、成员与整体哈希。私有根目录、文件身份、容量和原始签名 seal 控制保留清理；交付能力受会话和停止约束。排除图片、配方、数据库、原始工件码与秘密，不提供未脱敏 protected 导出。schema 39→40 保留旧历史、停止引用和中断封存事实；旧 reader 拒绝40。

验证与原始失败记录见 [稳定 ID、实际范围及证据](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-58.md)。最终候选原始全量测试 $($taskEvidence.solutionCases)/$($taskEvidence.solutionCases) 通过（含 T58 $($taskEvidence.v158Cases) 个），失败 0、跳过 0；既有消费者测试 $($taskEvidence.consumerCases) 项、T58 包消费者 19 项检查、8 个本地包、5 个 fatal 进程模式、未重编译旧二进制 ABI、13 条迁移路径共 182 阶段通过。早期开发失败、独立复核和主动中断记录均保留，最终结果来自新候选的完整运行，未替换原始失败记录。证据集合标记 PassWithRecordedDevelopmentFailures。最终证据 SHA256：$($taskPublication.evidenceSha256)。

真实 PLC/相机/工位、现场性能资格、真实桌面锁屏交互、人工 WPF 视觉验收、生产部署、正式发行和公开 NuGet 均为 NotRun。本地夹具不授予生产资格，R06 明确为门禁白盒验证。
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
    $taskIssue=(& $taskGh issue view 59 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-58\]' -or $taskIssue.state -cne 'OPEN') {throw 'Unexpected issue state.'}
    $taskInitial=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'issue.json') -Raw | ConvertFrom-Json
    if($taskIssue.body -cne $taskInitial.body -or [regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count -ne 7) {
        throw 'Acceptance body changed; review current requirements before closing.'
    }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 59 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) {throw 'Checklist update failed.'}
    & $taskGh issue comment 59 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) {throw 'Completion comment failed.'}
    & $taskGh issue close 59 --repo HoboCY/SharpInspect.NET
    if($LASTEXITCODE -ne 0) {throw 'Issue closure failed.'}
    $taskLabels=@($taskIssue.labels.name | Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'})
    if($taskLabels.Count) {
        & $taskGh issue edit 59 --repo HoboCY/SharpInspect.NET --remove-label ($taskLabels -join ',')
        if($LASTEXITCODE -ne 0) {throw 'Closed ticket label synchronization failed.'}
    }
    $taskAfter=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) {throw 'Closure readback failed.'}
    $taskTickets=@($taskAfter | Where-Object title -Match '^\[V1-\d{2}\]')
    if(($taskTickets | Where-Object number -eq 59).state -cne 'CLOSED' -or
        @($taskTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object {$_.labels.name} |
            Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'}).Count) {throw 'Closure or label readback differs.'}
    $taskClosed=(& $taskGh issue view 59 --repo HoboCY/SharpInspect.NET --json number,state,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskClosed.state -cne 'CLOSED' -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[x\]').Count -ne 7 -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[ \]').Count -ne 0) {throw 'Closed acceptance checklist readback differs.'}
    $taskClosed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-after-close.json') -Encoding utf8
    [ordered]@{ticket=58;issue=59;state='CLOSED';head=$taskPublication.head;removedLabels=$taskLabels;
        closed=@($taskTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -ceq 'OPEN').Count;total=$taskTickets.Count} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}





