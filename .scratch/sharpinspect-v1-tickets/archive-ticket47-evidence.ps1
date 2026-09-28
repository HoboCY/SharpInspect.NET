param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidationPath = Join-Path $taskRun 'validation.json'
$taskValidation = Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-47-development-validation' -or $taskValidation.result -cne 'Pass') {
    throw 'A complete canonical T47 validation pass is required.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskRun 'production-arming-consumer-evidence.json') -Raw | ConvertFrom-Json
$taskReview = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket47-corrective-review-evidence.json') -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or
    $taskConsumer.result -cne 'Pass' -or $taskConsumer.id -cne 'V147_N01' -or
    $taskReview.result -cne 'Pass' -or @($taskReview.sourceHashes).Count -eq 0) {
    throw 'Verified ABI, package consumer and reviewed corrective source evidence are required.'
}
foreach ($taskSource in $taskReview.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Reviewed corrective source changed.' }
}
$taskFullTrx = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'tests') -Filter '*.trx' -File)
if ($taskFullTrx.Count -ne 7) { throw 'Expected seven full-solution test assembly results.' }
$taskRows = @()
$taskReports = @()
$taskAllTrx = @(Get-ChildItem -LiteralPath $taskRun -Filter '*.trx' -File -Recurse)
foreach ($taskTrx in $taskAllTrx) {
    [xml]$taskXml = Get-Content -LiteralPath $taskTrx.FullName -Raw
    $taskCounters = $taskXml.SelectSingleNode('//*[local-name()="Counters"]')
    $taskResults = @($taskXml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($null -eq $taskCounters -or [int]$taskCounters.failed -ne 0 -or [int]$taskCounters.error -ne 0 -or
        [int]$taskCounters.total -ne $taskResults.Count -or
        @($taskResults | Where-Object outcome -CNE 'Passed').Count) {
        throw "Test report contains failure, skipped or incomplete results: $($taskTrx.FullName)"
    }
    $taskRelative = [IO.Path]::GetRelativePath($taskRun, $taskTrx.FullName)
    $taskReports += [ordered]@{path=$taskRelative;sha256=(Get-FileHash -LiteralPath $taskTrx.FullName).Hash;
        total=[int]$taskCounters.total;passed=[int]$taskCounters.passed}
    foreach ($taskResult in $taskResults) {
        $taskRows += [pscustomobject]@{testName=[string]$taskResult.testName;outcome=[string]$taskResult.outcome;
            storage=[string]$taskResult.relativeResultsDirectory;report=$taskRelative}
    }
}
$taskUnique = @($taskRows | Sort-Object testName -Unique)
$taskCases = @($taskUnique | Where-Object testName -Like '*V147_*')
if ($taskCases.Count -lt 82) { throw 'T47 cases are missing from the completed test reports.' }
$taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'packages') -Filter '*.nupkg' -File)
if ($taskPackages.Count -ne 8) { throw 'Eight packages are required.' }
$taskPackagesEvidence = @($taskPackages | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($taskRun,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$taskCases | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'v147-case-results.json') -Encoding utf8
$taskReports | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'test-report-hashes.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ticket47-final-review.md') -Destination $taskRun
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ticket47-corrective-review-evidence.json') -Destination $taskRun
[ordered]@{result='Pass';ticket=47;issue=48;validationRoot=$taskRun;
    validationSha256=(Get-FileHash -LiteralPath $taskValidationPath).Hash;
    sourceCount=@($taskValidation.sourceHashes).Count;fullSolutionAssemblies=$taskFullTrx.Count;
    distinctPassedCases=$taskUnique.Count;totalExecutions=$taskRows.Count;failedTests=0;
    v147CaseCount=$taskCases.Count;packageCount=$taskPackages.Count;packages=$taskPackagesEvidence;
    legacyAbi='Pass';reviewDisposition='Pass';packageConsumer='V147_N01 Pass';
    hardwareQualification='NotRun';production='NotRun';deployment='NotRun';
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRun 'ticket47-final-evidence.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRun 'ticket47-final-evidence.json')
