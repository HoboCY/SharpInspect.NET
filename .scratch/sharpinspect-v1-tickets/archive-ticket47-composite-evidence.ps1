param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidationPath = Join-Path $taskRun 'validation.json'
$taskValidation = Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-47-development-validation-composite' -or $taskValidation.result -cne 'Pass') {
    throw 'Completed composite build, package and consumer gate required.'
}
$taskContext = $taskValidation.continuation
if ($taskContext.fullSuiteSingleRunPass -ne $false -or $taskContext.distinctCaseCount -ne 2469 -or
    $taskContext.original.total -ne 2469 -or $taskContext.original.failed -ne 2 -or
    @($taskContext.original.tests).Count -ne 7 -or $taskContext.followup.passed -ne 87) { throw 'Unexpected composite scope.' }
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
$taskOriginalPath = Join-Path $taskContext.original.path 'validation.json'
if ((Get-FileHash -LiteralPath $taskOriginalPath).Hash -cne $taskContext.original.validationSha256 -or
    (Get-Content -LiteralPath $taskOriginalPath -Raw | ConvertFrom-Json).result -cne 'Fail') { throw 'Original failure changed.' }
foreach ($taskPin in $taskContext.productBinaryPins) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Original product binary changed.' }
}
if (@($taskContext.productBinaryPins).Count -ne 8) { throw 'Eight identical products required.' }
$taskReview = Get-Content -LiteralPath $taskContext.review.path -Raw | ConvertFrom-Json
if ($taskReview.kind -cne 'V1-47-independent-review-disposition' -or $taskReview.result -cne 'Pass' -or
    [string]::IsNullOrWhiteSpace($taskReview.independentReviewer) -or [string]::IsNullOrWhiteSpace($taskReview.scope) -or
    @($taskReview.independentReports).Count -lt 2 -or
    (Get-FileHash -LiteralPath $taskContext.review.path).Hash -cne $taskContext.review.sha256) { throw 'Reviewed correction changed.' }
