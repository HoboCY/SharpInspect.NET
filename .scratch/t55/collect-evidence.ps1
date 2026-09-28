param([Parameter(Mandatory)][string]$Run, [string]$AbiRun='F:\si55abi', [string]$ContinuationRun='F:\si55mfinal',
    [string]$RemainingRun='F:\si55m38final')
$ErrorActionPreference='Stop'
$taskManifestPath=Join-Path $Run 'validation.json'
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if($taskManifest.result -cne 'Fail' -or $taskManifest.kind -cne 'V1-55-development-validation') {throw 'Original full-run failure must remain recorded.'}
$taskContinuationPath=Join-Path $RemainingRun 'continuation-manifest.json'
$taskContinuation=Get-Content -LiteralPath $taskContinuationPath -Raw | ConvertFrom-Json
if($taskContinuation.result -cne 'Pass' -or $taskContinuation.kind -cne 'V1-55-bounded-migration-continuation' -or
    $taskContinuation.originalRun -cne $Run -or $taskContinuation.originalResult -cne 'Fail' -or
    $taskContinuation.originalManifestSha256 -cne (Get-FileHash -LiteralPath $taskManifestPath).Hash -or
    ($taskContinuation.completedSchemas -join ',') -cne '35,36,37,38' -or $taskContinuation.sourceChanges.Count -ne 2 -or
    @($taskContinuation.sourceChanges | Where-Object {
        $_.path -cnotin 'tools\evidence-reconciliation-migration-probe\Program.cs','tools\Test-EvidenceReconciliationMigrationConsumer.ps1'
    }).Count) {throw 'Bounded probe repair evidence incomplete.'}
if((Get-FileHash -LiteralPath $taskContinuation.interruptedManifest).Hash -cne $taskContinuation.interruptedManifestSha256 -or
    (Get-FileHash -LiteralPath $taskContinuation.interruptionEvidence).Hash -cne $taskContinuation.interruptionEvidenceSha256) {
    throw 'Interrupted continuation evidence changed.'
}
foreach($taskReport in $taskContinuation.reusedMigrationReports){
    if((Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256){throw 'Reused migration report changed.'}
}
foreach($taskSource in $taskManifest.sourceHashes) {
    $taskExpected=$taskSource.sha256
    $taskChange=@($taskContinuation.sourceChanges | Where-Object path -ceq $taskSource.path)
    if($taskChange.Count) {
        if($taskChange.Count -ne 1 -or $taskChange[0].before -cne $taskSource.sha256) {throw 'Probe source baseline differs.'}
        $taskExpected=$taskChange[0].after
    }
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskExpected) {throw ('Validated source changed: '+$taskSource.path)}
}
foreach($taskSource in $taskContinuation.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) {throw 'Continuation source changed.'}
}
$taskMigrationFailurePath='.scratch/t55/migration-failure-53.json'
$taskMigrationFailure=Get-Content -LiteralPath $taskMigrationFailurePath -Raw | ConvertFrom-Json
foreach($taskFile in $taskMigrationFailure.files) {
    if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Original failed migration evidence changed.'}
}
$taskSuites=@(); $taskCases=@(); $taskConsumerCount=0
$taskTestRoots=@('tests','identity-consumer-tests','draft-consumer-tests','recipe-release-consumer-tests',
    'camera-setup-consumer-tests','camera-recovery-consumer-tests','camera-network-consumer-tests','imaging-calibration-consumer-tests',
    'calibration-consumer-tests','plc-result-contract-consumer-tests','recipe-activation-consumer-tests',
    'preview-consumer-tests','calibration-import-consumer-tests','manual-consumer-tests',
    'production-admission-consumer-tests','trace-storage-policy-consumer-tests')
