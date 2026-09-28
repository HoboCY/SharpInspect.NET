param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'T45 development gate has not passed.' }
$taskCases = @()
$taskReused = @()
$taskTrxFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'tests') -Filter '*.trx' -File) +
    @(Get-Item -LiteralPath (Join-Path $taskRun $taskValidation.additionalBoundaryTests.trx))
foreach ($taskFile in $taskTrxFiles) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskTrxHash = (Get-FileHash -LiteralPath $taskFile.FullName).Hash
    foreach ($taskCase in $taskTrx.TestRun.Results.UnitTestResult) {
        $taskEntry = [ordered]@{test=$taskCase.testName;outcome=$taskCase.outcome;duration=$taskCase.duration;
            trx=[IO.Path]::GetRelativePath($taskRun,$taskFile.FullName);trxSha256=$taskTrxHash}
        if ($taskCase.testName -match '(V145_[A-Z]+\d+)') {
            $taskEntry.id=$Matches[1]
            $taskCases += $taskEntry
        } elseif ($taskCase.testName -match '(V142_R05|V142_R12|V109_R10|V109_R12|StopRemainsAvailableWhenStaleAndAcceptedDoesNotBecomeCompletion|ArmDoesNotSubmitWhenPresentationIsUnavailable)') {
            $taskEntry.id=$Matches[1]
            $taskReused += $taskEntry
        }
    }
}
if ($taskCases.Count -ne 31 -or @($taskCases | Where-Object { $_.outcome -cne 'Passed' }).Count) { throw 'Expected 31 passing V145 cases.' }
if ($taskReused.Count -ne 6 -or @($taskReused | Where-Object { $_.outcome -cne 'Passed' }).Count) { throw 'Expected six reused regression cases.' }
[ordered]@{result='Pass';assemblyCaseCount=$taskCases.Count;cases=@($taskCases | Sort-Object { $_.id }, { $_.test });
    reusedCases=@($taskReused | Sort-Object { $_.id }, { $_.test });notRun=@('Real PLC and camera qualification',
        'Physical safety and workpiece disposition','Production performance','Physical power-loss qualification')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRun 'v145-case-results.json') -Encoding utf8

$taskBase = Split-Path -Parent $taskRun
$taskPrior = Join-Path $taskRun 'prior-run-evidence'
[void][IO.Directory]::CreateDirectory($taskPrior)
foreach ($taskFolder in Get-ChildItem -LiteralPath $taskBase -Directory | Where-Object { $_.FullName -ne $taskRun -and $_.Name -ne 'temp' }) {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFolder.FullName -Recurse -File | Where-Object Extension -In '.log','.trx','.json') {
        $taskDestination = Join-Path $taskPrior ($taskFolder.Name + '/' + [IO.Path]::GetRelativePath($taskFolder.FullName,$taskFile.FullName))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
foreach ($taskProcess in Get-ChildItem -LiteralPath (Join-Path $taskBase 'temp') -Directory | Where-Object Name -Match '^v(142-retirement|145-stop-crash)-') {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskProcess.FullName -File) {
        $taskDestination = Join-Path $taskPrior ($taskProcess.Name + '/' + $taskFile.Name)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
foreach ($taskName in 'ticket45-review-01.md','ticket45-review-02-interrupted.json','ticket45-review-03.md','ticket45-review-04.md','ticket45-consumer-investigation.md','ticket45-review-evidence.json') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskPrior $taskName)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ticket45-review-evidence.json') -Destination (Join-Path $taskRun 'review-evidence.json')
foreach ($taskFile in Get-ChildItem -LiteralPath $taskBase -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskPrior $taskFile.Name)
}
'T45 cases, process evidence and prior failures archived.'
