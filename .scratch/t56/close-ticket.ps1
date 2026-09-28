$ErrorActionPreference='Stop'
$taskPublication=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Raw | ConvertFrom-Json
$taskEvidence=Get-Content -LiteralPath $taskPublication.evidence -Raw | ConvertFrom-Json
if($taskPublication.ticket -ne 56 -or $taskPublication.issue -ne 57 -or
    (Get-FileHash -LiteralPath $taskPublication.evidence).Hash -cne $taskPublication.evidenceSha256 -or
    $taskEvidence.result -cne 'PassWithRecordedDevelopmentFailures' -or $taskEvidence.solutionCases -ne 3164 -or
    $taskPublication.head -cne (git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T56 publication required before closure.'
}
$taskComment=@"
T56 已完成，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main 与服务端 SHA 一致，分歧 0/0。

诊断在扇出和序列化前按显式版本化策略分类，只接受批准的有界标量；未知对象不解构、不 ToString，禁入秘密整条拒绝。算法仅取得本次执行 scope。Safe、Protected 和可选 Safe 转发分别拥有固定物理 worker、独立队列和容量，慢速／故障 sink 不改写已接受算法结果，超时不提前释放仍运行的物理所有权。

两个滚动 UTF-8 JSONL 存储使用分离的本地 NTFS 根、验证后的 owner/ACL/卷和目录身份、独占写者租约及独立额度。Protected 保留更短，两个频道均满足精确 Trace 策略的最低保留及共享卷预留，且不得与数据库和配置的权威证据目录重叠。查询重新验证分类及当前持久化权限，WPF 清除锁屏／注销／导航后的数据，拒绝迟到恢复。

实际策略和安装绑定进入生产指纹；激活完成前保持门禁拒绝，输出故障进入健康快照并由显式 Alarm Policy 决定生产影响。执行 owner 持有运行时服务以避免异步激活后漏记生产诊断。异常只在拥有边界至多尝试一次，普通日志不充当 Core 或审计。Dispatcher／后台线程／挂起关闭均执行受控 Fault Abort、Stopped/Disarmed/Ready=false 和 exit 1。

最终组合验证覆盖全部 3164 个用例，含 T56 37 个案例。原完整轮 3162 通过／2 失败；补充完整 WPF 304/304 与两个修正夹具 2/2，原失败保留。包消费者测试 25 项、5 个独立 fatal 进程场景、8 个本地包、旧二进制 ABI 和 12 条迁移路径共 168 阶段均通过，无跳过。实际证据详见 [稳定 ID、边界及验证记录](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskPublication.head)/docs/verification/v1-56.md)。最终证据 SHA256：$($taskPublication.evidenceSha256)。

先前开发失败与中止均保留：NTFS 测试共享/ACL 恢复问题、未配置新部署的旧夹具、构建可空诊断、过长测试 TEMP 导致的 4 个迁移阶段失败，以及旧 sink 迟到调用计数兼容性及权威关停异常跳过诊断关闭。路径对照使用同一 DLL，仅缩短 TEMP 后 14/14 通过；未捕获原 native 返回码，不声称任意长迁移路径已获支持。计数修复通过原 V112_T03，并保留管道独立迟到统计。

既有 V127_R01 曾在全量轮返回 Rejected，同 DLL 隔离复验通过；原拒绝原因仍 Unproven。仅增强断言的原因码输出，未调整标定逻辑或阈值。负向目录隔离对照及一次增量构建复用负向 DLL 的证据也已保留，验证脚本现强制重建。

另保留原完整轮 V145_D01 初始化失败（原因 Unknown/Unproven），并补齐实际写者进程退出屏障；V143_S09 原 Hash 篡改会先触发签名检查点校验，现精确改为载荷篡改，保持原拒绝码和无写入断言。包消费者曾发现缺失 DI 引用、Domain 退出竞态及 WPF ProcessExit 异常，最终使用有界 Runtime 关闭后原生自终止，五模式输出为空。

真实 PLC／相机／工位、真实磁盘耗尽、硬断电、外部账户 owner 攻击、长时间 soak、桌面视觉验收、正式 Profile、部署与公开 NuGet 发布未执行。Protected ACL 不防本机管理员或离线读取；诊断无无损交付保证；临时 capture 和 SupportBundle 属于后续票。
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
    $taskIssue=(& $taskGh issue view 57 --repo HoboCY/SharpInspect.NET --json number,state,title,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskIssue.title -notmatch '^\[V1-56\]' -or $taskIssue.state -cne 'OPEN') {throw 'Unexpected issue state.'}
    $taskInitial=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'issue.json') -Raw | ConvertFrom-Json
    if($taskIssue.body -cne $taskInitial.body -or [regex]::Matches($taskIssue.body,'(?m)^- \[ \]').Count -ne 8) {
        throw 'Acceptance body changed; review current requirements before closing.'
    }
    $taskIssue | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-before-close.json') -Encoding utf8
    $taskBodyPath=Join-Path $PSScriptRoot 'issue-completed-body.md'
    ($taskIssue.body -replace '(?m)^- \[ \]','- [x]') | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
    & $taskGh issue edit 57 --repo HoboCY/SharpInspect.NET --body-file $taskBodyPath
    if($LASTEXITCODE -ne 0) {throw 'Checklist update failed.'}
    & $taskGh issue comment 57 --repo HoboCY/SharpInspect.NET --body-file $taskCommentPath
    if($LASTEXITCODE -ne 0) {throw 'Completion comment failed.'}
    & $taskGh issue close 57 --repo HoboCY/SharpInspect.NET
    if($LASTEXITCODE -ne 0) {throw 'Issue closure failed.'}
    $taskLabels=@($taskIssue.labels.name | Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'})
    if($taskLabels.Count) {
        & $taskGh issue edit 57 --repo HoboCY/SharpInspect.NET --remove-label ($taskLabels -join ',')
        if($LASTEXITCODE -ne 0) {throw 'Closed ticket label synchronization failed.'}
    }
    $taskAfter=@(& $taskGh issue list --repo HoboCY/SharpInspect.NET --state all --limit 100 --json number,title,state,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0) {throw 'Closure readback failed.'}
    $taskTickets=@($taskAfter | Where-Object title -Match '^\[V1-\d{2}\]')
    if(($taskTickets | Where-Object number -eq 57).state -cne 'CLOSED' -or
        @($taskTickets | Where-Object state -ceq 'CLOSED' | ForEach-Object {$_.labels.name} |
            Where-Object {$_ -cin 'needs-triage','needs-info','ready-for-agent','ready-for-human'}).Count) {throw 'Closure or label readback differs.'}
    $taskClosed=(& $taskGh issue view 57 --repo HoboCY/SharpInspect.NET --json number,state,body,labels | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $taskClosed.state -cne 'CLOSED' -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[x\]').Count -ne 8 -or
        [regex]::Matches($taskClosed.body,'(?m)^- \[ \]').Count -ne 0) {throw 'Closed acceptance checklist readback differs.'}
    $taskClosed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'issue-after-close.json') -Encoding utf8
    [ordered]@{ticket=56;issue=57;state='CLOSED';head=$taskPublication.head;removedLabels=$taskLabels;
        closed=@($taskTickets | Where-Object state -ceq 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -ceq 'OPEN').Count;total=$taskTickets.Count} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'closure.json')
}