foreach($taskRoot in $taskTestRoots) {
    $taskTrxFiles=@(Get-ChildItem -LiteralPath (Join-Path $Run $taskRoot) -File -Filter '*.trx')
    if($taskTrxFiles.Count -eq 0) {throw ('Missing suite: '+$taskRoot)}
    foreach($taskFile in $taskTrxFiles) {
        [xml]$taskXml=Get-Content -LiteralPath $taskFile.FullName -Raw
        $taskCounters=$taskXml.TestRun.ResultSummary.Counters
        $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
        if([int]$taskCounters.total -le 0 -or [int]$taskCounters.total -ne [int]$taskCounters.passed -or
            [int]$taskCounters.notExecuted -ne 0 -or $taskRows.Count -ne [int]$taskCounters.total -or
            @($taskRows | Where-Object outcome -cne 'Passed').Count) {throw ('Incomplete TRX: '+$taskFile.FullName)}
        $taskSuites += [ordered]@{path=$taskFile.FullName;sha256=(Get-FileHash -LiteralPath $taskFile.FullName).Hash;total=$taskRows.Count}
        if($taskRoot -eq 'tests') {$taskCases += $taskRows} else {$taskConsumerCount += $taskRows.Count}
    }
}
$taskNew=@($taskCases | Where-Object testName -match 'V155_')
if($taskNew.Count -ne 92 -or $taskCases.Count -ne 3127 -or $taskConsumerCount -ne 25) {throw 'Unexpected test denominator.'}
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
$taskFailedManifest=Get-Content -LiteralPath 'F:\si55v2\ticket55\20260916-003419-534\validation.json' -Raw | ConvertFrom-Json
if($taskFailedManifest.result -cne 'Fail' -or
    ($taskFailedManifest.sourceHashes | ConvertTo-Json -Depth 4 -Compress) -cne
    ($taskManifest.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) {throw 'Unchanged-source recheck identity differs.'}
[xml]$taskIsolated=Get-Content -LiteralPath '.scratch/t55/v140-recheck-51/v140-s24.trx' -Raw
if([int]$taskIsolated.TestRun.ResultSummary.Counters.total -ne 1 -or
    [int]$taskIsolated.TestRun.ResultSummary.Counters.passed -ne 1 -or
    $taskRuntimeHash -cne 'F0C61B284BA560D3C06EFFEA95801C2FB8FAC9DA3F4F39A3E4BA141B8779B7A2') {
    throw 'Isolated original-binary recheck identity differs.'
}
$taskPackageHash=(Get-FileHash -LiteralPath (Join-Path $Run 'packages/SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg')).Hash
$taskFeeds=Get-Content -LiteralPath .scratch/t54/preserved-feeds.json -Raw | ConvertFrom-Json
$taskOldHashes=@()
foreach($taskFeed in $taskFeeds) {foreach($taskPackage in $taskFeed.packages) {
    if((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) {throw 'Preserved package changed.'}
    if($taskPackage.path -match 'SharpInspect.NET.Runtime.') {$taskOldHashes += $taskPackage.sha256}
}}
$taskOldHashes += (Get-FileHash -LiteralPath 'E:\si54p1\ticket54\20260915-030352-254\packages\SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg').Hash
$taskNames=@('store-migration-consumer.json','image-evidence-migration-consumer.json','image-finalization-migration-consumer.json')+
    @(32..36 | ForEach-Object {'outbox-migration-'+$_+'-consumer.json'})+
    @(35..37 | ForEach-Object {'evidence-reconciliation-migration-'+$_+'-consumer.json'})+@('retention-migration-38-consumer.json')
$taskMigrations=foreach($taskName in $taskNames) {
    $taskMigrationRoot=if($taskName -match '^retention-migration') {$RemainingRun}
        elseif($taskName -match '^evidence-reconciliation-migration') {$ContinuationRun} else {$Run}
    $taskPath=Join-Path $taskMigrationRoot $taskName
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or $taskReport.cases.Count -ne 14 -or
        (@($taskReport.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
        @($taskReport.cases | Where-Object result -cne 'Pass').Count -or
        $taskReport.current.runtimeHash -cne $taskRuntimeHash -or $taskReport.current.packageHash -cne $taskPackageHash -or
        (Get-FileHash -LiteralPath $taskReport.current.runtime).Hash -cne $taskRuntimeHash -or
        (Get-FileHash -LiteralPath $taskReport.old.runtime).Hash -cne $taskReport.old.runtimeHash -or
        $taskReport.old.packageHash -cnotin $taskOldHashes) {throw ('Migration identity/phases invalid: '+$taskName)}
    if($taskName -match '^(outbox|evidence-reconciliation|retention)-migration' -and
        (-not $taskReport.fullPhaseCoverage -or @($taskReport.cases | Where-Object {
            -not $_.oldDatabaseAndWalUnchanged -or -not $_.oldWriterAuditUnchanged -or
            $_.currentPostWriteAuditSequence -le 0 -or $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence -or
            $_.seedPostWriteAuditSequence -le 0 -or $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence
        }).Count)) {throw ('Migration writer proof incomplete: '+$taskName)}
    if($taskName -match '^evidence-reconciliation-migration' -and @($taskReport.cases | Where-Object {
        $_.preCommandStartupCompleted -cne $true -or $_.commandSubmissions -ne 1 -or $_.preCommandAuditSequence -le 0 -or
        $_.preCommandVerifiedThroughSequence -lt $_.preCommandAuditSequence -or
        $_.currentPostWriteAuditSequence -le $_.preCommandAuditSequence
    }).Count) {throw ('Repaired startup ordering proof incomplete: '+$taskName)}
    if($taskName -eq 'retention-migration-38-consumer.json' -and
        ($taskReport.targetSchemaVersion -ne 39 -or @($taskReport.cases | Where-Object {
            -not $_.sourceRowsPreserved -or -not $_.operations.startupCompleted -or -not $_.operations.retentionAvailable -or
            -not $_.operations.serviceResolved -or $_.operations.deletionClasses -ne 0 -or $_.operations.capacityAvailable -or
            $null -ne $_.operations.maximumWalBytes -or $_.operations.PendingQuarantines -ne 0 -or $_.operations.IntegrityFaultRecorded
        }).Count)) {throw 'T55 migration scope incomplete.'}
    [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash;phases=14;oldRuntimeHash=$taskReport.old.runtimeHash;runtimeHash=$taskRuntimeHash}
}
$taskPackages=@(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -File -Filter '*.nupkg')
if($taskPackages.Count -ne 8) {throw 'Eight final packages required.'}
$taskAbiPath=Join-Path $AbiRun 'abi-package-binding.json'
$taskAbi=Get-Content -LiteralPath $taskAbiPath -Raw | ConvertFrom-Json
if($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or $taskAbi.libraries.Count -ne 4) {throw 'Legacy ABI missing.'}
foreach($taskLibrary in $taskAbi.libraries) {
    $taskZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $Run ('packages/'+$taskLibrary.package)))
    try {
        $taskStream=$taskZip.GetEntry($taskLibrary.entry).Open()
        try {$taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream))} finally {$taskStream.Dispose()}
        if($taskHash -cne $taskLibrary.sha256) {throw 'ABI package binding differs.'}
    } finally {$taskZip.Dispose()}
}
$taskEvidence=[ordered]@{kind='V1-55-final-development-evidence';result='PassWithRecordedFailures';run=$Run;
    originalFullRunResult='Fail';continuationRun=$ContinuationRun;remainingRun=$RemainingRun;continuationResult='Pass';
    continuationManifest=$taskContinuationPath;interruptedContinuationManifest=$taskContinuation.interruptedManifest;
    interruptedContinuationManifestSha256=$taskContinuation.interruptedManifestSha256;
    interruptedContinuationOriginalResult='NotRun';interruptionCause='Unproven';
    continuationManifestSha256=(Get-FileHash -LiteralPath $taskContinuationPath).Hash;sourceChanges=$taskContinuation.sourceChanges;
    manifestSha256=(Get-FileHash -LiteralPath $taskManifestPath).Hash;sourceFiles=$taskManifest.sourceHashes.Count;
    solutionCases=$taskCases.Count;v155Cases=$taskNew.Count;consumerCases=$taskConsumerCount;failed=0;skipped=0;
    suites=$taskSuites;packages=@($taskPackages | ForEach-Object {[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}});
    migrations=@($taskMigrations);migrationPaths=$taskMigrations.Count;migrationPhases=$taskMigrations.Count*14;
    legacyAbi='Pass';abiEvidence=$taskAbiPath;abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash;
    earlierDevelopmentFailuresRetained=$true;baselineSourcesMatchPriorFailure=$true;unchangedProductSourcesFromFullSolutionRun=$true;
    unchangedRuntimeFromIsolatedRecheck=$true;reusedSolutionAndPackageEvidence=$true;
    runtimeSha256=$taskRuntimeHash;abortedRun='F:\si55v1\ticket55\20260915-234957-858';
    retainedFailureRun='F:\si55v2\ticket55\20260916-003419-534';
    retainedFailureManifestSha256=(Get-FileHash -LiteralPath 'F:\si55v2\ticket55\20260916-003419-534\validation.json').Hash;
    retainedFailureTrxSha256=(Get-FileHash -LiteralPath 'F:\si55v2\ticket55\20260916-003419-534\tests\Hobo_HOBO-DESKTOP_2026-09-16_00_34_37.trx').Hash;
    originalFailure='V140_S24 / RecoveryBlocked / QualificationModbusRecoveryRequired';originalFailureCause='Unproven';
    isolatedRecheck='.scratch/t55/v140-recheck-51/v140-s24.trx';
    isolatedRecheckSha256=(Get-FileHash -LiteralPath '.scratch/t55/v140-recheck-51/v140-s24.trx').Hash;
    diagnostic='F:\si55diag\r1\first-chance-56472.json';diagnosticSha256=(Get-FileHash -LiteralPath 'F:\si55diag\r1\first-chance-56472.json').Hash;
    migrationFailure=$taskMigrationFailurePath;migrationFailureSha256=(Get-FileHash -LiteralPath $taskMigrationFailurePath).Hash;
    migrationFailureDatabaseAndWalUnchanged=$true;migrationInternalAppendFailureReason='Unproven';
    repairedProbeGap='Await startup completion and its verified audit tail before one command; no retry or threshold change';
    productionQualification='NotRun';publicNugetPublication='NotRun';completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath .scratch/t55/final-evidence.json -Encoding utf8
[pscustomobject]$taskEvidence | Select-Object result,sourceFiles,solutionCases,v155Cases,consumerCases,migrationPaths,migrationPhases,legacyAbi | ConvertTo-Json
