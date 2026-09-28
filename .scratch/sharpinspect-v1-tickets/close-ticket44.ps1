$ErrorActionPreference = 'Stop'
$taskPublication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket44-publication.json') -Raw | ConvertFrom-Json
if ($taskPublication.ticket -ne 44 -or $taskPublication.head -cne (git rev-parse HEAD).Trim() -or
    $taskPublication.head -cne $taskPublication.server -or $taskPublication.head -cne $taskPublication.tracking -or
    $taskPublication.divergence -notmatch '^0\s+0$') { throw 'Verified T44 commit and push required.' }
$taskRun = $taskPublication.validationRoot
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
$taskCases = Get-Content -LiteralPath (Join-Path $taskRun 'v144-case-results.json') -Raw | ConvertFrom-Json
$taskSummary = @(Get-Content -LiteralPath (Join-Path $taskRun 'test-summary.json') -Raw | ConvertFrom-Json)
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskCases.result -cne 'Pass' -or
    $taskCases.assemblyCaseCount -ne 67 -or $taskAbi.result -cne 'Pass' -or $taskSummary.Count -ne 7 -or
    @($taskSummary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count) {
    throw 'Full suite, consumer, stable case and ABI evidence required.'
}
$taskTotal = ($taskSummary | Measure-Object -Property passed -Sum).Sum
$taskBody = @"
T44 已实现并提交推送：未确认 PLC 交付保留原 Inspection、固定 payload、已知持久握手阶段与不确定性；重启进入 Recovery，不自动重发结果。

- 人工恢复绑定精确事件、原因和实物处置，验证安全停线来源、物理会话、ManualRecovery 权限及新鲜 Step-Up。授权审计先提交，再清理 Runtime 拥有的输出并验证心跳、同步及 Trigger/Ack 低电平。
- 不写 PLC Ack、不补造普通结果、不重编码历史 Core/payload。恢复失败保留待处理记录；新尝试需要重新授权，全部待处理清除后仍未武装，后续生产依赖普通门槛及另行 Arm。
- schema 30 显式启用恢复和完整审计，拒绝通用事件入口绕过授权。旧 schema 28/29 的哈希和读取契约保持兼容，普通启动不执行旧库迁移。
- WPF 维护页面展示待恢复记录及原周期/报文引用；陈旧数据和会话变化使提交失效。独立 NuGet 消费者验证恢复入口挂载及未认证状态下禁用命令。

提交：$($taskPublication.head)。HEAD、origin/main、服务器 SHA 一致，分歧 0/0；保留远端 README 提交 fdf024ab701d7456c5a4adb3407b96e9250bea9c。

组合开发验收：7 个程序集共 $taskTotal/$taskTotal，零失败、零跳过；本票 67 个实例、8 个 NuGet 包、全部独立消费者及旧二进制消费者 ABI 检查通过。源码与产物哈希、早期失败及审查范围均记录在 $taskRun，说明见 docs/verification/v1-44.md。

原始全量运行因磁盘空间耗尽中断，失败状态保留；续跑只在 SampleHost 冒烟检查增加异步读取等待，全部库、测试和包源码未变，重新构建的独立消费者完整通过。来源、唯一源码差异与复用边界记录在验证说明中。

独立 DeepSeek 审查运行器在执行前因 WinError 2 失败，未计作独立审查通过；主任务完成复核并修复相关问题。真实 PLC、安全停线来源、相机、实物处置、断电恢复、性能/存储耗尽及完整工位资格为 NOT_RUN，开发验证不替代生产验收。
"@
$taskBodyFile = Join-Path $PSScriptRoot 'ticket44-close-comment.md'
$taskBody | Set-Content -LiteralPath $taskBodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 45 --body-file $taskBodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T44 completion comment failed.' }
    & $taskGh issue close 45 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T44 close failed.' }
    & $taskGh issue view 45 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T44 close verification failed.' }
}
