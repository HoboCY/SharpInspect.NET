param([Parameter(Mandatory)][string]$Run, [int]$ExpectedCases = 67)
$ErrorActionPreference = 'Stop'
$taskValidation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Full development gate must pass before archival.' }
$taskOriginalRun = if ($taskValidation.resumedFrom) { $taskValidation.resumedFrom } else { $Run }
$taskCases = @()
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskTrxHash = (Get-FileHash -LiteralPath $taskFile.FullName).Hash
    foreach ($taskCase in $taskTrx.TestRun.Results.UnitTestResult) {
        if ($taskCase.testName -match '(V144_[A-Z]+\d+[A-Z]?)') {
            $taskCases += [ordered]@{id=$Matches[1];test=$taskCase.testName;outcome=$taskCase.outcome;
                duration=$taskCase.duration;trx=$taskFile.Name;trxSha256=$taskTrxHash}
        }
    }
}
if ($taskCases.Count -ne $ExpectedCases -or @($taskCases | Where-Object outcome -CNE 'Passed').Count) {
    throw "Expected $ExpectedCases passing T44 assembly cases; found $($taskCases.Count)."
}
$taskConsumerFile = 'production-recovery-demo/production-recovery-consumer-evidence.json'
$taskConsumer = Get-Content -LiteralPath (Join-Path $Run $taskConsumerFile) -Raw | ConvertFrom-Json
if ($taskConsumer.Result -cne 'Pass') { throw 'Independent recovery consumer did not pass.' }
[ordered]@{result='Pass';assemblyCaseCount=$taskCases.Count;cases=@($taskCases | Sort-Object id,test);
    consumer=[ordered]@{id='V144-N01';result='Pass';file=$taskConsumerFile};
    notRun=@('Real PLC and camera qualification','Physical safety source qualification',
        'Production Arm and physical disposition','Physical power-loss qualification','Production performance and storage exhaustion')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Run 'v144-case-results.json') -Encoding utf8

$taskPrior = Join-Path $Run 'prior-run-evidence'
[void][IO.Directory]::CreateDirectory($taskPrior)
$taskDevelopment = Join-Path (Split-Path -Parent (Split-Path -Parent $taskOriginalRun)) 'development'
foreach ($taskFile in Get-ChildItem -LiteralPath $taskDevelopment -File -Recurse | Where-Object Extension -In '.log','.trx','.json') {
    $taskDestination = Join-Path $taskPrior ([IO.Path]::GetRelativePath($taskDevelopment,$taskFile.FullName))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
foreach ($taskPrevious in Get-ChildItem -LiteralPath (Split-Path -Parent $taskOriginalRun) -Directory | Where-Object FullName -NE $Run) {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskPrevious.FullName -File | Where-Object Extension -In '.json','.log') {
        $taskDestination = Join-Path $taskPrior ($taskPrevious.Name + '/' + $taskFile.Name)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
$taskProcessEvidence = Join-Path (Split-Path -Parent (Split-Path -Parent $taskOriginalRun)) 'temp'
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskProcessEvidence -Directory -Filter 'v142-retirement-*') {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory.FullName -File | Where-Object Extension -In '.json','.log','.trx') {
        $taskDestination = Join-Path $taskPrior ($taskDirectory.Name + '/' + $taskFile.Name)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
foreach ($taskLog in Get-ChildItem -LiteralPath (Split-Path -Parent $Run) -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskLog.FullName -Destination (Join-Path $taskPrior $taskLog.Name)
}
foreach ($taskFailedRun in @(
    'E:\temp\SharpInspect.NET-validation-artifacts\ticket44\20260911-175200-continuation',
    'F:\temp\SharpInspect.NET-validation-artifacts\ticket44\20260911-final-continuation',
    'F:\temp\SharpInspect.NET-validation-artifacts\ticket44\20260911-status-diagnostic')) {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFailedRun -File | Where-Object Extension -In '.json','.log') {
        $taskDestination = Join-Path $taskPrior ((Split-Path -Leaf $taskFailedRun) + '/' + $taskFile.Name)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
[ordered]@{independentReview='Unavailable';jobId='dd51edd94c6148dab8d2d82967993014';
    runnerError='WinError 2 before review execution';nativeReviewerSubstitution=$false;
    primaryReview='Completed';resolved=@('Legacy event hash and schema compatibility','WPF read-only Run bindings',
        'Stale rejection versus physical failure audit replay','Physical versus SQLite commit authority fences');
    unresolvedPrimaryFindings=@()} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $Run 'review-evidence.json') -Encoding utf8
'T44 stable cases, prior failures and review scope archived.'
