$ErrorActionPreference='Stop'
$taskEvidence=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'final-evidence.json') -Raw | ConvertFrom-Json
if($taskEvidence.result -cne 'PassWithRecordedFailures' -or $taskEvidence.solutionCases -ne 3127 -or
    $taskEvidence.v155Cases -ne 92 -or $taskEvidence.consumerCases -ne 25 -or
    $taskEvidence.migrationPhases -ne 168 -or $taskEvidence.legacyAbi -cne 'Pass' -or
    -not $taskEvidence.unchangedProductSourcesFromFullSolutionRun -or -not $taskEvidence.unchangedRuntimeFromIsolatedRecheck) {
    throw 'Complete verified T55 evidence required.'
}
$taskPath='docs/verification/v1-55.md'
$taskDocument=Get-Content -LiteralPath $taskPath -Raw
$taskMarker='完整验证仍在执行，最终数量、包绑定与独立审查结论将在提交前补齐。'
if([regex]::Matches($taskDocument,[regex]::Escape($taskMarker)).Count -ne 1) {throw 'Verification placeholder differs.'}
$taskPackageRows=($taskEvidence.packages | ForEach-Object {
    '| '+[IO.Path]::GetFileName($_.path)+' | `'+$_.sha256+'` |'
}) -join "`n"
$taskReplacement=@"
最终开发验证汇总为 **PassWithRecordedFailures**：解决方案 $($taskEvidence.solutionCases)/$($taskEvidence.solutionCases)，其中 T55 为 $($taskEvidence.v155Cases) 项；
独立包消费者 $($taskEvidence.consumerCases)/$($taskEvidence.consumerCases)，均无失败、无跳过。
8 个本地 NuGet 包、12 条迁移路径共 168 个公开阶段，以及未重新编译的旧二进制 ABI 均通过。
原完整轮目录：``$($taskEvidence.run)``；其 ``validation.json`` 保留 Fail，
SHA256 为 ``$($taskEvidence.manifestSha256)``。
该轮解决方案、消费者、包及前 8 条迁移通过；后续修复两个迁移探针文件后，
在 ``$($taskEvidence.continuationRun)`` 完成 3 条路径的 42 阶段；进程随后在最后一条的消费者构建中中断，
原清单保留 NotRun。确认原进程不存在、尚无迁移阶段开始后，在 ``$($taskEvidence.remainingRun)``
完成剩余 38→39 的 14 阶段。汇总续验证清单 ``$($taskEvidence.continuationManifest)`` 为 Pass，
SHA256 为 ``$($taskEvidence.continuationManifestSha256)``。中断原因和原进程退出码未捕获。
解决方案复核开始时的 $($taskEvidence.sourceFiles) 个源文件与前一失败轮一致；此后仅改动
``tools/evidence-reconciliation-migration-probe/Program.cs`` 及其 PowerShell 驱动，
前后哈希已记录于续验证清单。产品源文件、Runtime DLL 和最终包保持不变；Runtime DLL 也与隔离复查原二进制一致：
``$($taskEvidence.runtimeSha256)``。

包消费者绑定最终包内 Runtime DLL；保留旧包哈希，逐阶段核对旧写入器拒绝后的数据库／WAL 零修改、
种子及新写入审计序列已被验证。38→39 的 1–13 阶段实际终止消费者进程树，14 阶段完成后重开，
每阶段均证明原始行及备份保全；公开保留服务可用，未批准生产策略时容量仍不可用、WAL 上限为 NULL。
该迁移探针没有产生新的生产 Core，未配置任何删除类别。
旧 ABI 消费者 SHA256 保持 ``C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1``，
仅替换最终包中的 Abstractions、Runtime、Wpf 和 Cameras.Virtual 四个程序集后执行通过。

| 最终本地包 | SHA256 |
| --- | --- |
$taskPackageRows

各项已通过范围与失败轮分别留存；前轮 V140_S24 根因仍为 Unproven，以下记录不作已修复声明。
"@
$taskDocument=$taskDocument.Replace($taskMarker,$taskReplacement.Trim())
$taskDocument=$taskDocument.Replace('保持源码与阈值不变后继续完整复核，原报告保留 Fail。',
    '保持源码与阈值不变后，未启用诊断钩子的解决方案复核通过 3127 项；原报告保留 Fail。该轮后续迁移另有下面记录的失败。')
[IO.File]::WriteAllText((Join-Path (Get-Location) $taskPath),$taskDocument,[Text.UTF8Encoding]::new($false))
