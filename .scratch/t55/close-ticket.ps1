$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 55 -or $taskPublication.issue -ne 56 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedFailures' -or $taskEvidence.solutionCases -ne 3127 -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T55 publication required before closure.'
}
$taskComment=@"
T55 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 与服务端 SHA 一致，分歧 0/0。

显式 schema39 留存配置及版本化策略管理空间预留、WAL、图片和 Required Outbox 积压。达到阈值阻止新 Trigger，已接受周期继续结算 Core、PNG 和 ACK。保留义务绑定原始已验证来源与起算事件；Hold、延期和释放需要精确 Step-Up，只有获准且到期的文件能经耐久 DeleteIntent、同句柄删除和结果事实退役。未知结果保持 Unknown。

大型清理和 checkpoint 只在启动停产维护中执行，逻辑超时不能释放实际物理所有权。WAL 超过普通门槛时，既有认证和精确策略发布可使用显式、有界且跨重启累计的控制写入预算；策略发布不自动消除故障或执行 checkpoint。运维页面显示容量、保留历史、处置状态和恢复配额。

最终完整解决方案 3127/3127，含 T55 92 项；独立包消费者 25 项、8 个本地包、旧编译二进制 ABI 和 12 条迁移路径共 168 个公开阶段均通过。全部测试无跳过。38→39 使用独立旧／新包消费者、实际进程终止与重开、备份及旧写入器零修改拒绝证据。先前中止与失败的开发记录均保留。

前一完整轮为 3126 通过、旧资格场景 V140_S24 一项失败；原 DLL 的一次隔离复现通过，随后完全相同源码的解决方案测试 3127/3127。原轮根因仍 Unproven，不把复核通过解释为已修复原失败。

后续包迁移在 schema35→38 第6阶段返回 TraceAuditUnavailable。保留原轮 Fail、失败库及备份；只修正探针先提交命令、后等启动对账的时序遗漏，加入提交前完成及已验证审计尾证明，始终只提交一次。复用相同产品 DLL、8个包和已通过测试，在新目录完成受影响的三条对账路径及尚未执行的38→39，共56阶段。最终汇总为 PassWithRecordedFailures，迁移原轮内部拒绝码仍 Unproven。

续验证在前三条42阶段通过后，于最后一条的消费者构建中中断，原因和退出码未捕获。保留该清单 NotRun；确认没有原验证进程且尚未进入任何迁移阶段后，仅在另一新目录执行剩余38→39的14阶段，完整证据绑定记录于验证文档。

[实现、稳定验证 ID 和实际范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-55.md)。最终证据清单 SHA256：$($taskPublication.evidenceSha256)。独立复核发现的恢复认证锁定边界已修复，并验证达到失败上限后的禁用状态跨冷重启仍生效。

真实 PLC／相机／MES、实际磁盘耗尽、硬件断电耐久、桌面人工验收、完整正式 Profile、现场部署和公开 NuGet 发布未执行。控制写入门槛是事务前准入水位，规划余量不保证 SQLite 写放大；控制预算耗尽后不保证自救。
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
    $taskIssue=(& $taskGh issue view 56 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-55\]' -or $taskIssue.state -cne 'OPEN') {throw 'Unexpected issue state.'}
    $taskInitial=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'issue.json') -Raw | ConvertFrom-Json
    if($taskIssue.body -cne $taskInitial.body -or [regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count -ne 6) {
        throw 'Acceptance body changed; review current requirements before closing.'
    }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 56 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) {throw 'Checklist update failed.'}
    & $taskGh issue comment 56 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) {throw 'Completion comment failed.'}
    & $taskGh issue close 56 --repo HoboCY/SharpInspect.NET
    if($LASTEXITCODE -ne 0) {throw 'Issue closure failed.'}
    $taskLabels=@($taskIssue.labels.name | Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'})
    if($taskLabels.Count) {
        & $taskGh issue edit 56 --repo HoboCY/SharpInspect.NET --remove-label ($taskLabels -join ',')
        if($LASTEXITCODE -ne 0) {throw 'Closed ticket label synchronization failed.'}
    }
    $taskAfter=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) {throw 'Closure readback failed.'}
    $taskTickets=@($taskAfter | Where-Object title -Match '^\[V1-\d{2}\]')
    if(($taskTickets | Where-Object number -eq 56).state -cne 'CLOSED' -or
        @($taskTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object {$_.labels.name} |
            Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'}).Count) {throw 'Closure or label readback differs.'}
    $taskClosed=(& $taskGh issue view 56 --repo HoboCY/SharpInspect.NET --json number,state,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskClosed.state -cne 'CLOSED' -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[x\]').Count -ne 6 -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[ \]').Count -ne 0) {throw 'Closed acceptance checklist readback differs.'}
    $taskClosed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-after-close.json') -Encoding utf8
    [ordered]@{ticket=55;issue=56;state='CLOSED';head=$taskPublication.head;removedLabels=$taskLabels;
        closed=@($taskTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -ceq 'OPEN').Count;total=$taskTickets.Count} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}
