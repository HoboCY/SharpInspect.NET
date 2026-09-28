param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($Run)
$taskValidationPath=Join-Path $taskRoot 'validation.json'
$taskValidation=Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-52-development-validation' -or
    $taskValidation.executionMode -cne 'BoundedProbeContinuation' -or
    $taskValidation.result -cne 'NotRun' -or -not $taskValidation.freshOutboxMigrationsCompleted) {
    throw 'Completed fresh migration paths and an unfinished continuation are required.'
}
$taskProvenancePath=Join-Path $taskRoot 'continuation-provenance.json'
if ((Get-FileHash -LiteralPath $taskProvenancePath).Hash -cne $taskValidation.provenanceSha256) { throw 'Provenance changed.' }
$taskProvenance=Get-Content -LiteralPath $taskProvenancePath -Raw | ConvertFrom-Json
$taskParentValidationPath=Join-Path $taskProvenance.parentRun 'validation.json'
if ((Get-FileHash -LiteralPath $taskParentValidationPath).Hash -cne $taskProvenance.parentValidationSha256) {
    throw 'The original failed validation changed.'
}
$taskParentValidation=Get-Content -LiteralPath $taskParentValidationPath -Raw | ConvertFrom-Json
if ($taskParentValidation.result -cne 'Fail' -or $taskValidation.reusedFrom -cne $taskProvenance.parentRun) { throw 'Parent linkage differs.' }
if ($taskValidation.startingHead -cne (git rev-parse HEAD).Trim()) { throw 'HEAD changed.' }
$taskFiles=@(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File)+
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskSources=@($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath((Get-Location).Path,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($taskSources | ConvertTo-Json -Compress) -cne ($taskValidation.sourceHashes | ConvertTo-Json -Compress)) {
    throw 'Continuation source set or content changed.'
}
$taskAllowed=@('tools\outbox-migration-probe\Program.cs','tools\Test-OutboxMigrationConsumer.ps1')
if (($taskParentValidation.sourceHashes.path -join "`n") -cne ($taskSources.path -join "`n")) { throw 'Parent source set differs.' }
for($taskIndex=0;$taskIndex -lt $taskSources.Count;$taskIndex++) {
    if($taskSources[$taskIndex].sha256 -cne $taskParentValidation.sourceHashes[$taskIndex].sha256 -and
        $taskSources[$taskIndex].path -cnotin $taskAllowed) { throw 'Reused product dependency changed.' }
}
foreach($taskArtifact in $taskProvenance.reusedArtifacts) {
    if((Get-FileHash -LiteralPath $taskArtifact.source).Hash -cne $taskArtifact.sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $taskRoot $taskArtifact.destination)).Hash -cne $taskArtifact.sha256) {
        throw ('Reused artifact changed: '+$taskArtifact.destination)
    }
}
foreach($taskArtifact in $taskProvenance.referencedParentEvidence) {
    if((Get-FileHash -LiteralPath $taskArtifact.path).Hash -cne $taskArtifact.sha256) {
        throw ('Referenced original evidence changed: '+$taskArtifact.path)
    }
}
$taskRuntimeHash=(Get-FileHash -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll').Hash
if($taskRuntimeHash -cne '7985DBBA156059E1D1FD17BF0B581BF4AB9F26C638841A88D36B9DCDEF351C24') { throw 'Product runtime changed.' }
foreach($taskSchema in 32,33,34,35) {
    $taskPath=Join-Path $taskRoot ('outbox-migration-'+$taskSchema+'-consumer.json')
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or -not $taskReport.fullPhaseCoverage -or $taskReport.cases.Count -ne 14 -or
        (@($taskReport.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
        $taskReport.current.runtimeHash -cne $taskRuntimeHash -or
        (Get-FileHash -LiteralPath $taskReport.current.runtime).Hash -cne $taskRuntimeHash -or
        @($taskReport.cases | Where-Object {
            $_.result -cne 'Pass' -or $_.seedPostWriteAuditSequence -le 0 -or
            $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence -or $_.currentPostWriteAuditSequence -le 0 -or
            $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence -or -not $_.oldDatabaseAndWalUnchanged
        }).Count) { throw ('Fresh migration evidence incomplete: '+$taskSchema) }
}
$taskAbi=Get-Content -LiteralPath (Join-Path $taskRoot 'abi-package-binding.json') -Raw | ConvertFrom-Json
if($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or $taskAbi.libraries.Count -ne 4) { throw 'Legacy ABI missing.' }
$taskValidation.result='Pass'
$taskValidation | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O'))
$taskValidation | Add-Member -NotePropertyName evidenceIntegrity -NotePropertyValue 'All copied and referenced original evidence hashes rechecked; all four changed-probe paths freshly passed.'
$taskValidation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskValidationPath -Encoding utf8
'Bounded continuation PASS; original full invocation remains Fail with immutable linked evidence.'
