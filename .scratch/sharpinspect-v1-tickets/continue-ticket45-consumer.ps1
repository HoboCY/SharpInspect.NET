param([Parameter(Mandatory)][string]$FullRun, [Parameter(Mandatory)][string]$ConsumerRun,
    [Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskFullFile = Join-Path $FullRun 'validation.json'
$taskOriginal = Get-Content -LiteralPath $taskFullFile -Raw | ConvertFrom-Json
$taskConsumerFile = Join-Path $ConsumerRun 'production-recovery-demo/production-recovery-consumer-evidence.json'
$taskConsumer = Get-Content -LiteralPath $taskConsumerFile -Raw | ConvertFrom-Json
$taskRechecks = @(Get-Content -LiteralPath (Join-Path $ConsumerRun 'original-binary-rechecks.json') -Raw | ConvertFrom-Json)
if ($taskOriginal.result -cne 'Fail' -or $taskConsumer.Result -cne 'Pass' -or
    $taskRechecks.Count -ne 2 -or @($taskRechecks | Where-Object exitCode -NE 0).Count) { throw 'Required original failure and passing rechecks missing.' }
if (Test-Path -LiteralPath $Run) { throw 'Fresh continuation directory required.' }
$taskSources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskCurrent = @($taskSources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($taskCurrent | ConvertTo-Json -Depth 4 -Compress) -cne
    ($taskOriginal.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'No source change is allowed for this continuation.' }
$taskRows = @()
foreach ($taskTrxFile in Get-ChildItem -LiteralPath (Join-Path $FullRun 'tests') -Filter '*.trx' -File) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskTrxFile.FullName -Raw
    $taskCounters = $taskTrx.TestRun.ResultSummary.Counters
    if ([int]$taskCounters.failed -ne 0 -or [int]$taskCounters.notExecuted -ne 0 -or
        [int]$taskCounters.total -ne [int]$taskCounters.passed) { throw 'Original full suite must pass completely.' }
    $taskRows += [ordered]@{path=$taskTrxFile.FullName;sha256=(Get-FileHash -LiteralPath $taskTrxFile.FullName).Hash;
        passed=[int]$taskCounters.passed}
}
if ($taskRows.Count -ne 7 -or ($taskRows | ForEach-Object { $_.passed } | Measure-Object -Sum).Sum -ne 2288) { throw 'Unexpected base suite.' }
$taskOriginalHash = (Get-FileHash -LiteralPath $taskFullFile).Hash
[void][IO.Directory]::CreateDirectory($Run)
foreach ($taskDirectory in Get-ChildItem -LiteralPath $FullRun -Directory | Where-Object {
    $_.Name -notin 'production-recovery-consumer','production-recovery-consumer-cache','production-recovery-demo','private-keys' }) {
    New-Item -ItemType Junction -Path (Join-Path $Run $taskDirectory.Name) -Target $taskDirectory.FullName | Out-Null
}
foreach ($taskName in 'production-recovery-consumer','production-recovery-demo') {
    New-Item -ItemType Junction -Path (Join-Path $Run $taskName) -Target (Join-Path $ConsumerRun $taskName) | Out-Null
}
foreach ($taskFile in Get-ChildItem -LiteralPath $FullRun -File | Where-Object Name -NE 'validation.json') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $Run $taskFile.Name)
}
foreach ($taskFile in Get-ChildItem -LiteralPath $ConsumerRun -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $Run $taskFile.Name)
}
$taskEvidence = [ordered]@{}
foreach ($taskProperty in $taskOriginal.PSObject.Properties) { $taskEvidence[$taskProperty.Name] = $taskProperty.Value }
$taskEvidence.kind = 'V1-45-development-validation-consumer-continuation'
$taskEvidence.originalFullRun = [ordered]@{path=$FullRun;validationSha256=$taskOriginalHash;result='Fail'}
$taskEvidence.originalFailure = 'Last production recovery UI consumer timed out after its UI marker and before generic smoke markers. Original wait predicate and root cause were not captured. This is not attributed to Dispose.'
$taskEvidence.reusedThrough = 'V143_N01 part identity consumer; original seven test assemblies all pass.'
$taskEvidence.reusedTests = $taskRows
$taskEvidence.continuedConsumers = @(44)
$taskEvidence.consumerContinuation = [ordered]@{path=$ConsumerRun;evidenceSha256=(Get-FileHash -LiteralPath $taskConsumerFile).Hash;result='Pass';sourceUnchanged=$true}
$taskEvidence.originalBinaryRechecks = $taskRechecks
$taskEvidence.limitation = 'One intermittent smoke wait failure remains unexplained. An instrumented diagnostic run, an unchanged-source external consumer rebuild, and two executions of the exact original consumer binary passed. No timeout or production condition was relaxed.'
$taskEvidence.result = 'Pass'
$taskEvidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Run 'validation.json') -Encoding utf8
if ((Get-FileHash -LiteralPath $taskFullFile).Hash -cne $taskOriginalHash) { throw 'Original failed validation changed.' }
'T45 unchanged-source consumer continuation PASS; original failed manifest retained.'
