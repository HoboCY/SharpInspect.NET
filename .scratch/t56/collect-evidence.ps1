param([Parameter(Mandatory)][string]$Run, [string]$AbiRun='F:\si56abi')
$ErrorActionPreference='Stop'
$taskManifestPath=Join-Path $Run 'validation.json'
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if($taskManifest.result -cne 'Pass' -or $taskManifest.kind -cne 'V1-56-composite-development-validation') {throw 'Complete T56 composite validation required.'}
$taskFormatting=$null
$taskFormattingPath='.scratch/t56/eof-finalization/normalization.json'
if(Test-Path -LiteralPath $taskFormattingPath){
    $taskFormatting=Get-Content -LiteralPath $taskFormattingPath -Raw | ConvertFrom-Json
    if($taskFormatting.kind -cne 'T56-post-validation-EOF-formatting' -or $taskFormatting.run -cne $Run -or $taskFormatting.files.Count -ne 3){throw 'Unexpected post-validation formatting.'}
}
foreach($taskSource in $taskManifest.sourceHashes) {
    $taskCurrentHash=(Get-FileHash -LiteralPath $taskSource.path).Hash
    if($taskCurrentHash -cne $taskSource.sha256) {
        $taskChange=@($taskFormatting.files | Where-Object path -ceq $taskSource.path)
        if($taskChange.Count -ne 1 -or $taskChange[0].validatedSha256 -cne $taskSource.sha256 -or $taskChange[0].finalSha256 -cne $taskCurrentHash -or
           (Get-FileHash -LiteralPath $taskChange[0].validatedSource).Hash -cne $taskSource.sha256){throw ('Validated source changed: '+$taskSource.path)}
        $taskValidated=[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $taskChange[0].validatedSource)))
        if($taskValidated -notmatch '(\r?\n){2,}\z'){throw 'Formatting input did not have extra EOF line.'}
        $taskExpected=[Text.Encoding]::UTF8.GetBytes($taskValidated.TrimEnd([char[]]"`r`n")+"`n")
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskExpected)) -cne $taskCurrentHash){throw 'Post-validation change was not EOF-only.'}
    }
}
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
$taskPositive=Get-Content -LiteralPath '.scratch/t56/forced-positive-44.json' -Raw | ConvertFrom-Json
if($taskPositive.result -cne 'Pass' -or $taskPositive.runtimeSha256 -cne $taskRuntimeHash -or
    $taskPositive.directVerifyCallOffsets.Count -ne 0 -or
    (Get-FileHash -LiteralPath tests/SharpInspect.Runtime.Tests/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash -cne $taskRuntimeHash) {
    throw 'Final binary differs from the forced positive IL/probe evidence.'
}
$taskCoveragePath=Join-Path $Run 'coverage.json'
$taskCoverage=Get-Content -LiteralPath $taskCoveragePath -Raw | ConvertFrom-Json
if($taskCoverage.result -cne 'Pass' -or $taskCoverage.kind -cne 'V1-56-fixed-composite-test-coverage' -or
    $taskCoverage.originalFullResult -cne 'Fail' -or $taskCoverage.originalFailures.Count -ne 2 -or
    $taskCoverage.caseCount -ne 3164 -or
    (Get-FileHash -LiteralPath (Join-Path $taskCoverage.baseline 'validation.json')).Hash -cne $taskCoverage.baselineManifestSha256 -or
    (Get-FileHash -LiteralPath '.scratch/t56/baseline53/bindings.json').Hash -cne $taskCoverage.baselineBindingsSha256) {throw 'Composite provenance invalid.'}
$taskSuites=@(); $taskCases=@($taskCoverage.selectedCases); $taskConsumerCount=0
if(@($taskCases | Group-Object key | Where-Object Count -ne 1).Count -or @($taskCases | Where-Object outcome -cne 'Passed').Count){throw 'Composite case identities invalid.'}
foreach($taskGroup in @($taskCases | Group-Object trx)) {
    $taskTrxPath=$taskGroup.Name
    $taskTrxHash=(Get-FileHash -LiteralPath $taskTrxPath).Hash
    if(@($taskGroup.Group | Where-Object trxSha256 -cne $taskTrxHash).Count){throw 'Selected TRX changed.'}
    [xml]$taskXml=Get-Content -LiteralPath $taskTrxPath -Raw
    $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
    $taskLookup=@{}
    foreach($taskRow in $taskRows){
        $taskRowKey=$taskRow.testId+'|'+$taskRow.executionId
        if($taskLookup.ContainsKey($taskRowKey)){throw 'Duplicate TRX execution identity.'}
        $taskLookup[$taskRowKey]=$taskRow
    }
    foreach($taskCase in $taskGroup.Group) {
        $taskObserved=$taskLookup[$taskCase.testId+'|'+$taskCase.executionId]
        if($null -eq $taskObserved -or $taskObserved.testName -cne $taskCase.testName -or $taskObserved.outcome -cne 'Passed'){throw 'Selected case has no passing original observation.'}
    }
    $taskSuites += [ordered]@{path=$taskTrxPath;sha256=$taskTrxHash;selected=$taskGroup.Count;sourceTotal=$taskRows.Count;sourceFailed=@($taskRows | Where-Object outcome -cne 'Passed').Count}
}
$taskTestRoots=@('identity-consumer-tests','draft-consumer-tests','recipe-release-consumer-tests',
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
        $taskConsumerCount += $taskRows.Count
    }
}
$taskNew=@($taskCases | Where-Object testName -match 'V156_')
if($taskNew.Count -ne 37 -or $taskCases.Count -ne 3164 -or $taskConsumerCount -ne 25) {throw 'Unexpected test denominator.'}
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
    $taskMigrationRoot=$Run
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
$taskFatalPath=Join-Path $Run 'diagnostics-consumer/validation.json'
$taskFatal=Get-Content -LiteralPath $taskFatalPath -Raw | ConvertFrom-Json
if($taskFatal.result -cne 'Pass' -or $taskFatal.verificationId -cne 'V156_H01' -or
    $taskFatal.runtimeAssemblySha256 -cne $taskRuntimeHash -or
    $taskFatal.wpfAssemblySha256 -cne (Get-FileHash -LiteralPath 'src/SharpInspect.Wpf/bin/Release/net6.0-windows/SharpInspect.Wpf.dll').Hash -or
    $taskFatal.runs.Count -ne 5 -or ($taskFatal.runs.mode -join ',') -cne 'dispatcher,domain,hang,domain-race,process-exit' -or
    @($taskFatal.runs | Where-Object exitCode -ne 1).Count) {throw 'Packaged fatal boundary evidence missing.'}
