param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidationPath = Join-Path $taskRun 'validation.json'
$taskValidation = Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-48-development-validation' -or $taskValidation.result -cne 'Pass') {
    throw 'A completed passing full gate is required.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Source drift.' }
}
$taskReviewPath = Join-Path $PSScriptRoot 'ticket48-review-disposition.json'
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
$taskConsumerPath = Join-Path $taskRun 'recipe-lifecycle-consumer-evidence.json'
$taskConsumer = Get-Content -LiteralPath $taskConsumerPath -Raw | ConvertFrom-Json
if ($taskConsumer.id -cne 'V148_N01' -or $taskConsumer.result -cne 'Pass') { throw 'T48 package consumer failed.' }
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
$taskNewCases = @($taskCases | Where-Object name -Match 'V148_')
if ($taskNewCases.Count -ne 71 -or @($taskSummaries | Where-Object scope -EQ 'solution').Count -ne 7) {
    throw 'Unexpected T48 case count or missing solution test project.'
}
$taskReviewArchive = Join-Path $taskRun 'independent-review'
[void][IO.Directory]::CreateDirectory($taskReviewArchive)
Copy-Item -LiteralPath $taskReviewPath -Destination $taskReviewArchive
foreach ($taskReport in $taskReview.independentReports) { Copy-Item -LiteralPath $taskReport.path -Destination $taskReviewArchive }
$taskEvidence = [ordered]@{
    kind='V1-48-final-evidence';result='Pass';reviewDisposition='Pass';legacyAbi='Pass';packageCount=$taskPackages.Count;
    failedTests=0;v148CaseCount=$taskNewCases.Count;solutionTestCount=$taskCases.Count;
    solutionUniqueTestCount=@($taskCases.id | Sort-Object -Unique).Count;
    consumerTrxCount=@($taskSummaries | Where-Object scope -EQ 'consumer').Count;
    consumerTestExecutions=($taskSummaries | Where-Object scope -EQ 'consumer' | ForEach-Object { [int]$_.total } | Measure-Object -Sum).Sum;
    validationSha256=(Get-FileHash -LiteralPath $taskValidationPath).Hash;
    reviewDispositionSha256=(Get-FileHash -LiteralPath $taskReviewPath).Hash;
    abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash;
    packageConsumerSha256=(Get-FileHash -LiteralPath $taskConsumerPath).Hash;
    startingHead=$taskValidation.startingHead;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    packages=@($taskPackages | ForEach-Object { [ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} });
    testRuns=$taskSummaries;newCases=$taskNewCases;
    notRun=@('RealCamera','RealPLC','SafetyCircuit','ProductionDeployment','StationQualification','LongTermCapacity','PowerLoss')
}
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'ticket48-final-evidence.json') -Encoding utf8
[pscustomobject]$taskEvidence | Select-Object kind,result,solutionTestCount,solutionUniqueTestCount,v148CaseCount,consumerTrxCount,consumerTestExecutions,packageCount,legacyAbi,reviewDisposition | ConvertTo-Json
