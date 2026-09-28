$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskRun = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/20260911-final-consumer-continuation'
$taskFixed = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/consumer-fixed-02'
$taskFixedFile = Join-Path $taskFixed 'production-recovery-demo/production-recovery-consumer-evidence.json'
$taskFixedEvidence = Get-Content -LiteralPath $taskFixedFile -Raw | ConvertFrom-Json
$taskProvenanceFile = Join-Path $taskFixed 'input-provenance.json'
$taskProvenance = Get-Content -LiteralPath $taskProvenanceFile -Raw | ConvertFrom-Json
if ($taskFixedEvidence.Result -cne 'Pass' -or -not $taskFixedEvidence.IndependentSigningKeyCreated -or
    -not $taskFixedEvidence.DisposableSigningKeyRemoved -or $taskFixedEvidence.ParentAuditKeyInputsUsed -or
    -not $taskFixedEvidence.ParentAuditKeyProvided -or -not $taskFixedEvidence.ParentAuditKeyDirectoryProvided -or
    $taskProvenance.result -cne 'Pass' -or -not $taskProvenance.parentKeyUnchanged -or
    (Get-FileHash -LiteralPath 'tools/Test-ProductionRecoveryConsumer.ps1').Hash -cne $taskProvenance.scriptSha256) {
    throw 'Final consumer and existing-parent-key preservation regression must pass.'
}
$taskManifest = Join-Path $taskRun 'validation.json'
$taskEvidence = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
if ($taskEvidence.result -cne 'NotRun') { throw 'Only the pending final gate may be completed.' }
$taskBaseFile = Join-Path $taskEvidence.consumerContinuation.intermediateRun 'validation.json'
$taskBase = Get-Content -LiteralPath $taskBaseFile -Raw | ConvertFrom-Json
if ($taskBase.result -cne 'Fail' -or (Get-FileHash -LiteralPath $taskBaseFile).Hash -cne
    $taskEvidence.consumerContinuation.intermediateValidationSha256) { throw 'Original consumer failure changed.' }
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskSources = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskBefore = @{}
foreach ($taskSource in $taskBase.sourceHashes) { $taskBefore[$taskSource.path] = $taskSource.sha256 }
if ($taskSources.Count -ne $taskBefore.Count) { throw 'Source set changed.' }
$taskDelta = @()
foreach ($taskSource in $taskSources) {
    if (-not $taskBefore.ContainsKey($taskSource.path)) { throw 'Unexpected source path.' }
    if ($taskSource.sha256 -cne $taskBefore[$taskSource.path]) {
        if ($taskSource.path.Replace('\','/') -cne 'tools/Test-ProductionRecoveryConsumer.ps1') { throw 'Unexpected product delta.' }
        $taskDelta += [ordered]@{path=$taskSource.path;before=$taskBefore[$taskSource.path];after=$taskSource.sha256}
    }
}
if ($taskDelta.Count -ne 1) { throw 'Only the reviewed consumer repair may differ.' }
foreach ($taskName in 'production-recovery-consumer','production-recovery-demo') {
    $taskLinkPath = Join-Path $taskRun $taskName
    $taskLink = Get-Item -LiteralPath $taskLinkPath
    $taskExpectedTarget = [IO.Path]::GetFullPath(('F:/temp/SharpInspect.NET-validation-artifacts/ticket46/consumer-fixed-01/' + $taskName))
    if ($taskLink.LinkType -cne 'Junction' -or [IO.Path]::GetFullPath([string]$taskLink.Target) -cne $taskExpectedTarget) {
        throw 'Only the task-owned earlier consumer junction may be replaced.'
    }
    # Non-recursive removal of the verified junction leaves its target and evidence intact.
    Remove-Item -LiteralPath $taskLinkPath
    New-Item -ItemType Junction -Path $taskLinkPath -Target (Join-Path $taskFixed $taskName) | Out-Null
}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskFixed -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRun $taskFile.Name)
}
$taskRecipe = Get-Content -LiteralPath (Join-Path $taskRun 'recipe-selection-consumer-evidence.json') -Raw | ConvertFrom-Json
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
if ($taskRecipe.result -cne 'Pass' -or $taskAbi.result -cne 'Pass') { throw 'Final remaining consumers must pass.' }
$taskReviewPath = '.scratch/sharpinspect-v1-tickets/ticket46-consumer-key-review.md'
$taskReviewReport = Get-Content -LiteralPath $taskReviewPath -Raw
if ($taskReviewReport -notmatch '10F2792586C2A92D5E67D71B2CE0209624434BF65BF6E6B9D11BF5E62B540CDD') {
    throw 'Actual completed independent review required.'
}
$taskEvidence.sourceHashes = $taskSources
$taskEvidence.consumerContinuation.sourceDelta = $taskDelta
$taskEvidence.consumerContinuation.fixedConsumer = [ordered]@{path=$taskFixedFile;sha256=(Get-FileHash -LiteralPath $taskFixedFile).Hash;result='Pass'}
$taskEvidence.consumerContinuation.review = 'Pass'
$taskEvidence.consumerContinuation | Add-Member -NotePropertyName reviewDisposition -NotePropertyValue ([ordered]@{
    report=$taskReviewPath;reportSha256=(Get-FileHash -LiteralPath $taskReviewPath).Hash;job='9922aff2cf504d63a90852496e8d654d';
    independentlyReviewedScriptSha256='10F2792586C2A92D5E67D71B2CE0209624434BF65BF6E6B9D11BF5E62B540CDD';
    finalScriptSha256=$taskProvenance.scriptSha256;inputProvenance=$taskProvenanceFile;
    inputProvenanceSha256=(Get-FileHash -LiteralPath $taskProvenanceFile).Hash;
    postReviewChanges='Primary resolved three Low findings: unused legacy key parameters are explicitly documented/reported without validating ignored paths; initial-provisioning comment is precise; cleanup failure preserves an existing primary run error. Final consumer and parent-key-preservation check pass. Later bytes were not independently re-reviewed.';
    limitation='Cleanup-failure exception precedence was code-inspected; no forced cleanup-failure test was run.'})
$taskEvidence.consumerContinuation.remainingConsumers = 'V146_N01 and unchanged old ABI Pass.'
$taskEvidence.result = 'Pass'
$taskEvidence | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
$taskEvidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskManifest -Encoding utf8
Write-Output ('T46 composite gate Pass; final source count ' + $taskSources.Count + '; original failures preserved.')
