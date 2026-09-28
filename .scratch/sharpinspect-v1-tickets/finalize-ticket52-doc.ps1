param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
$taskFinalPath = Join-Path $taskRun 'ticket52-final-evidence.json'
$taskFinal = Get-Content -LiteralPath $taskFinalPath -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskFinal.result -cne 'Pass' -or
    $taskFinal.v152CaseCount -ne 77 -or $taskFinal.packageCount -ne 8 -or
    $taskFinal.failedTests -ne 0 -or $taskFinal.outboxMigrationConsumers.Count -ne 4) {
    throw 'Complete passing evidence is required before final documentation.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Source drift.' }
}
$taskDocument = 'docs/verification/v1-52.md'
$taskText = Get-Content -LiteralPath $taskDocument -Raw
if ($taskText.Contains('### 当前候选通过记录')) { throw 'Final section already exists; inspect instead of duplicating.' }
$taskText = $taskText.Replace('当前为实现与验证记录；完整开发检查、包消费者与最终审查结果尚待补录，不作为完成或发行声明。',
    '本票实现、完整开发检查、包消费者和审查结果见下方通过记录；这些开发证据不替代正式发行与真实工位资格。')
$taskText = $taskText.Replace('最终统一检查、完整包消费者与旧 ABI 结果尚待补录。',
    '最终统一检查、完整包消费者与旧 ABI 的同一候选结果见下方；历史失败与中断记录继续保留。')
$taskText = $taskText.Replace('最终完整解决方案、包消费者及迁移结果待取得后补录。',
    '本轮完整解决方案、包消费者及迁移结果已取得，见下方通过记录。')
$taskText = $taskText.Replace('最终全量结果仍待取得。','最终全量结果另列于本轮通过记录。')
$taskText = $taskText.Replace('最后一轮列出的两个内部图片守卫','第三轮审查列出的两个内部图片守卫')
$taskSection = @'

### 当前候选通过记录

__DATE__（Asia/Shanghai）完成汇总。证据目录为
`__RUN__/`，基线提交 `__BASELINE__`，绑定 __SOURCES__ 个源码及配置文件。
实际环境为 __OS__，构建 __BUILD__，.NET SDK __SDK__；本次不是完整框架／工位资格矩阵。

| 检查 | 实际结果 |
| --- | --- |
| Release 构建 | 通过，0 错误；保留既有 NETSDK1138 警告 |
| 解决方案回归 | 复用同一产品源码的原 7 份 TRX，__TOTAL__ 项、__UNIQUE__ 个唯一用例全部通过，0 失败、0 跳过 |
| T52 场景 | 77 项通过；稳定 ID 和行为范围列于前文 |
| 实际包消费者 | 复用已核对哈希的 8 个 NuGet、__CONSUMER_TRX__ 份消费者 TRX及非 TRX 输出、__CONSUMER_CASES__ 次测试执行通过 |
| 既有迁移 | 32→33、33→34、34→35 各 14 个公开阶段的实际进程终止／恢复通过 |
| Outbox 迁移 | 32／33／34／35→36 以新探针各执行 14 阶段通过，含命令后已验证审计水位、备份来源、旧写入器拒绝及主库／WAL／审计不变；两组共 98 阶段 |
| 旧 ABI | 未重编译的消费者通过，4 个加载库与当前 NuGet 字节一致 |
| 独立审查 | 5 份只读审查及 5 份架构结果，处置和当前源码绑定已归档，无未解决的阻断发现 |
| 执行来源 | 原完整调用保持 Fail；仅两份迁移工具变化后的有界续验通过，复制及原路径引用证据均有哈希清单 |

源 35 的包迁移探针还验证旧图片公开查询、工作队列与积压可用、图片证据不为 Faulted。
空库探针只证明功能兼容和真实空集合；实际 Core／PNG／PLC／Outbox 的组合由 R04 另行验证。
发送器的新 store／epoch 冷重建、Core 最终围栏回滚、接收器实际进程终止分别记录；没有把其中一种
当作其他种类的中断证据，也没有执行真实 MES、生产目的地、真实硬件断电或目标工位资格。
完整案例、包哈希及各验证 JSON 的哈希列于 `ticket52-final-evidence.json`。

| 文件 | SHA-256 |
| --- | --- |
| `ticket52-final-evidence.json` | `__FINAL_HASH__` |
| `validation.json` | `__VALIDATION_HASH__` |
| `abi-package-binding.json` | `__ABI_HASH__` |
| `continuation-provenance.json` | `__PROVENANCE_HASH__` |
| 当前实际 Runtime DLL | `__RUNTIME_HASH__` |

'@
$taskValues = @{
    '__DATE__'=(Get-Date).ToString('yyyy-MM-dd HH:mm'); '__RUN__'=$taskRun.Replace('\','/');
    '__BASELINE__'=$taskValidation.startingHead; '__SOURCES__'=$taskValidation.sourceHashes.Count;
    '__OS__'=$taskValidation.os.caption; '__BUILD__'=$taskValidation.os.build; '__SDK__'=$taskValidation.sdk;
    '__TOTAL__'=$taskFinal.solutionTestCount; '__UNIQUE__'=$taskFinal.solutionUniqueTestCount;
    '__CONSUMER_TRX__'=$taskFinal.consumerTrxCount; '__CONSUMER_CASES__'=$taskFinal.consumerTestExecutions;
    '__FINAL_HASH__'=(Get-FileHash -LiteralPath $taskFinalPath).Hash;
    '__VALIDATION_HASH__'=$taskFinal.validationSha256; '__ABI_HASH__'=$taskFinal.abiEvidenceSha256;
    '__PROVENANCE_HASH__'=$taskValidation.provenanceSha256;
    '__RUNTIME_HASH__'=$taskFinal.outboxMigrationConsumers[0].currentRuntimeSha256
}
foreach ($taskKey in $taskValues.Keys) { $taskSection=$taskSection.Replace($taskKey,[string]$taskValues[$taskKey]) }
if ($taskSection -match '__[A-Z_]+__') { throw 'Unresolved documentation placeholders.' }
[IO.File]::WriteAllText((Join-Path (Get-Location).Path $taskDocument),
    $taskText.TrimEnd() + "`r`n" + $taskSection, [Text.UTF8Encoding]::new($false))
'Final development evidence documented; publication remains separate.'
