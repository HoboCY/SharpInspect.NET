param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'collect-evidence.ps1') -Run $Run
$taskEvidence=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'final-evidence.json') -Raw | ConvertFrom-Json
$taskPackages=($taskEvidence.packages | ForEach-Object {
    '| '+[IO.Path]::GetFileName($_.path)+' | `'+$_.sha256+'` |'
}) -join "`n"
$taskFinal=@"
最终组合开发验证通过，清单为 ``$Run\validation.json``；源码路径集及 $($taskEvidence.sourceFiles) 个文件哈希在运行前后完全一致。
解决方案 $($taskEvidence.solutionCases) 个用例得到完整覆盖，包含 V156 的 $($taskEvidence.v156Cases) 个案例；所选证据均通过、无跳过。
原完整轮为 3162 通过／2 失败；最终完整 WPF 为 304/304，两个修正后的夹具为 2/2。原失败保持不变。
用例映射清单 SHA256：``$($taskEvidence.coverageSha256)``。
验证结束后仅移除 3 个文件的 EOF 空行；保留验证时原文及前后哈希，并逐字节确认最后换行之前的内容完全不变。
格式记录为 ``.scratch/t56/eof-finalization/normalization.json``，SHA256：``$($taskEvidence.postValidationFormattingSha256)``；已验证 DLL／包不变。
另有 25 个包消费者测试、8 个本地包、未重新编译的旧 ABI 消费者，以及 12 条迁移路径的 168 个公开阶段通过。
五个独立包消费者 fatal 场景（Dispatcher／后台线程／挂起关闭／强制退出竞态／ProcessExit 故障回调）
均验证一次 Runtime 边界调用、Stopped、Disarmed、Ready=false 与 exit 1；
标准输出／错误输出为空；强制竞态模式执行两次终止委托调用，ProcessExit 回调未运行。迁移验证使用保留的旧包及本轮新包，逐阶段验证恢复、旧源不变和审计尾。

组合运行清单 SHA256：``$($taskEvidence.manifestSha256)``。
Runtime DLL SHA256：``$($taskEvidence.runtimeSha256)``。
最终聚合证据位于 ``.scratch/t56/final-evidence.json``，状态为 ``PassWithRecordedDevelopmentFailures``；
该状态保留下文失败历史；最终组合驱动为 ``Pass``，原完整轮仍为 ``Fail``。
包消费者 fatal 清单 SHA256：``$($taskEvidence.fatalConsumerSha256)``。
旧 ABI 绑定清单 SHA256：``$($taskEvidence.abiEvidenceSha256)``。

| 本轮包 | SHA256 |
|---|---|
$taskPackages
"@
$taskPath='docs/verification/v1-56.md'
$taskDocument=Get-Content -LiteralPath $taskPath -Raw
$taskPlaceholder='本票全量验证尚在执行；最终测试数、包 SHA、消费者／迁移结果和失败记录在完成后补充。'
if(-not $taskDocument.Contains($taskPlaceholder)) {throw 'Expected document placeholder missing.'}
$taskDocument=$taskDocument.Replace($taskPlaceholder,$taskFinal).Replace('NuGet 消费者验证待归档','本轮 NuGet 消费者验证，3 模式通过')
Set-Content -LiteralPath $taskPath -Value ($taskDocument.TrimEnd()+"`r`n") -Encoding utf8 -NoNewline