foreach($taskMode in $taskFatal.runs){
    $taskModePath=Join-Path $Run ('diagnostics-consumer/'+$taskMode.mode+'.json')
    $taskModeEvidence=Get-Content -LiteralPath $taskModePath -Raw | ConvertFrom-Json
    if((Get-FileHash -LiteralPath $taskModePath).Hash -cne $taskMode.evidenceSha256 -or $taskModeEvidence.Calls -ne 1 -or
       $taskModeEvidence.Ready -ne $false -or $taskModeEvidence.Arm -cne 'Disarmed' -or $taskModeEvidence.Lifecycle -cne 'Stopped' -or
       -not [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath (Join-Path $Run ('diagnostics-consumer/'+$taskMode.mode+'.log')) -Raw)) -or
       (Test-Path -LiteralPath ($taskModePath+'.process-exit'))){throw 'Fatal mode observation changed.'}
}
if((Get-Content -LiteralPath (Join-Path $Run 'diagnostics-consumer/domain-race.json.exit-calls') -Raw).Trim() -cne '2'){throw 'Exit race not exercised.'}
foreach($taskFile in (Get-Content -LiteralPath .scratch/t56/protected-files.json -Raw | ConvertFrom-Json)) {
    if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Protected files changed.'}
}
$taskEvidence=[ordered]@{kind='V1-56-final-development-evidence';result='PassWithRecordedDevelopmentFailures';run=$Run;
    manifestSha256=(Get-FileHash -LiteralPath $taskManifestPath).Hash;sourceFiles=$taskManifest.sourceHashes.Count;
    solutionCases=$taskCases.Count;v156Cases=$taskNew.Count;consumerCases=$taskConsumerCount;selectedFailures=0;skipped=0;
    originalFullResult='Fail';originalFullFailures=$taskCoverage.originalFailures.Count;
    coverage=$taskCoveragePath;coverageSha256=(Get-FileHash -LiteralPath $taskCoveragePath).Hash;selectionRule=$taskCoverage.selectionRule;
    postValidationFormatting=$taskFormatting;postValidationFormattingSha256=if($taskFormatting){(Get-FileHash -LiteralPath $taskFormattingPath).Hash}else{$null};
    suites=$taskSuites;packages=@($taskPackages | ForEach-Object {[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}});
    migrations=@($taskMigrations);migrationPaths=$taskMigrations.Count;migrationPhases=$taskMigrations.Count*14;
    legacyAbi='Pass';abiEvidence=$taskAbiPath;abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash;
    fatalConsumer=$taskFatalPath;fatalConsumerSha256=(Get-FileHash -LiteralPath $taskFatalPath).Hash;fatalModes=$taskFatal.runs;
    runtimeSha256=$taskRuntimeHash;protectedFilesUnchanged=$true;
    earlierDevelopmentFailuresRetained=$true;abortedDevelopmentRun='.scratch/t56/solution-25-status.json';
    abortedLongTempRun='.scratch/t56/solution-32-status.json';tempPathProbe='.scratch/t56/migration-temp-path-34.json';
    abortedLegacyCounterRun='.scratch/t56/solution-35-status.json';
    abortedShutdownRun='.scratch/t56/solution-38-status.json';
    abortedIncrementalReuseRun='.scratch/t56/solution-43-status.json';
    abortedCalibrationRun='.scratch/t56/solution-45-status.json';calibrationOriginalRejectionCause='Unproven';
    calibrationIsolatedRecheck='.scratch/t56/test-results/v127-isolated-46.trx';
    calibrationIsolatedRecheckSha256=(Get-FileHash -LiteralPath '.scratch/t56/test-results/v127-isolated-46.trx').Hash;
    originalCrashRun='.scratch/t56/solution-47-status.json';crashOriginalCause='Unknown/Unproven';
    crashObservation='.scratch/t56/crash-reopen-52-status.json';
    fatalConsumerBuildFailure='.scratch/t56/preliminary-consumer-48-status.json';
    fatalRawOutputFailure='.scratch/t56/preliminary-consumer-49-status.json';
    fatalExitRaceAndWpfFailure='.scratch/t56/fatal-race-50-status.json';
    fatalSourceControl='F:\si56race51\validation.json';
    forcedPositiveEvidence='.scratch/t56/forced-positive-44.json';
    forcedPositiveEvidenceSha256=(Get-FileHash -LiteralPath '.scratch/t56/forced-positive-44.json').Hash;
    isolationControl='.scratch/t56/isolation-control-42.json';
    isolationControlSha256=(Get-FileHash -LiteralPath '.scratch/t56/isolation-control-42.json').Hash;
    tempPathProbeSha256=(Get-FileHash -LiteralPath '.scratch/t56/migration-temp-path-34.json').Hash;
    earlierBuildFailure='F:\si56v1\ticket56\20260916-113335-414\validation.json';
    earlierBuildFailureSha256=(Get-FileHash -LiteralPath 'F:\si56v1\ticket56\20260916-113335-414\validation.json').Hash;
    productionQualification='NotRun';publicNugetPublication='NotRun';completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath .scratch/t56/final-evidence.json -Encoding utf8
[pscustomobject]$taskEvidence | Select-Object result,sourceFiles,solutionCases,v156Cases,consumerCases,migrationPaths,migrationPhases,legacyAbi | ConvertTo-Json


