$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket39-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 39 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T39 publication required.'
}
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json)
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
if ($taskSummary.Count -ne 7 -or $taskTotal -le 1923 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$taskValidation = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Full gate must pass.' }
$taskBody = @"
T39 已实现并提交推送：部署负责人通过维护界面、ManageProductionPolicy 权限和一次性 Step-Up，发布完整追溯存储政策并读取当前准入预检。

- 11 类保留规则、磁盘储备、Required Route／图像积压、stage／commit 截止时间、scrubber／checkpoint 预算、WAL 上限及审批依据均显式提供；拒绝缺失、矛盾、物理不可能参数和当前路由不匹配。
- schema25 将精确政策、冻结规则快照、身份与命令绑定到不可变审计；版本 CAS 与严格重放检查覆盖当前身份、会话、授权版本／政策及来源。
- 后续较短政策不能重解释旧快照；纯保留义务、追加延长和 Hold 谱系绑定精确快照与前驱。本票不创建生产 Run，实际逐 Run 锁存留给周期票消费。
- 真实 WPF Dispatcher 与按钮经 SQLite 发布两个版本，独立进程只读重启验证历史快照不变；磁盘／WAL 未知和未实现准入门持续拒绝生产。

提交：$($taskPublication.head)。本地 HEAD、origin/main 与远端服务器 SHA 一致，分歧 0/0。

最终门禁：$taskTotal/$taskTotal，7 个测试程序集，零失败、零跳过；8 个 NuGet 包及全部独立消费者通过。未重编译的 T32 消费者通过旧构造入口 ABI 检查并与包内 DLL 逐字节绑定。源码零漂移、验证产物 SHA-256 归档，独立审查问题已修复并回归。

验证说明：docs/verification/v1-39.md；证据目录 $($taskPublication.validationRoot)。早期失败证据保留。真实生产政策阈值、真实设备、实际 Run／证据／清理执行器及正式工位资格为 NOT_RUN；开发通过不构成部署或生产准入。完整 Shell 导航仅静态接线检查，动态消费者覆盖实际面板与按钮。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket39-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 40 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T39 completion comment failed.' }
    & $taskGh issue close 40 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T39 issue close failed.' }
    & $taskGh issue view 40 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T39 issue close verification failed.' }
}
