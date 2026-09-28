$ErrorActionPreference = 'Stop'
$t38Publication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket38-publication.json') -Raw | ConvertFrom-Json
if ($t38Publication.ticket -ne 38 -or $t38Publication.head -cne (git rev-parse HEAD).Trim() -or
    $t38Publication.head -cne $t38Publication.server -or $t38Publication.divergence -notmatch '^0\s+0$') {
    throw 'Verified T38 publication required.'
}
$t38Summary = @(Get-Content -LiteralPath (Join-Path $t38Publication.validationRoot 'test-summary.json') -Raw | ConvertFrom-Json)
$t38Total = ($t38Summary | Measure-Object -Property passed -Sum).Sum
if ($t38Summary.Count -ne 7 -or $t38Total -le 1859 -or
    @($t38Summary | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.passed -ne $_.total }).Count -ne 0) {
    throw 'Expected final test evidence missing.'
}
$t38Validation = Get-Content -LiteralPath (Join-Path $t38Publication.validationRoot 'validation.json') -Raw | ConvertFrom-Json
if ($t38Validation.result -cne 'Pass') { throw 'Full gate must pass.' }
$t38Body = @'
T38 已实现并提交推送：精确本地 Draft／Released Snapshot 可导出签名数据包；接收端按当前已批准 Recipe Trust Store 验签，仅原子创建新本地 Draft 与不可变来源。

- 固定版本规范清单、成员和整包哈希、ECDSA P-256 签名；有界无压缩内存暂存拒绝路径／链接／重复／尾随／超额／不兼容内容。
- Schema 绑定的 PortableRecipeData 分类与本地许可共同控制导出；本地 Schema 解析无包默认值、工厂或 Provider 回调，签名私钥由独立域 DPAPI 保护。
- 真实 Human 权限，信任与键管理强制一次性 Step-Up；schema24 在同一事务重新核对身份、信任、签名与内容，完整审计覆盖不可变来源、精确重放和取消回滚。
- 来源跨本地编辑保留，后续普通本地发布重新验证与批准；导入不继承外部 Released／Active／资格，不覆盖既有身份。

提交：COMMIT_SHA。本地 HEAD、origin/main 与远端服务器 SHA 一致，分歧 0/0。

最终门禁：TEST_TOTAL/TEST_TOTAL，7 个测试程序集，零失败、零跳过；8 个 NuGet 包及全部独立消费者通过。未重编译的 T32 消费者通过旧构造入口 ABI 检查。源文件零漂移、验证产物 SHA-256 归档，独立审查的问题均已修复并完成回归。

验证说明：docs/verification/v1-38.md；证据目录 VALIDATION_ROOT。早期失败记录保留。真实部署签名身份、公钥批准、外部工位互换、设备、生产激活及正式工位资格为 NOT_RUN；公开 NuGet 消费者覆盖匿名拒绝与第二进程只读边界，认证正向流程由受控 Runtime 验证。
'@
$t38Body = $t38Body.Replace('COMMIT_SHA', $t38Publication.head).Replace('TEST_TOTAL', [string]$t38Total).Replace('VALIDATION_ROOT', $t38Publication.validationRoot)
$t38BodyFile = Join-Path $PSScriptRoot 'ticket38-close-comment.md'
$t38Body | Set-Content -LiteralPath $t38BodyFile -Encoding utf8
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    & $taskGh issue comment 39 --body-file $t38BodyFile
    if ($LASTEXITCODE -ne 0) { throw 'T38 completion comment failed.' }
    & $taskGh issue close 39 --reason completed
    if ($LASTEXITCODE -ne 0) { throw 'T38 issue close failed.' }
    & $taskGh issue view 39 --json number,title,state,closedAt,url
    if ($LASTEXITCODE -ne 0) { throw 'T38 issue close verification failed.' }
}
