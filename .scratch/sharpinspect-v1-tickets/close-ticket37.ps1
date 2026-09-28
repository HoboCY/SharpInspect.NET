$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket37-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 37 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T37 publication required.'
}
$taskSummary = Get-Content -LiteralPath (Join-Path $taskPublication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json
if (($taskSummary | Measure-Object -Property passed -Sum).Sum -ne 1859 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$taskBody = @'
T37 已实现并提交推送：Runtime 独占工位资格会话，按冻结计划验证五类输出隔离，持久接纳后才运行独立资格刺激，正常、失败、取消及冷恢复均恢复并读回目标配置。

- 专门权限和精确一次性 Step-Up，持久目标与瞬态配置分离，类型化 QualificationRunId 和独立 payload。
- schema23 原子身份、命令及会话事实，完整签名和跨事件语义验证、容量恢复预留、冷恢复责任及实际资源退休。
- WPF 工程页，独立 NuGet 消费者匿名拒绝与第二进程只读查询；结束保持 Disarmed。

提交：COMMIT_SHA。已核对本地 HEAD、origin/main 和远端服务器 SHA 完全一致，分歧 0/0。

最终完整门禁：Runtime 1356、WPF 218、OpenCv 15、Virtual 97、Hikrobot 82、Conformance 31、Calibration 60，共 1859/1859，零失败、零跳过。8 个 NuGet 包和全部独立消费者通过。789 个源文件零漂移，426 个验证产物归档 SHA-256。未重编译的 T32 消费者通过旧 Virtual 11 参数和 WPF 9 参数构造入口兼容检查。独立产品与规格审查问题已关闭。

证据：VALIDATION_ROOT；仓库说明 docs/verification/v1-37.md。早期失败运行和修复证据保留。

受控设施验证实现边界。真实 PLC、厂商相机、MES/SPC/产量设施、现场断电和正式资格签发为 NOT_RUN；公开消费者未声称完成正向物理恢复。
'@
$taskBody = $taskBody.Replace('COMMIT_SHA', $taskPublication.head).Replace('VALIDATION_ROOT', $taskPublication.validationRoot)
$taskBodyFile = Join-Path $PSScriptRoot 'ticket37-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 38 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T37 completion comment failed.' }
    & $taskGh issue close 38 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T37 issue close failed.' }
    & $taskGh issue view 38 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T37 issue close verification failed.' }
}
