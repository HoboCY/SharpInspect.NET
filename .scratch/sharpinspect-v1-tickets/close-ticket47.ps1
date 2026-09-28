param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket47-publication.json') -Raw | ConvertFrom-Json
$taskFinal = Get-Content -LiteralPath (Join-Path $Run 'ticket47-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskFinal.result -cne 'Pass' -or $taskPublication.ticket -ne 47 -or $taskPublication.issue -ne 48 -or
    $taskPublication.validationRoot -cne [IO.Path]::GetFullPath($Run) -or
    $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T47 publication is required before closing issue 48.' }
$taskBody = @"
已完成 T47，提交 $($taskPublication.head) 已推送 main；HEAD、origin/main、远端 main 一致，分歧 0/0。

实现独立且版本化的开机/PLC 换方后武装政策，默认人工武装/重武装。自动入口由 Runtime 持有一次性权限，PLC 原因必须经过真实成功激活、专用握手闭合及新的输入稳定窗口，复用实际准入门。schema32 记录尝试、实际物理 Ready 回执与 PLC 状态处置；维护后的首次人工确认绑定真实用户及维护头。当前 Ready 与历史 ReadyConfirmed 分别处理，撤销后实际清除 PLC Ready。

组合验证完成：原 7 个程序集完整执行 2,469 项，2,467 通过、2 失败，原结果保持 Fail。之后修正新 R17 测试的断言，8 个产品 DLL 哈希不变；T47 全部 84 项及旧身份拒绝 3 项分别复验共 87/87 通过，复用其他完整结果。新包消费者另补一条缺失的命名空间引用，原编译失败保留，产品与测试源码不变。旧超时未复现、原因未确定，不宣称产品修复或单次全套通过。组合共 $($taskFinal.distinctPassedCases) 个不同通过用例（包含包消费者测试）；后续实际重新构建、8 个 NuGet 包、全部独立消费者、V147_N01 及未重编译旧 ABI 消费者通过。独立审查发现的起因仲裁和时间量化问题已处理；撤掉仲裁修复的对照运行直接检出 ResetObserved 后 ProtocolFault。验证文档：docs/verification/v1-47.md；本地证据根：$Run。

范围限制：未运行真实 PLC/相机、正式部署、升级迁移或工位资格验收。正式维护日志 reader 属后续票，默认缺失会拒绝自动武装；冷启动尚未在当前进程准备持久 Active 算法时由实际准入门拒绝。本票不以内部夹具或激活成功宣称生产合格。
"@
$taskBodyPath = Join-Path $PSScriptRoot 'ticket47-close-body.md'
$taskBody | Set-Content -LiteralPath $taskBodyPath -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    $taskServer = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskServer.Count -ne 1 -or
        ($taskServer[0] -split '\s+')[0] -cne $taskPublication.head) { throw 'Remote main changed before issue closure.' }
    & $taskGh issue comment 48 --body-file $taskBodyPath
    if ($LASTEXITCODE -ne 0) { throw 'T47 completion comment failed.' }
    & $taskGh issue close 48
    if ($LASTEXITCODE -ne 0) { throw 'T47 issue closure failed.' }
    $taskIssue = (& $taskGh issue view 48 --json number,state,title,url | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $taskIssue.state -cne 'CLOSED') { throw 'Issue 48 is not confirmed closed.' }
    $taskIssue | ConvertTo-Json
    $taskAll = @(& $taskGh issue list --state all --limit 100 --json number,title,state | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Post-closure count query failed.' }
    $taskTickets = @($taskAll | Where-Object title -Match '^\[V1-\d{2}\]')
    [ordered]@{total=$taskTickets.Count;closed=@($taskTickets | Where-Object state -CEQ 'CLOSED').Count;
        open=@($taskTickets | Where-Object state -CEQ 'OPEN').Count} | ConvertTo-Json
}
