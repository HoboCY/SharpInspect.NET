param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t51Run = [IO.Path]::GetFullPath($Run)
$t51Publication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket51-publication.json') -Raw | ConvertFrom-Json
$t51Final = Get-Content -LiteralPath (Join-Path $t51Run 'ticket51-final-evidence.json') -Raw | ConvertFrom-Json
if ($t51Final.result -cne 'Pass' -or $t51Publication.ticket -ne 51 -or $t51Publication.issue -ne 52 -or
    $t51Publication.validationRoot -cne $t51Run -or $t51Publication.head -cne (git rev-parse HEAD).Trim() -or
    $t51Publication.head -cne $t51Publication.server -or $t51Publication.head -cne $t51Publication.tracking -or
    $t51Publication.divergence -notmatch '^0\s+0$') { throw 'Verified T51 publication required before issue52 closure.' }
$t51Body = @"
T51 已完成，提交 $($t51Publication.head) 已推送 main；HEAD、origin/main 和服务端 main 一致，分歧 0/0。

Runtime 从耐久 stage/Manifest 分页恢复，以单一物理工作器写唯一临时 PNG、刷盘、解码核验规范像素哈希并原子改名；保护句柄与单次内部凭证约束 Succeeded 事务，只有成功落库后才清理 stage 并追加独立释放事实。覆盖 Mono8、Mono16 10/12/16 位、BGR 与 padding，权威图不缩放或画叠加。已有 final 无成功记录可恢复同一尝试；取消、超时和进程退出不允许迟到补写或第二权威图片。

临时文件/SQL 失败保留可恢复输入，从耐久队列按预算重试；次数耗尽明确 Failed、禁止继续自动重试，保留原失败原因。完整积压在预算内不撤销 Arm；达到数量、字节或年龄上限关闭下一准入并记录 ImageEvidenceBacklog 报警，已接受周期仍可完成。已引用证据缺失/损坏记录锁存的 EvidenceIntegrityFault；无引用文件保留并要求对账，不冒充已引用证据损坏。只读 WPF 证据页显示持久状态、尝试、失败、哈希及最终描述。可选 schema35 经独占34→35迁移启用，历史 Core/Manifest/审计事实不改写。

验证：Release 构建通过；解决方案 $($t51Final.solutionTestCount) 个唯一用例全部通过，零失败、零跳过，其中本票 $($t51Final.v151CaseCount) 项；8 个 NuGet 包、规定消费者和未重新编译的旧 ABI 消费者通过。保存的真实 schema32/33/34 包分别验证32→33、33→34、34→35，每条路径14个公开迁移阶段；新路径同时核验备份来源、旧写入器拒绝及主库/WAL/审计不变。独立审查原文、固定范围、后续修复和开发失败记录均保留。

[行为、稳定验证 ID 与执行范围](https://github.com/HoboCY/SharpInspect.NET/blob/$($t51Publication.head)/docs/verification/v1-51.md)。完整本地证据：$t51Run；汇总 ticket51-final-evidence.json。

范围限制：未执行真实相机/PLC、强制断电、目标工位性能或正式资格验收；进程终止不替代物理断电。人工处置、孤儿隔离、历史巡检、保留删除和发行资格由后续票完成。
"@
$t51BodyPath = Join-Path $PSScriptRoot 'ticket51-close-comment.md'
$t51Body | Set-Content -LiteralPath $t51BodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($t51Gh)
    $t51Server = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $t51Server.Count -ne 1 -or
        ($t51Server[0] -split '\s+')[0] -cne $t51Publication.head) { throw 'Remote changed before closure.' }
    & $t51Gh issue comment 52 --body-file $t51BodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T51 comment failed.' }
    & $t51Gh issue close 52
    if ($LASTEXITCODE -ne 0) { throw 'T51 closure failed.' }
    $t51Issue = (& $t51Gh issue view 52 --json number,state,title,url | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $t51Issue.state -cne 'CLOSED') { throw 'Issue52 closure unverified.' }
    $t51Issue | ConvertTo-Json
    $t51All = @(& $t51Gh issue list --state all --limit 100 --json number,title,state | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Ticket count refresh failed.' }
    $t51Tickets = @($t51All | Where-Object title -Match '^\[V1-\d{2}\]')
    [ordered]@{total=$t51Tickets.Count;closed=@($t51Tickets | Where-Object state -CEQ 'CLOSED').Count;
        open=@($t51Tickets | Where-Object state -CEQ 'OPEN').Count} | ConvertTo-Json
}
