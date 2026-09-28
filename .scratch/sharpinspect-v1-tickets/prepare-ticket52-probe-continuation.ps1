param([Parameter(Mandatory)][string]$ParentRun)
$ErrorActionPreference = 'Stop'
$taskParent = [IO.Path]::GetFullPath($ParentRun)
$taskParentValidationPath = Join-Path $taskParent 'validation.json'
$taskParentValidation = Get-Content -LiteralPath $taskParentValidationPath -Raw | ConvertFrom-Json
if ($taskParentValidation.kind -cne 'V1-52-development-validation' -or $taskParentValidation.result -cne 'Fail' -or
    $taskParentValidation.startingHead -cne (git rev-parse HEAD).Trim()) { throw 'Unexpected parent validation.' }
$taskAllowed = @('tools\outbox-migration-probe\Program.cs','tools\Test-OutboxMigrationConsumer.ps1')
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskSources = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath((Get-Location).Path,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($taskSources.path -join "`n") -cne ($taskParentValidation.sourceHashes.path -join "`n")) { throw 'Source path set changed.' }
$taskChanges = @()
for ($taskIndex=0; $taskIndex -lt $taskSources.Count; $taskIndex++) {
    if ($taskSources[$taskIndex].sha256 -cne $taskParentValidation.sourceHashes[$taskIndex].sha256) {
        if ($taskSources[$taskIndex].path -cnotin $taskAllowed) { throw 'A non-probe dependency changed; reuse forbidden.' }
        $taskChanges += [ordered]@{path=$taskSources[$taskIndex].path;before=$taskParentValidation.sourceHashes[$taskIndex].sha256;
            after=$taskSources[$taskIndex].sha256}
    }
}
if ($taskChanges.Count -ne 2) { throw 'Expected exactly the two reviewed probe changes.' }
$taskTrx = @(Get-ChildItem -LiteralPath $taskParent -Filter '*.trx' -Recurse -File)
$taskSolutionCases=0; $taskConsumerCases=0; $taskSolutionFiles=0; $taskConsumerFiles=0; $taskNewCases=0
foreach ($taskFile in $taskTrx) {
    [xml]$taskXml=Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskResults=@($taskXml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $taskCounters=$taskXml.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if (-not $taskResults.Count -or [int]$taskCounters.total -ne $taskResults.Count -or
        [int]$taskCounters.passed -ne $taskResults.Count -or @($taskResults | Where-Object outcome -CNE 'Passed').Count) {
        throw 'Parent contains incomplete or failed tests.'
    }
    if ([IO.Path]::GetRelativePath($taskParent,$taskFile.FullName) -match '^tests[\\/]') {
        $taskSolutionFiles++; $taskSolutionCases+=$taskResults.Count
        $taskNewCases+=@($taskResults | Where-Object testName -Match 'V152_').Count
    } else { $taskConsumerFiles++; $taskConsumerCases+=$taskResults.Count }
}
if ($taskSolutionFiles -ne 7 -or $taskSolutionCases -ne 2924 -or $taskNewCases -ne 77 -or
    $taskConsumerFiles -ne 15 -or $taskConsumerCases -ne 25) { throw 'Parent solution/consumer coverage differs.' }
$taskPackages=@(Get-ChildItem -LiteralPath (Join-Path $taskParent 'packages') -Filter '*.nupkg' -File)
if ($taskPackages.Count -ne 8) { throw 'Parent packages incomplete.' }
foreach ($taskName in 'store-migration-consumer.json','image-evidence-migration-consumer.json','image-finalization-migration-consumer.json') {
    $taskMigration=Get-Content -LiteralPath (Join-Path $taskParent $taskName) -Raw | ConvertFrom-Json
    if ($taskMigration.result -cne 'Pass' -or $taskMigration.cases.Count -ne 14 -or
        @($taskMigration.cases | Where-Object result -CNE 'Pass').Count) { throw 'A reused old migration did not pass.' }
}
$taskRoot=Join-Path (Split-Path -Parent $taskParent) (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
if (Test-Path -LiteralPath $taskRoot) { throw 'Fresh continuation root required.' }
[void][IO.Directory]::CreateDirectory($taskRoot)
$taskEvidenceFiles=@(Get-ChildItem -LiteralPath $taskParent -File | Where-Object {
    $_.Name -cne 'validation.json' -and $_.Name -notmatch '^outbox-migration-' }) + $taskTrx + $taskPackages
$taskReused=@()
foreach ($taskFile in $taskEvidenceFiles) {
    $taskRelative=[IO.Path]::GetRelativePath($taskParent,$taskFile.FullName)
    $taskDestination=Join-Path $taskRoot $taskRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
    $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName).Hash
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    if ((Get-FileHash -LiteralPath $taskDestination).Hash -cne $taskHash) { throw 'Reused artifact copy mismatch.' }
    $taskReused += [ordered]@{source=$taskFile.FullName;destination=$taskRelative;sha256=$taskHash}
}
$taskReferences=@(Get-ChildItem -LiteralPath $taskParent -Recurse -File | Where-Object {
    $taskRelative=[IO.Path]::GetRelativePath($taskParent,$_.FullName)
    $taskRelative -notmatch '(^|\\)(obj|cache|[^\\]+-cache|PRIVATE-KEYS|keys|audit-keys)(\\|$)' -and
        $_.Extension -notin '.key','.pfx','.p12'
} | ForEach-Object {
    [ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;bytes=$_.Length}
})
$taskProvenance=[ordered]@{
    kind='V1-52-probe-continuation-provenance';parentRun=$taskParent;parentResult='Fail';
    parentValidationSha256=(Get-FileHash -LiteralPath $taskParentValidationPath).Hash;
    reason='Only the independent Outbox migration probe sampled post-command Verifying too early; source35 phase5 seed failed before migration.';
    allowedSourceChanges=$taskChanges;reusedArtifacts=$taskReused;referencedParentEvidence=$taskReferences;
    reusedScope='2924 solution tests, 25 consumer test executions, prior public consumer outputs, 8 packages, and 3 earlier migration paths. All src/tests/samples/build inputs and their other tool dependencies are byte-identical.';
    freshScope='Recompile both preserved/current package probes and execute all 14 phases for each 32/33/34/35-to36 path using post-command audit-watermark checks; legacy ABI and final source/package binding.';
    limit='This is a failed full invocation followed by a bounded continuation with explicit evidence reuse, not one uninterrupted passing invocation.';
    createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
}
$taskProvenancePath=Join-Path $taskRoot 'continuation-provenance.json'
$taskProvenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskProvenancePath -Encoding utf8
$taskValidation=[ordered]@{
    kind='V1-52-development-validation';result='NotRun';executionMode='BoundedProbeContinuation';
    startedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');startingHead=$taskParentValidation.startingHead;
    os=$taskParentValidation.os;sdk=$taskParentValidation.sdk;runtimes=$taskParentValidation.runtimes;
    sourceHashes=$taskSources;reusedFrom=$taskParent;reusedParentResult='Fail';
    provenanceSha256=(Get-FileHash -LiteralPath $taskProvenancePath).Hash
}
$taskValidation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRoot 'validation.json') -Encoding utf8
$taskRoot | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket52-current-full-run.txt')
$taskRoot
