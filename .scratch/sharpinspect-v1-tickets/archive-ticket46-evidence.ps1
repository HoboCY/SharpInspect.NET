param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskRepo = (Get-Location).Path
$taskGateFile = Join-Path $taskRun 'validation.json'
$taskGate = Get-Content -LiteralPath $taskGateFile -Raw | ConvertFrom-Json
if ($taskGate.result -cne 'Pass' -or $taskGate.kind -cne 'V1-46-development-validation-composite') {
    throw 'Completed composite build and consumer gate required.'
}
$taskConsumerFix = $taskGate.consumerContinuation
if ($taskConsumerFix.review -cne 'Pass' -or
    (Get-FileHash -LiteralPath (Join-Path $taskConsumerFix.intermediateRun 'validation.json')).Hash -cne $taskConsumerFix.intermediateValidationSha256) {
    throw 'Consumer fixture review and preserved intermediate failure are required.'
}
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskRun 'recipe-selection-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or $taskConsumer.result -cne 'Pass' -or $taskConsumer.id -cne 'V146_N01') {
    throw 'Package and unchanged old ABI consumers must pass.'
}
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskSources = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($taskSources | ConvertTo-Json -Depth 4 -Compress) -cne
    ($taskGate.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after consumer validation.' }
$taskOriginalRun = $taskGate.continuation.original.path
$taskOriginal = Get-Content -LiteralPath (Join-Path $taskOriginalRun 'validation.json') -Raw | ConvertFrom-Json
if ($taskOriginal.result -cne 'Fail' -or
    (Get-FileHash -LiteralPath (Join-Path $taskOriginalRun 'validation.json')).Hash -cne $taskGate.continuation.original.validationSha256) {
    throw 'Preserved original failure changed.'
}
$taskProof = Join-Path $taskRun 'composite-evidence'
if (Test-Path -LiteralPath $taskProof) { throw 'Archive already exists; do not overwrite.' }
[void][IO.Directory]::CreateDirectory($taskProof)
function Copy-TaskProof([string]$Source, [string]$RelativePath) {
    $taskDestination = Join-Path $taskProof $RelativePath
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
    Copy-Item -LiteralPath $Source -Destination $taskDestination
    if ((Get-FileHash -LiteralPath $Source).Hash -cne (Get-FileHash -LiteralPath $taskDestination).Hash) {
        throw 'Evidence copy changed.'
    }
}
foreach ($taskName in 'validation.json','test-summary.json','v146-case-results.json','original-tested-binary-hashes.json') {
    Copy-TaskProof (Join-Path $taskOriginalRun $taskName) ('original/' + $taskName)
}
$taskOriginalBinaries = Get-Content -LiteralPath (Join-Path $taskOriginalRun 'original-tested-binary-hashes.json') -Raw | ConvertFrom-Json
if (@($taskOriginalBinaries).Count -ne 15) { throw 'Original binary archive incomplete.' }
foreach ($taskBinary in $taskOriginalBinaries) {
    $taskPath = Join-Path $taskOriginalRun $taskBinary.archive
    if ((Get-FileHash -LiteralPath $taskPath).Hash -cne $taskBinary.sha256) { throw 'Original binary archive changed.' }
    Copy-TaskProof $taskPath ('original/binaries/' + [IO.Path]::GetFileName($taskPath))
}
$taskFinalCases = @{}
$taskOldE10 = 'SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests.V146_E10_PublishedUnacknowledgedResponseSurvivesRestartWithoutAutomaticRecovery'
foreach ($taskTrxRef in $taskGate.continuation.original.tests) {
    if ((Get-FileHash -LiteralPath $taskTrxRef.path).Hash -cne $taskTrxRef.sha256) { throw 'Original TRX changed.' }
    Copy-TaskProof $taskTrxRef.path ('original/tests/' + [IO.Path]::GetFileName($taskTrxRef.path))
    [xml]$taskTrx = Get-Content -LiteralPath $taskTrxRef.path -Raw
    foreach ($taskCase in @($taskTrx.TestRun.Results.UnitTestResult)) {
        $taskName = if ($taskCase.testName -ceq $taskOldE10) { $taskOldE10 + '(onlyDedicatedResponseRetained: False)' } else { $taskCase.testName }
        $taskFinalCases[$taskName] = [ordered]@{testName=$taskName;outcome=$taskCase.outcome;source='original';originalName=$taskCase.testName}
    }
}
$taskFollowup = $taskGate.continuation.followup
if ((Get-FileHash -LiteralPath $taskFollowup.path).Hash -cne $taskFollowup.sha256 -or
    (Get-FileHash -LiteralPath $taskFollowup.sourceAndBinaryEvidence).Hash -cne $taskFollowup.sourceAndBinaryEvidenceSha256) {
    throw 'Final follow-up evidence changed.'
}
Copy-TaskProof $taskFollowup.path ('followup/' + [IO.Path]::GetFileName($taskFollowup.path))
Copy-TaskProof $taskFollowup.sourceAndBinaryEvidence 'followup/source-and-binary-evidence.json'
$taskFollowupEvidence = Get-Content -LiteralPath $taskFollowup.sourceAndBinaryEvidence -Raw | ConvertFrom-Json
foreach ($taskBinary in $taskFollowupEvidence.binaries) {
    if ((Get-FileHash -LiteralPath $taskBinary.archive).Hash -cne $taskBinary.sha256) { throw 'Follow-up binary archive changed.' }
    Copy-TaskProof $taskBinary.archive ('followup/binaries/' + [IO.Path]::GetFileName($taskBinary.archive))
}
[xml]$taskTrx = Get-Content -LiteralPath $taskFollowup.path -Raw
$taskCases = @($taskTrx.TestRun.Results.UnitTestResult)
foreach ($taskCase in $taskCases) {
    $taskFinalCases[$taskCase.testName] = [ordered]@{testName=$taskCase.testName;outcome=$taskCase.outcome;source='followup';originalName=$taskCase.testName}
}
$taskFinalRows = @($taskFinalCases.Values | Sort-Object { $_.testName })
if ($taskFinalRows.Count -ne 2385 -or @($taskFinalRows | Where-Object outcome -CNE 'Passed').Count) {
    throw 'Composite distinct checks are not complete.'
}
$taskFinalRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'test-summary-composite.json') -Encoding utf8
$taskRecipeCases = @($taskCases | Where-Object testName -Match 'V146_' | Sort-Object testName | ForEach-Object {
    [ordered]@{testName=$_.testName;outcome=$_.outcome;duration=$_.duration;sourceTrx=[IO.Path]::GetFileName($taskFollowup.path)}
})
if ($taskRecipeCases.Count -ne 95) { throw 'T46 stable case set is incomplete.' }
[ordered]@{result='Pass';assemblyCaseCount=95;cases=$taskRecipeCases;packageConsumer='V146_N01';
    limitation='The 95 assembly instances exclude the separately executed package consumer. Real device acceptance is NOT_RUN.'} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRun 'v146-case-results.json') -Encoding utf8
