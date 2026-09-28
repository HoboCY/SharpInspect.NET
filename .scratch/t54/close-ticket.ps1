$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 54 -or $taskPublication.issue -ne 55 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedFailures' -or $taskEvidence.solutionCases -ne 3035 -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T54 publication required before closure'
}
$taskComment=@"
T54 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 与服务端 main 一致，分歧 0/0。

显式 schema38 增加启动核对与有界历史巡检，支持原35/36/37精确迁移。启动前保持 Ready=false；有效 Pending 恢复，绑定 final 在 decode/hash 后确认 Succeeded。旧发送尝试先耐久记录 UnknownOutcome，写入失败不能放行启动；外部发送等待统一屏障。孤儿先记录意图、用受保护同一文件句柄隔离、再追加完成，不能补造生产引用。缺失、损坏、多链接及矛盾形成锁存故障，Core、历史与原报文字节保持不变。

历史按冻结上界分页，持久化 checkpoint 后才推进；取消／冷重启继续原 RunId。文件读／解码和完整验证在写协调器外，提交用同一读连接 data_version witness、审计尾和来源版本复核。图片与 Outbox 每轮交替，即使零提交让出也不阻塞另一来源。只读 WPF 页面提供查询、进度和隔离／故障事实，离页清除数据并拒绝迟到响应。

验证：完整解决方案3035/3035通过，0失败0跳过，其中T54新增55项；八个NuGet包、规定消费者、未重编译的旧ABI消费者均通过。11条迁移路径各14个公开阶段，去重154阶段；本票35/36/37→38各14/14，共42阶段。包内库、实际加载Runtime、源码、原表行、旧写入器拒绝及DB/WAL/审计不变均已关联。

最终证据为 PassWithRecordedFailures：原post-test在旧35→36的phase6 seed前置返回TraceAuditUnavailable，该原运行仍为Fail。旧包及探针与T53相同；冷读无命令事实，审计尾13为StartupRecoveryRequired/AlarmCodeUnmapped。内部Append失败原因仍Unproven，不归因当前T54，也不声称复核修复了原失败。六条已过路径和包／消费者按哈希复用；原35→36完整14阶段有界复核通过，随后补跑原未执行路径，未重复计算原前五阶段。

[实现、不变量、验证ID与实际范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-54.md)。组合清单 SHA256：$($taskPublication.evidenceSha256)。原失败、定向复核、完整解决方案及迁移续验分别保留。独立架构复核发现的两个边界缺陷已修复并有对应故障注入测试；最终限定复核无新的可复现P1/P2。

未执行真实PLC／相机／MES工位验收、硬件断电耐久资格、完整正式发行Profile、现场部署或公开NuGet发布。

"@
$taskCommentPath=Join-Path $PSScriptRoot 'close-comment.md'
$taskComment | Set-Content -LiteralPath $taskCommentPath -Encoding utf8
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskRemote=@(git -c safe.directory=D:/projects/SharpInspect.NET ls-remote origin refs/heads/main)
    if($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1 -or
        ($taskRemote[0] -split '\s+')[0] -cne $taskPublication.head) { throw 'Remote changed before closure.' }
    $taskIssue=(& $taskGh issue view 55 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-54\]' -or $taskIssue.state -cne 'OPEN') { throw 'Unexpected issue state.' }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskUnchecked=[regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count
    if($taskUnchecked -ne 7) { throw 'Acceptance checklist changed; review actual body before editing.' }
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 55 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) { throw 'Checklist update failed.' }
    & $taskGh issue comment 55 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) { throw 'Completion comment failed.' }
    & $taskGh issue close 55 --repo HoboCY/SharpInspect.NET
    if($LASTEXITCODE -ne 0) { throw 'Issue closure failed.' }
    $taskAll=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) { throw 'Tracker refresh failed.' }
    $taskTickets=@($taskAll | Where-Object title -Match '^\[V1-\d{2}\]')
    $taskLabelChanges=@()
    foreach($taskClosed in $taskTickets | Where-Object state -ceq 'CLOSED') {
        $taskStale=@($taskClosed.labels.name | Where-Object { $_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human' })
        if($taskStale.Count) {
            & $taskGh issue edit $taskClosed.number --repo HoboCY/SharpInspect.NET --remove-label ($taskStale -join ',')
            if($LASTEXITCODE -ne 0) { throw 'Closed-ticket label synchronization failed.' }
            $taskLabelChanges += [ordered]@{issue=$taskClosed.number;removed=$taskStale}
        }
    }
    $taskAfter=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) { throw 'Closure readback failed.' }
    $taskAfterTickets=@($taskAfter | Where-Object title -Match '^\[V1-\d{2}\]')
    if(($taskAfterTickets | Where-Object number -eq 55).state -cne 'CLOSED' -or
        @($taskAfterTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object { $_.labels.name } |
            Where-Object { $_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human' }).Count) { throw 'Closure or label readback differs.' }
    [ordered]@{ticket=54;issue=55;state='CLOSED';head=$taskPublication.head;labelChanges=$taskLabelChanges;
        closed=@($taskAfterTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskAfterTickets | Where-Object state -ceq 'OPEN').Count;total=$taskAfterTickets.Count} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}
