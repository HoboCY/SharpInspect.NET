$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 45 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T45 commit and push required.' }
$taskRun = $taskPublication.validationRoot
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Raw | ConvertFrom-Json
$taskCases = Get-Content -LiteralPath (Join-Path $taskRun 'v145-case-results.json') -Raw | ConvertFrom-Json
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskRun 'test-summary.json') -Raw | ConvertFrom-Json)
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskReview = Get-Content -LiteralPath (Join-Path $taskRun 'review-evidence.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskCases.result -cne 'Pass' -or
    $taskCases.assemblyCaseCount -ne 31 -or $taskAbi.result -cne 'Pass' -or $taskSummary.Count -ne 7 -or
    $taskValidation.additionalBoundaryTests.passed -ne 2 -or
    $taskReview.result -cne 'Pass' -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count) {
    throw 'Full suite, stable case, ABI and review evidence required.'
}
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
$taskBody = @"
T45 已实现并提交推送：Graceful Stop 关闭新触发，保留已接受周期直至交付和资源退休；Fault Abort 取消计算并固定 Cancelled + Unknown，健康存储和 PLC 仍完成普通交付，已固定 Success/Timeout/Core 不改写。

- 未登录、锁屏、旧会话下物理本机 Stop 可提交，其他权限不因此开放。Stop 在受理时绑定所属周期的退休结果，故障中止后不会因心跳收尾延迟而误记正常完成。
- 实际报警 ProductionImpact 控制中止；确认、复位不武装、不释放 PLC ACK、不回收挂起算法帧。已固定的超时结果可在算法仍持有帧时交付，下一周期仍受资源退休门约束。
- 宿主关闭使用单一生产收尾期限；缺少 ACK 时保留原 Core/ResultValid 并报告关闭未完成。隔离进程强制终止后，重启不会补记正常 Stop 完成。
- 全量检查中发现并修复空闲 Stop 的报警优先级回归；恢复授权提交争用的确定性回归证明，仅重试已回滚事务，沿用原期限、尝试和一次性 Step-Up，不重复 PLC 清理。

提交：$($taskPublication.head)。HEAD、origin/main 和服务器 SHA 一致，分歧 0/0。

开发验收：7 个程序集完整回归 $taskTotal/$taskTotal，零失败、零跳过；此后另加 2 个恢复授权重试边界并单独执行通过，共 $($taskValidation.distinctPassedCases) 个不同用例。全部原始源码和 8 个产品库二进制保持不变，唯一新增为包含这 2 项测试的文件；两轮测试程序集和原始全量清单分别保留。本票共 31 个新实例、6 个复用边界、8 个 NuGet 包、全部独立消费者及旧二进制 ABI 检查通过。独立审查的实际范围、已修复发现和一次服务重启中断均保留。源码及产物证据位于 $taskRun，说明见 docs/verification/v1-45.md。

两轮早期全量失败及全部专项失败记录保持原状。第三轮完整检查在最后消费者遇到一次烟测等待超时，原清单仍为 Fail；最终清单分别绑定已通过的全量测试、未修改源码的消费者复验和补充测试。原消费者同一二进制另在两个新数据库上通过；独立调查排除 Dispose 路径，该间歇等待问题的具体原因仍未证实，详见验证说明。真实 PLC、相机、安全停线、实物处置、生产性能及设备断电资格为 NOT_RUN；软件进程终止测试不能替代真实工位资格。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket45-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 46 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T45 completion comment failed.' }
    & $taskGh issue close 46 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T45 close failed.' }
    & $taskGh issue view 46 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T45 close verification failed.' }
}