foreach ($taskReport in $taskReview.independentReports) {
    if ((Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256) { throw 'Independent report changed.' }
}
foreach ($taskPin in $taskReview.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Reviewed source changed.' }
}
$taskProof = Join-Path $taskRun 'composite-evidence'
if (Test-Path -LiteralPath $taskProof) { throw 'Do not overwrite an existing archive.' }
[void][IO.Directory]::CreateDirectory($taskProof)
$taskProofPins = @()
function Copy-TaskProof([string]$Source, [string]$Relative) {
    $taskDestination = Join-Path $taskProof $Relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
    Copy-Item -LiteralPath $Source -Destination $taskDestination
    $taskHash = (Get-FileHash -LiteralPath $Source).Hash
    if ((Get-FileHash -LiteralPath $taskDestination).Hash -cne $taskHash) { throw 'Proof copy changed.' }
    $script:taskProofPins += [ordered]@{path=$Relative;sha256=$taskHash}
}
Copy-TaskProof $taskOriginalPath 'original/validation.json'
if ((Get-FileHash -LiteralPath $taskContext.completedOriginalSummary.path).Hash -cne
    $taskContext.completedOriginalSummary.sha256) { throw 'Gate40 completion summary changed.' }
Copy-TaskProof $taskContext.completedOriginalSummary.path 'original/completed-full-run-test-summary.json'
Copy-TaskProof $taskContext.review.path 'review/review-evidence.json'
Copy-TaskProof (Join-Path $PSScriptRoot 'ticket47-final-review.md') 'review/initial-independent-review.md'
Copy-TaskProof (Join-Path $PSScriptRoot 'ticket47-r17-final-review.md') 'review/r17-final-review.md'
$taskReports = @()
$taskCases = @{}
$taskExecutionCount = 0
function Read-TaskReport([string]$Path, [string]$Stage, [bool]$AllowOriginalFailures) {
    [xml]$taskXml = Get-Content -LiteralPath $Path -Raw
    $taskRows = @($taskXml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $taskCounter = $taskXml.SelectSingleNode('//*[local-name()="Counters"]')
    if ($taskRows.Count -ne [int]$taskCounter.total -or $taskRows.Count -ne [int]$taskCounter.executed) { throw 'Incomplete TRX.' }
    foreach ($taskRow in $taskRows) {
        if ($taskRow.outcome -cne 'Passed' -and
            (-not $AllowOriginalFailures -or $taskRow.outcome -cne 'Failed' -or $taskRow.testName -notmatch '(V147_R17_|V143_R02_)')) {
            throw "Unresolved test: $($taskRow.testName)"
        }
        if ($Stage -ceq 'followup' -and -not $script:taskCases.ContainsKey($taskRow.testName)) { throw 'Follow-up introduced a different case.' }
        $script:taskCases[$taskRow.testName] = [ordered]@{testName=$taskRow.testName;outcome=$taskRow.outcome;stage=$Stage;report=$Path}
    }
    $script:taskExecutionCount += $taskRows.Count
    $script:taskReports += [ordered]@{path=$Path;sha256=(Get-FileHash -LiteralPath $Path).Hash;stage=$Stage;
        total=$taskRows.Count;failed=@($taskRows | Where-Object outcome -CNE 'Passed').Count}
}
foreach ($taskReport in $taskContext.original.tests) {
    if ((Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256) { throw 'Original TRX changed.' }
    Read-TaskReport $taskReport.path 'original' $true
    Copy-TaskProof $taskReport.path ('original/tests/' + [IO.Path]::GetFileName($taskReport.path))
}
$taskFollowup = $taskContext.followup
if ((Get-FileHash -LiteralPath $taskFollowup.path).Hash -cne $taskFollowup.sha256 -or
    (Get-FileHash -LiteralPath $taskFollowup.sourceAndBinaryEvidence).Hash -cne $taskFollowup.sourceAndBinaryEvidenceSha256) {
    throw 'Follow-up evidence changed.'
}
Copy-TaskProof $taskFollowup.path 'followup/focused45.trx'
Copy-TaskProof $taskFollowup.sourceAndBinaryEvidence 'followup/source-and-binary-evidence.json'
$taskBinding = Get-Content -LiteralPath $taskFollowup.sourceAndBinaryEvidence -Raw | ConvertFrom-Json
$taskRepair = $taskContext.consumerScriptRepair
$taskFollowupSources = @{}
foreach ($taskPin in $taskBinding.sourceHashes) { $taskFollowupSources[$taskPin.path.Replace('\','/')] = $taskPin.sha256 }
if ($taskFollowupSources.Count -ne @($taskValidation.sourceHashes).Count) { throw 'Follow-up source set mismatch.' }
foreach ($taskPin in $taskValidation.sourceHashes) {
    $taskPath = $taskPin.path.Replace('\','/')
    if (-not $taskFollowupSources.ContainsKey($taskPath)) { throw 'Follow-up source set mismatch.' }
    if ($taskPin.sha256 -cne $taskFollowupSources[$taskPath] -and
        ($taskPath -cne 'tools/Test-ProductionArmingConsumer.ps1' -or $taskPath -cne $taskRepair.path -or
            $taskPin.sha256 -cne $taskRepair.after -or $taskFollowupSources[$taskPath] -cne $taskRepair.before)) {
        throw 'Follow-up source delta is not the verified consumer import.'
    }
}
foreach ($taskPin in @($taskRepair.failedValidation,$taskRepair.fixedConsumerEvidence)) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Consumer repair evidence changed.' }
}
Copy-TaskProof $taskRepair.failedValidation.path 'consumer-repair/failed-validation.json'
Copy-TaskProof $taskRepair.fixedConsumerEvidence.path 'consumer-repair/fixed-consumer-evidence.json'
foreach ($taskPin in $taskBinding.binaries) {
    if ((Get-FileHash -LiteralPath $taskPin.source).Hash -cne $taskPin.sha256 -or
        (Get-FileHash -LiteralPath $taskPin.archive).Hash -cne $taskPin.sha256) { throw 'Follow-up binary changed.' }
    Copy-TaskProof $taskPin.archive ('followup/binaries/' + [IO.Path]::GetFileName($taskPin.archive))
}
Read-TaskReport $taskFollowup.path 'followup' $false
if ($taskCases.Count -ne 2469 -or @($taskCases.Values | Where-Object outcome -CNE 'Passed').Count) { throw 'Composite suite incomplete.' }
$taskConsumerTrx = @(Get-ChildItem -LiteralPath $taskRun -Filter '*.trx' -File -Recurse |
    Where-Object { -not $_.FullName.StartsWith($taskProof + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
if (-not $taskConsumerTrx.Count) { throw 'Actual package consumer test reports required.' }
foreach ($taskReport in $taskConsumerTrx) { Read-TaskReport $taskReport.FullName 'consumer' $false }
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskRun 'production-arming-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or
    $taskConsumer.result -cne 'Pass' -or $taskConsumer.id -cne 'V147_N01') { throw 'ABI and V147_N01 package checks required.' }
foreach ($taskChange in $taskContext.reviewedChanges) {
    $taskBefore = Join-Path (Join-Path $taskContext.original.path 'source-before-followup') $taskChange.path
    if ((Get-FileHash -LiteralPath $taskBefore).Hash -cne $taskChange.before -or
        (Get-FileHash -LiteralPath $taskChange.path).Hash -cne $taskChange.after) { throw 'Bounded correction changed.' }
    Copy-TaskProof $taskBefore ('source-before/' + $taskChange.path)
    Copy-TaskProof $taskChange.path ('source-after/' + $taskChange.path)
}
$taskAll = @($taskCases.Values | Sort-Object { $_.testName })
$taskV147 = @($taskAll | Where-Object { $_.testName -match 'V147_' })
if ($taskV147.Count -ne 84) { throw 'All 84 T47 assembly cases required.' }
$taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'packages') -Filter '*.nupkg' -File)
if ($taskPackages.Count -ne 8) { throw 'Eight packages required.' }
$taskPackagesEvidence = @($taskPackages | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($taskRun,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$taskAll | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'test-summary-composite.json') -Encoding utf8
$taskV147 | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'v147-case-results.json') -Encoding utf8
$taskReports | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'test-report-hashes.json') -Encoding utf8
$taskProofPins | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskProof 'proof-hashes.json') -Encoding utf8
[ordered]@{result='Pass';kind='V1-47-final-composite-development-validation';ticket=47;issue=48;validationRoot=$taskRun;
    validationSha256=(Get-FileHash -LiteralPath $taskValidationPath).Hash;sourceCount=@($taskValidation.sourceHashes).Count;
    fullSolutionAssemblies=7;fullSuiteSingleRunPass=$false;originalFailedTests=2;followupPassed=87;
    distinctPassedCases=$taskCases.Count;totalExecutions=$taskExecutionCount;effectiveUnresolvedCases=0;failedTests=0;
    v147CaseCount=84;packageCount=8;packages=$taskPackagesEvidence;legacyAbi='Pass';reviewDisposition='Pass';
    packageConsumer='V147_N01 Pass';hardwareQualification='NotRun';production='NotRun';deployment='NotRun';
    limitation=$taskContext.limitation;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRun 'ticket47-final-evidence.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRun 'ticket47-final-evidence.json')