$taskReviewPath = $taskGate.continuation.review.path
if ((Get-FileHash -LiteralPath $taskReviewPath).Hash -cne $taskGate.continuation.review.sha256) { throw 'Review evidence changed.' }
Copy-TaskProof $taskReviewPath 'review/review-evidence.json'
$taskReview = Get-Content -LiteralPath $taskReviewPath -Raw | ConvertFrom-Json
foreach ($taskReport in $taskReview.independentReviews) {
    if ((Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256) { throw 'Independent review changed.' }
    Copy-TaskProof $taskReport.path ('review/' + [IO.Path]::GetFileName($taskReport.path))
}
Copy-TaskProof '.scratch/sharpinspect-v1-tickets/ticket46-final-review-disposition.md' 'review/final-review-disposition.md'
Copy-TaskProof '.scratch/sharpinspect-v1-tickets/ticket46-consumer-diagnosis.md' 'review/consumer-diagnosis.md'
Copy-TaskProof '.scratch/sharpinspect-v1-tickets/ticket46-consumer-key-review.md' 'review/consumer-key-review.md'
Copy-TaskProof (Join-Path $taskConsumerFix.intermediateRun 'validation.json') 'consumer-fix/prior-validation.json'
Copy-TaskProof (Join-Path $taskConsumerFix.intermediateRun 'production-recovery-consumer-run.log') 'consumer-fix/prior-failed-run.log'
Copy-TaskProof $taskConsumerFix.fixedConsumer.path 'consumer-fix/fixed-consumer.json'
Copy-TaskProof $taskConsumerFix.originalConsumerBinary.path 'consumer-fix/original-binary-fixed-input.json'
Copy-TaskProof 'tools/Test-ProductionRecoveryConsumer.ps1' 'consumer-fix/Test-ProductionRecoveryConsumer.ps1'
foreach ($taskName in 'key-reuse-probes.json','probe-03.log','probe-04.log') {
    Copy-TaskProof ('F:/temp/SharpInspect.NET-validation-artifacts/ticket46/consumer-diagnostic-01/' + $taskName) ('consumer-fix/' + $taskName)
}
$taskDriverRoot = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/continuation-driver-20260911-231805-708'
$taskDriverPin = Get-Content -LiteralPath (Join-Path $taskDriverRoot 'driver-provenance.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath 'tools/Test-Development.ps1').Hash -cne $taskDriverPin.originalScriptSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $taskDriverRoot 'Test-Development-Continuation.ps1')).Hash -cne $taskDriverPin.generatedScriptSha256) {
    throw 'Continuation driver provenance changed.'
}
foreach ($taskName in 'driver-provenance.json','continuation-context.json','Test-Development-Continuation.ps1') {
    Copy-TaskProof (Join-Path $taskDriverRoot $taskName) ('driver/' + $taskName)
}
foreach ($taskChange in $taskGate.continuation.reviewedChanges) {
    $taskBefore = Join-Path (Join-Path $taskOriginalRun 'source-before-followup') $taskChange.path
    if ((Get-FileHash -LiteralPath $taskBefore).Hash -cne $taskChange.before -or
        (Get-FileHash -LiteralPath $taskChange.path).Hash -cne $taskChange.after) { throw 'Reviewed source delta changed.' }
    Copy-TaskProof $taskBefore ('source-before-followup/' + $taskChange.path)
    Copy-TaskProof $taskChange.path ('source-after-followup/' + $taskChange.path)
}
$taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'packages') -Filter '*.nupkg' -File)
if ($taskPackages.Count -ne 8) { throw 'Expected eight packages.' }
[ordered]@{kind='V1-46-final-composite-development-validation';result='Pass';sourceHashes=$taskSources;
    baseValidation=[ordered]@{path='validation.json';sha256=(Get-FileHash -LiteralPath $taskGateFile).Hash};
    originalFullRun=[ordered]@{path=$taskOriginalRun;result='Fail';total=2382;passed=2381;failed=1};
    followup=[ordered]@{path=$taskFollowup.path;passed=104;v146Passed=95};distinctPassedCases=$taskFinalRows.Count;
    packages=8;packageConsumers='Pass';legacyAbi='Pass';review='Pass';sourceDrift=0;consumerFixtureRepair=$taskConsumerFix;
    limitation='Composite evidence reuses unaffected original tests and replaces affected cases with the final 104-check run. No single-run full-suite pass, automatic retained-response recovery, or real device qualification is claimed.'} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Encoding utf8
$taskArtifacts = @($taskPackages) + @(Get-ChildItem -LiteralPath $taskRun -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\(bin|obj|audit-keys)\\|cache\\|packages\\[^\\]+\\' -and
    $_.Extension -in '.json','.trx','.log','.dll','.nupkg','.png','.md','.cs','.ps1' -and $_.Name -ne 'artifact-hashes.json'
})
$taskArtifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($taskRun,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRun 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sources=$taskSources.Count;distinctPassedCases=$taskFinalRows.Count;v146=95;packages=8;artifactHashes=$taskArtifacts.Count} | ConvertTo-Json
