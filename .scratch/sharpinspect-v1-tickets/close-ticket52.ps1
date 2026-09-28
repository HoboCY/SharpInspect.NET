param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskTicket52Run = [IO.Path]::GetFullPath($Run)
$taskTicket52Publication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket52-publication.json') -Raw | ConvertFrom-Json
$taskTicket52Final = Get-Content -LiteralPath (Join-Path $taskTicket52Run 'ticket52-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskTicket52Final.result -cne 'Pass' -or $taskTicket52Final.v152CaseCount -ne 77 -or
    $taskTicket52Final.outboxMigrationConsumers.Count -ne 4 -or
    $taskTicket52Publication.ticket -ne 52 -or $taskTicket52Publication.issue -ne 53 -or
    $taskTicket52Publication.validationRoot -cne $taskTicket52Run -or
    $taskTicket52Publication.head -cne (git rev-parse HEAD).Trim() -or
    $taskTicket52Publication.head -cne $taskTicket52Publication.server -or
    $taskTicket52Publication.head -cne $taskTicket52Publication.tracking -or
    $taskTicket52Publication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T52 publication and complete evidence required before issue53 closure.'
}
$taskTicket52Body = @"
T52 已完成，提交 $($taskTicket52Publication.head) 已推送 main；HEAD、origin/main 和服务端 main 一致，分歧 0/0。

显式路由与独立连接绑定控制外部交付。Runtime 在事务外冻结 Core JSON、Delivery ID、精确字节及版本/哈希，在 Core 的同一 SQLite 耐久事务中创建义务和待办；有界工作器先持久化 AttemptStarted，再发送原身份与字节。Required 接收能力及 Durable Acceptance 通过固定接收方身份验签，确认丢失后重复接收保留同一回执和单一业务效果。超时/退休不允许迟到成功，也不释放尚在运行的物理路由槽位；不声称跨系统 exactly-once。

Required 永久阻断或积压达到上限只关闭新的 Trigger，已接受周期继续完成；BestEffort 交付失败只产生 ProductionImpact=None 的专用报警。接受 Trigger 时即从耐久账本预留完整 Outbox 容量，Core 提交转为实际记录，终态释放未用尝试预算；不可变历史仍计入有限本地容量。schema36 显式支持从32/33/34/35迁移并保留原可选功能。

验证：Release 构建通过；解决方案 $($taskTicket52Final.solutionTestCount) 个用例全部通过，零失败、零跳过，其中本票77项。8个NuGet包、规定消费者及未重新编译的旧ABI消费者通过，4个实际加载库与包内字节一致。旧32→33、33→34、34→35三条路径，以及新增32/33/34/35→36四条路径，均执行各14个公开维护阶段的进程中断与恢复（共98个阶段），验证旧写入器拒绝及主库/WAL/审计不变。五轮独立审查发现与修复、最终架构复核、开发失败记录均保留。

执行来源：原完整调用在最后35→36的旧库seed审计快照尚为Verifying时失败，原Fail记录保留。只修正两份独立迁移验证工具，以本次真实持久审计尾约束Verified水位和原期限；产品/测试/构建输入不变。已通过的解决方案、包消费者和既有三条迁移按哈希复用，四条Outbox迁移全部重新执行后完成有界续验及ABI核对，不称为单次完整执行全过。

[行为、稳定验证ID及执行范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($taskTicket52Publication.head)/docs/verification/v1-52.md)。本地完整证据：$taskTicket52Run；汇总 ticket52-final-evidence.json。

范围：实际进程终止覆盖隔离接收器与迁移；发送器为新store/epoch冷重建，Core事务为最终围栏回滚，三者分别记录。未执行真实MES、生产目的地、真实PLC/相机、硬件断电或完整工位资格矩阵。后续T53承担人工处置、受控重试与corrective delivery，本票不替代这些授权与事实。
"@
$taskTicket52BodyPath = Join-Path $PSScriptRoot 'ticket52-close-comment.md'
$taskTicket52Body | Set-Content -LiteralPath $taskTicket52BodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskTicket52Gh)
    $taskTicket52Server = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskTicket52Server.Count -ne 1 -or
        ($taskTicket52Server[0] -split '\s+')[0] -cne $taskTicket52Publication.head) {
        throw 'Remote changed before closure.'
    }
    & $taskTicket52Gh issue comment 53 --body-file $taskTicket52BodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T52 comment failed.' }
    & $taskTicket52Gh issue close 53
    if ($LASTEXITCODE -ne 0) { throw 'T52 closure failed.' }
    $taskTicket52Issue = (& $taskTicket52Gh issue view 53 --json number,state,title,url | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $taskTicket52Issue.state -cne 'CLOSED') { throw 'Issue53 closure unverified.' }
    $taskTicket52Issue | ConvertTo-Json
    $taskTicket52All = @(& $taskTicket52Gh issue list --state all --limit 100 --json number,title,state | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Ticket count refresh failed.' }
    $taskTicket52Tickets = @($taskTicket52All | Where-Object title -Match '^\[V1-\d{2}\]')
    [ordered]@{total=$taskTicket52Tickets.Count;closed=@($taskTicket52Tickets | Where-Object state -CEQ 'CLOSED').Count;
        open=@($taskTicket52Tickets | Where-Object state -CEQ 'OPEN').Count} | ConvertTo-Json
}



