param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][int]$ExpectedCases)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidationPath = Join-Path $taskRun 'validation.json'
$taskValidation = Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-50-development-validation' -or $taskValidation.result -cne 'Pass') {
    throw 'A completed passing full gate is required.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Source drift.' }
}
$taskReviewPath = Join-Path $PSScriptRoot 'ticket50-review-disposition.json'
$taskReview = Get-Content -LiteralPath $taskReviewPath -Raw | ConvertFrom-Json
if ($taskReview.result -cne 'Pass' -or
    ($taskReview.sourceHashes | ConvertTo-Json -Compress) -cne
    ($taskValidation.sourceHashes | ConvertTo-Json -Compress)) { throw 'Review scope does not match validation.' }
foreach ($taskReport in $taskReview.independentReports) {
    if ((Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256) { throw 'Review report changed.' }
}
$taskAbiPath = Join-Path $taskRun 'abi-package-binding.json'
$taskAbi = Get-Content -LiteralPath $taskAbiPath -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or $taskAbi.consumerUnchanged -cne $true -or $taskAbi.libraries.Count -ne 4) {
    throw 'Legacy ABI evidence is incomplete.'
}
$taskConsumerPath = Join-Path $taskRun 'store-migration-consumer.json'
$taskConsumer = Get-Content -LiteralPath $taskConsumerPath -Raw | ConvertFrom-Json
if ($taskConsumer.id -cne 'V149_N01' -or $taskConsumer.result -cne 'Pass') { throw 'T49 package consumer failed.' }
if ($taskConsumer.cases.Count -ne 14 -or
    (@($taskConsumer.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
    @($taskConsumer.cases | Where-Object { $_.result -cne 'Pass' -or -not $_.oldDatabaseUnchanged }).Count -ne 0) {
    throw 'The fourteen actual process-crash and old-writer cases are incomplete.'
}
if ($taskConsumer.old.runtimeHash -cne 'E76A485D95C68BF1CA18E3702C5D3AA754C904B3747556BE27D5DDB3C00CFC13' -or
    (Get-FileHash -LiteralPath $taskConsumer.current.runtime).Hash -cne $taskConsumer.current.runtimeHash -or
    $taskConsumer.current.runtimeHash -cne (Get-FileHash -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll').Hash) {
    throw 'Migration probe runtime identity mismatch.'
}
$taskImageConsumerPath = Join-Path $taskRun 'image-evidence-migration-consumer.json'
$taskImageConsumer = Get-Content -LiteralPath $taskImageConsumerPath -Raw | ConvertFrom-Json
if ($taskImageConsumer.id -cne 'V150_N01' -or $taskImageConsumer.result -cne 'Pass' -or
    $taskImageConsumer.sourceSchemaVersion -ne 33 -or $taskImageConsumer.targetSchemaVersion -ne 34 -or
    $taskImageConsumer.cases.Count -ne 14 -or -not $taskImageConsumer.fullPhaseCoverage -or
    (@($taskImageConsumer.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
    @($taskImageConsumer.cases | Where-Object { $_.result -cne 'Pass' -or
        $_.interruptedBy -cne 'Process.Kill(entireProcessTree:true)' -or -not $_.oldDatabaseUnchanged -or
        -not $_.oldWriterAuditUnchanged -or -not $_.oldDatabaseAndWalUnchanged -or $_.oldWriterReason -cne $_.oldWriterExpectedReason -or
        $_.pendingImageWorkItems -ne 0 -or $_.sourceSchemaVersion -ne 33 -or $_.targetSchemaVersion -ne 34 }).Count) {
    throw 'Incomplete schema33-to34 process crash, resume, cold work or old-writer evidence.'
}
if ($taskImageConsumer.old.runtimeHash -cne '58C4DFED58ECEC1C9A4015C08623AB9B94D7CFB11474C0A06544D15BBF9CB94D' -or
    (Get-FileHash -LiteralPath $taskImageConsumer.current.runtime).Hash -cne $taskImageConsumer.current.runtimeHash -or
    $taskImageConsumer.current.runtimeHash -cne $taskConsumer.current.runtimeHash) {
    throw 'Image migration actual runtime identity mismatch.'
}
$taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'packages') -Filter '*.nupkg' -File)
if ($taskPackages.Count -ne 8) { throw 'Expected eight packages.' }
$taskAllTrx = @(Get-ChildItem -LiteralPath $taskRun -Filter '*.trx' -Recurse -File)
$taskSummaries = @()
$taskCases = @()
foreach ($taskFile in $taskAllTrx) {
    [xml]$taskXml = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskCounters = $taskXml.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    $taskResults = @($taskXml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $taskTotal = [int]$taskCounters.GetAttribute('total')
    $taskPassed = [int]$taskCounters.GetAttribute('passed')
    if ($taskTotal -le 0 -or $taskPassed -ne $taskTotal -or $taskResults.Count -ne $taskTotal -or
        @($taskResults | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count) {
        throw "Incomplete or failed TRX: $($taskFile.FullName)"
    }
    $taskRelative = [IO.Path]::GetRelativePath($taskRun, $taskFile.FullName)
    $taskMain = $taskRelative -match '^tests[\\/]'
    $taskSummaries += [ordered]@{path=$taskRelative;sha256=(Get-FileHash -LiteralPath $taskFile.FullName).Hash;
        scope=$(if ($taskMain) {'solution'} else {'consumer'});total=$taskTotal;passed=$taskPassed}
    if ($taskMain) {
        foreach ($taskCase in $taskResults) {
            $taskCases += [ordered]@{name=$taskCase.GetAttribute('testName');id=$taskCase.GetAttribute('testId');
                outcome=$taskCase.GetAttribute('outcome');trx=$taskRelative}
        }
    }
}
$taskNewCases = @($taskCases | Where-Object name -Match 'V150_')
if ($taskNewCases.Count -ne $ExpectedCases -or @($taskSummaries | Where-Object scope -EQ 'solution').Count -ne 7) {
    throw 'Unexpected T50 case count or missing solution test project.'
}
$taskReviewArchive = Join-Path $taskRun 'independent-review'
[void][IO.Directory]::CreateDirectory($taskReviewArchive)
Copy-Item -LiteralPath $taskReviewPath -Destination $taskReviewArchive
$taskReviewScopePath = Join-Path $PSScriptRoot 'ticket50-review-scope.json'
Copy-Item -LiteralPath $taskReviewScopePath -Destination $taskReviewArchive
foreach ($taskReport in $taskReview.independentReports) { Copy-Item -LiteralPath $taskReport.path -Destination $taskReviewArchive }
$taskEvidence = [ordered]@{
    kind='V1-50-final-evidence';result='Pass';reviewDisposition='Pass';legacyAbi='Pass';packageCount=$taskPackages.Count;
    failedTests=0;v150CaseCount=$taskNewCases.Count;solutionTestCount=$taskCases.Count;
    solutionUniqueTestCount=@($taskCases.id | Sort-Object -Unique).Count;
    consumerTrxCount=@($taskSummaries | Where-Object scope -EQ 'consumer').Count;
    consumerTestExecutions=($taskSummaries | Where-Object scope -EQ 'consumer' | ForEach-Object { [int]$_.total } | Measure-Object -Sum).Sum;
    validationSha256=(Get-FileHash -LiteralPath $taskValidationPath).Hash;
    reviewDispositionSha256=(Get-FileHash -LiteralPath $taskReviewPath).Hash;
    reviewScopeSha256=(Get-FileHash -LiteralPath $taskReviewScopePath).Hash;
    abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash;
    packageConsumerSha256=(Get-FileHash -LiteralPath $taskConsumerPath).Hash;
    imagePackageConsumerSha256=(Get-FileHash -LiteralPath $taskImageConsumerPath).Hash;
    imageMigrationCrashPhases=@($taskImageConsumer.cases.phase);imageMigrationOldRuntimeSha256=$taskImageConsumer.old.runtimeHash;
    startingHead=$taskValidation.startingHead;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    packages=@($taskPackages | ForEach-Object { [ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} });
    testRuns=$taskSummaries;newCases=$taskNewCases;
    migrationCrashPhases=@($taskConsumer.cases.phase);migrationOldRuntimeSha256=$taskConsumer.old.runtimeHash;
    notRun=@('RealCamera','RealPLC','SafetyCircuit','ProductionDeployment','StationQualification','LongTermCapacity','PowerLoss')
}
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'ticket50-final-evidence.json') -Encoding utf8
[pscustomobject]$taskEvidence | Select-Object kind,result,solutionTestCount,solutionUniqueTestCount,v150CaseCount,consumerTrxCount,consumerTestExecutions,packageCount,legacyAbi,reviewDisposition | ConvertTo-Json


