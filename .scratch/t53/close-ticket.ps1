$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 53 -or $taskPublication.issue -ne 54 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedFailures' -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T53 publication required before closure.'
}
$taskComment=@"
T53 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 和服务端 main 一致，分歧 0/0。

Outbox 临时／未知失败保持原 Delivery ID 和精确字节有界重试；永久失败停止自动重试。Required 永久阻塞立即关闭新 Trigger，BestEffort 保持 ProductionImpact=None。人工恢复在同一权威事务中重查个人权限、会话、精确 Step-Up、原因、状态版本和完整容量预留，追加新尝试预算并保留原历史；更正交付以新 ID、显式新字节关联原项，不能改写 Core 或把失败直接标为送达。缺失确切历史处理器形成耐久 HandlerBlocked，重新安装处理器后仍需授权恢复。

显式 schema36→37 迁移支持旧32/33/34/35→36日志接续，保留旧行、rowid、NULL和哈希。恢复激活沿用原签名检查点间隔，更正仅在原路由及Payload Contract内换内容；新外部契约版本仍需独立部署治理。

验证采用保留原失败的组合证据：原解决方案运行2980项，2979通过、1项旧V145_S01/AckHigh在Stop提交前未取得Admitted。该测试及触发路径与4302f5c一致，夹具未配置Outbox；当次拒绝原因未证实。同一候选五个Stop阶段和两个确定性准入撤销测试7/7通过，不能据此宣称该间歇问题已修复。原Fail、全部TRX、源码及二进制身份依据均保留；去重后2980项均有通过证据，其中本票56项，7项重复执行不增加不同用例数。

独立post-test范围完成有界续验：8个NuGet包、25个消费者测试执行及规定公开消费者、旧ABI消费者（未重编译，4个实际加载库匹配包内SHA256）；8条迁移路径各14个公开阶段进程中断与恢复，去重共112阶段。原post-test调用在35→36的phase2建库时，旧包返回TraceAuditUnavailable；冷读无命令事实，尾第13条为启动恢复告警。该原Fail保留，内部写失败原因未证实。探针和产品未修改，35→36用原包完整14/14复核，36→37独立14/14完成；其余已过证据按哈希复用。旧写入器拒绝、库/WAL/审计不变与完整审计水位均已核对。后端/WPF独立审查、修复及开发失败记录保留。

[行为、稳定验证ID和实际范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-53.md)。组合清单：$($taskPublication.evidence)。原解决方案和post-test运行均保持Fail；局部复核与有界续验分别记录，不称为单次完整全过，也不声称已修复两次间歇失败。

未执行真实PLC/相机/MES工位验收、硬件断电资格、完整正式发行Profile或公共NuGet发布。开发夹具与进程终止不能替代真实工位资格。
"@
$taskCommentPath=Join-Path $PSScriptRoot 'close-comment.md'
$taskComment | Set-Content -LiteralPath $taskCommentPath -Encoding utf8
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskRemote=@(git -c safe.directory=D:/projects/SharpInspect.NET ls-remote origin refs/heads/main)
    if($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1 -or
        ($taskRemote[0] -split '\s+')[0] -cne $taskPublication.head) { throw 'Remote changed before closure.' }
    $taskIssue=(& $taskGh issue view 54 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-53\]' -or $taskIssue.state -cne 'OPEN') { throw 'Unexpected issue state.' }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskUnchecked=[regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count
    if($taskUnchecked -ne 7) { throw 'Acceptance checklist changed; review actual body before editing.' }
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 54 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) { throw 'Checklist update failed.' }
    & $taskGh issue comment 54 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) { throw 'Completion comment failed.' }
    & $taskGh issue close 54 --repo HoboCY/SharpInspect.NET
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
    if(($taskAfterTickets | Where-Object number -eq 54).state -cne 'CLOSED' -or
        @($taskAfterTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object { $_.labels.name } |
            Where-Object { $_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human' }).Count) { throw 'Closure or label readback differs.' }
    [ordered]@{ticket=53;issue=54;state='CLOSED';head=$taskPublication.head;labelChanges=$taskLabelChanges;
        closed=@($taskAfterTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskAfterTickets | Where-Object state -ceq 'OPEN').Count;total=$taskAfterTickets.Count} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}
