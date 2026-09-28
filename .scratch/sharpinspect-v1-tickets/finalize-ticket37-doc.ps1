param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskGate = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($taskGate.result -cne 'Pass') { throw 'Final full gate required.' }
$taskSummary = & (Join-Path $PSScriptRoot 'archive-ticket37-evidence.ps1') -Run $Run
$taskSummary | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket37-archive-summary.json') -Encoding utf8
$taskDocPath = Join-Path (Get-Location) 'docs/verification/v1-37.md'
$taskDoc = [IO.File]::ReadAllText($taskDocPath)
$taskDoc = $taskDoc.Replace('完整门禁 63', '完整门禁 64')
$taskNote = '- `64-full-gate.log`：提交前仅清理测试代码一个行尾空格及本文末尾空行后，重新完成同一全量门禁。最终发布证据为 `RUN_NAME`，7 套仍为 1859/1859，8 包及全部独立消费者通过；789 源文件零漂移，426 个产物哈希。第 63 次通过证据原样保留。'
$taskNote = $taskNote.Replace('RUN_NAME', (Split-Path -Leaf $Run))
$taskDoc = $taskDoc.Replace('独立审查已促成专用相机所有权', $taskNote + "`r`n`r`n" + '独立审查已促成专用相机所有权')
[IO.File]::WriteAllText($taskDocPath, $taskDoc.TrimEnd() + "`r`n", [Text.UTF8Encoding]::new($false))
git add -- docs/verification/v1-37.md 2> (Join-Path $PSScriptRoot 'ticket37-final-doc-stage-warnings.txt')
if ($LASTEXITCODE -ne 0) { throw 'Final evidence documentation staging failed.' }
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Final staged whitespace check failed.' }
$taskSummary
