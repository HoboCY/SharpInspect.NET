param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
$taskCases = @()
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    foreach ($taskCase in $taskTrx.TestRun.Results.UnitTestResult) {
        if ($taskCase.testName -match '(V142_[A-Z]+\d+[A-Z]?)') {
            $taskCases += [ordered]@{id=$Matches[1];test=$taskCase.testName;outcome=$taskCase.outcome;
                duration=$taskCase.duration;trx=$taskFile.Name;trxSha256=$taskHash}
        }
    }
}
if ($taskCases.Count -ne 52 -or @($taskCases | Where-Object outcome -CNE 'Passed').Count -ne 0) {
    throw "Expected 52 passing V142 assembly cases; actual $($taskCases.Count)."
}
$taskConsumer=Get-Content -LiteralPath (Join-Path $Run 'production-inspection-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskConsumer.Result -cne 'Pass') {throw 'Production NuGet consumer is not passing.'}
[ordered]@{result='Pass';assemblyCaseCount=$taskCases.Count;cases=@($taskCases | Sort-Object id,test);
    consumer=[ordered]@{id='V142_N01';result=$taskConsumer.Result;file='production-inspection-consumer-evidence.json'};
    notRun=@('Real PLC and camera qualification','Production network and production Arm/Ready',
        'Physical power-loss qualification','Governed physical recovery and disposition')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Run 'v142-case-results.json') -Encoding utf8
$taskPrior=Join-Path $Run 'prior-run-evidence'
[void][IO.Directory]::CreateDirectory($taskPrior)
$taskRoot=Split-Path -Parent $Run
foreach ($taskLog in Get-ChildItem -LiteralPath $taskRoot -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskLog.FullName -Destination $taskPrior
}
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskRoot -Directory | Where-Object Name -Match '^(all-|focused-|runtime-)') {
    $taskTarget=Join-Path $taskPrior $taskDirectory.Name
    [void][IO.Directory]::CreateDirectory($taskTarget)
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory.FullName -File -Filter '*.trx') {
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
    }
}
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskRoot -Directory | Where-Object {
    $_.Name -match '^\d{8}-\d{6}-\d{3}$' -and $_.FullName -ne [IO.Path]::GetFullPath($Run) }) {
    $taskTarget=Join-Path $taskPrior $taskDirectory.Name
    [void][IO.Directory]::CreateDirectory($taskTarget)
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory.FullName -File | Where-Object Extension -In '.json','.log') {
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
    }
    $taskTests=Join-Path $taskDirectory.FullName 'tests'
    if (Test-Path -LiteralPath $taskTests) {
        $taskTestTarget=Join-Path $taskTarget 'tests'
        [void][IO.Directory]::CreateDirectory($taskTestTarget)
        Get-ChildItem -LiteralPath $taskTests -File -Filter '*.trx' | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $taskTestTarget
        }
    }
}
foreach ($taskDirectory in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'process-temp') -Directory -Filter 'v142-retirement-*') {
    $taskTarget=Join-Path $taskPrior $taskDirectory.Name
    [void][IO.Directory]::CreateDirectory($taskTarget)
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory.FullName -File | Where-Object Extension -In '.trx','.log') {
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
    }
}
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskRoot -Directory -Filter 'consumer-preflight-*') {
    $taskTarget=Join-Path $taskPrior $taskDirectory.Name
    [void][IO.Directory]::CreateDirectory($taskTarget)
    Get-ChildItem -LiteralPath $taskDirectory.FullName -File | Where-Object Extension -In '.json','.log' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $taskTarget
    }
}
[ordered]@{result='No unresolved P1/P2';scope='T42 production lifecycle, storage/codec, activation contention';
    evidence='Independent reviewer and architect static reviews plus separately archived runtime validation';
    coveredFixes=@('Signed audit to event binding and full projection verification','Pending Core and fault cold recovery',
        'Physical retirement before publication','Bounded Busy retry after rollback and commit gate release',
        'Schema 28 canonical cold-open validation','Fresh consumer cache and actual loaded assembly to nupkg binding'); reviewedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'independent-review.json') -Encoding utf8
'T42 case and prior evidence archived.'
