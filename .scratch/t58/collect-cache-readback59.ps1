param([Parameter(Mandatory)][string]$Run, [string]$AbiRun='F:\si58abi22', [string]$CoveragePath, [string]$ManifestPath, [string]$PerformancePath, [switch]$FinalizeManifest)
$ErrorActionPreference='Stop'
$taskManifestPath=if($ManifestPath){$ManifestPath}else{Join-Path $Run 'validation.json'}
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if(($taskManifest.result -cne 'Pass' -and -not ($FinalizeManifest -and $taskManifest.result -ceq 'PendingEvidenceAudit')) -or $taskManifest.kind -notin 'V1-58-development-validation','V1-58-composite-development-validation') {throw 'Completed T58 validation required.'}
foreach($taskSource in $taskManifest.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) {throw ('Validated source changed: '+$taskSource.path)}
}
$taskBinaryProofPath=Join-Path $PSScriptRoot 'binary-bindings22.json'
$taskBinaryProof=Get-Content -LiteralPath $taskBinaryProofPath -Raw | ConvertFrom-Json
if($taskBinaryProof.binaries.Count -ne 16) {throw 'Candidate assembly denominator differs.'}
foreach($taskBinary in $taskBinaryProof.binaries) {
    if((Get-FileHash -LiteralPath $taskBinary.path).Hash -cne $taskBinary.sha256) {throw ('Candidate assembly changed: '+$taskBinary.path)}
}
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
if((Get-FileHash -LiteralPath tests/SharpInspect.Runtime.Tests/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash -cne $taskRuntimeHash) {throw 'Test runtime assembly differs.'}
$taskSuites=@(); $taskCases=@(); $taskConsumerCount=0
if($CoveragePath) {
    $taskCoverage=Get-Content -LiteralPath $CoveragePath -Raw | ConvertFrom-Json
    if($taskCoverage.result -cne 'PassWithRecordedDevelopmentFailures') {throw 'Fixed coverage incomplete.'}
    $taskOriginalManifest=Join-Path $taskCoverage.baseline 'validation.json'
    if((Get-FileHash -LiteralPath $taskOriginalManifest).Hash -cne $taskCoverage.baselineManifestSha256 -or
        (Get-Content -LiteralPath $taskOriginalManifest -Raw | ConvertFrom-Json).result -cne 'Fail') {throw 'Original failure manifest changed.'}
    $taskCases=@($taskCoverage.selectedCases)
    $taskSelectedByName=@{}
    foreach($taskSelected in $taskCases) {if($taskSelectedByName.ContainsKey($taskSelected.testName)){throw 'Duplicate selected case.'};$taskSelectedByName[$taskSelected.testName]=$taskSelected}
    $taskOriginalCount=0; $taskOriginalFailures=@()
    foreach($taskOriginalTrx in Get-ChildItem -LiteralPath (Join-Path $taskCoverage.baseline 'tests') -Filter '*.trx' -File) {
        [xml]$taskOriginalXml=Get-Content -LiteralPath $taskOriginalTrx.FullName -Raw
        $taskOriginalHash=(Get-FileHash -LiteralPath $taskOriginalTrx.FullName).Hash
        foreach($taskOriginalRow in $taskOriginalXml.TestRun.Results.UnitTestResult) {
            $taskOriginalCount++
            $taskSelected=$taskSelectedByName[$taskOriginalRow.testName]
            if($null -eq $taskSelected) {throw 'Original case omitted.'}
            if($taskOriginalRow.outcome -ceq 'Passed') {
                if($taskSelected.trx -cne $taskOriginalTrx.FullName -or $taskSelected.trxSha256 -cne $taskOriginalHash -or
                    $taskSelected.testId -cne $taskOriginalRow.testId -or $taskSelected.executionId -cne $taskOriginalRow.executionId) {throw 'An original passing case was replaced.'}
            } else {$taskOriginalFailures += $taskOriginalRow.testName}
        }
    }
    if($taskOriginalCount -ne $taskCases.Count -or (@($taskOriginalFailures | Sort-Object) -join '|') -cne
        (@($taskCoverage.originalFailures.testName | Sort-Object) -join '|')) {throw 'Original failure identities or denominator differ.'}
} else {
    foreach($taskTrx in Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File) {
        [xml]$taskXml=Get-Content -LiteralPath $taskTrx.FullName -Raw
        $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
        if($taskRows.Count -ne [int]$taskXml.TestRun.ResultSummary.Counters.total -or @($taskRows | Where-Object outcome -cne 'Passed').Count) {throw 'Original full test run did not pass.'}
        $taskHash=(Get-FileHash -LiteralPath $taskTrx.FullName).Hash
        foreach($taskRow in $taskRows) {
            $taskCases += [pscustomobject][ordered]@{testName=$taskRow.testName;testId=$taskRow.testId;executionId=$taskRow.executionId;outcome=$taskRow.outcome;trx=$taskTrx.FullName;trxSha256=$taskHash}
        }
    }
}
if(@($taskCases | Where-Object outcome -cne 'Passed').Count -or @($taskCases | Group-Object testName | Where-Object Count -ne 1).Count) {throw 'Case identities or outcomes invalid.'}
foreach($taskGroup in $taskCases | Group-Object trx) {
    $taskTrxHash=(Get-FileHash -LiteralPath $taskGroup.Name).Hash
    if(@($taskGroup.Group | Where-Object trxSha256 -cne $taskTrxHash).Count) {throw 'Original TRX was altered.'}
    [xml]$taskXml=Get-Content -LiteralPath $taskGroup.Name -Raw
    $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
    $taskLookup=@{}
    foreach($taskRow in $taskRows) {
        $taskKey=$taskRow.testId+'|'+$taskRow.executionId
        if($taskLookup.ContainsKey($taskKey)) {throw 'Duplicate TRX execution identity.'}
        $taskLookup[$taskKey]=$taskRow
    }
    foreach($taskCase in $taskGroup.Group) {
        $taskObserved=$taskLookup[$taskCase.testId+'|'+$taskCase.executionId]
        if($null -eq $taskObserved -or $taskObserved.testName -cne $taskCase.testName -or $taskObserved.outcome -cne 'Passed') {throw 'Case has no unique passing original observation.'}
    }
    $taskSuites += [ordered]@{path=$taskGroup.Name;sha256=$taskTrxHash;selected=$taskGroup.Count;sourceTotal=$taskRows.Count;sourceFailed=@($taskRows | Where-Object outcome -cne 'Passed').Count}
}
. (Join-Path $PSScriptRoot 'collect-existing-checks.ps1')
$taskPerformancePath=if($PerformancePath){$PerformancePath}else{Join-Path $Run 'performance-consumer.json'}
$taskPerformance=Get-Content -LiteralPath $taskPerformancePath -Raw | ConvertFrom-Json
if($taskPerformance.result -cne 'Pass' -or -not $taskPerformance.externalNuGetConsumer -or
    $taskPerformance.productionQualificationAuthority -cne $false -or $taskPerformance.hashes.RuntimeAssembly -cne $taskRuntimeHash -or
    $taskPerformance.runtimeInspect.passed -cne $true -or $taskPerformance.runtimeInspect.productionQualificationAuthority -cne $false -or
    $taskPerformance.runtimeInspect.counts.acceptedCycles -ne 2 -or $taskPerformance.runtimeInspect.captureState -cne 'Sealed') {throw 'Independent actual performance readback missing.'}
$taskRawPath=Join-Path $Run 'performance-runtime-evidence/fixed-virtual-runtime.performance.json'
if((Get-FileHash -LiteralPath $taskRawPath).Hash -cne $taskPerformance.runtimeEvidenceSha256) {throw 'Actual performance document changed.'}
$taskOwned=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-files.json') -Raw | ConvertFrom-Json)
$taskProtected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-baseline.json') -Raw | ConvertFrom-Json | Where-Object { $_.path -notin $taskOwned })
foreach($taskFile in $taskProtected) {if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Unrelated baseline file changed.'}}
$taskSupportPath=Join-Path $Run 'diagnostic-support-consumer.json'
$taskSupport=Get-Content -LiteralPath $taskSupportPath -Raw | ConvertFrom-Json
$taskSupportChecks=@('profile.canonicalComponents','profile.immutableCollections','profile.bounds','profile.enumerationBound',
    'scope.canonicalOrder','scope.bounds','commands.targets','commands.invalidRejected','permissions.distinct',
    'storeOptions.binding','storeOptions.failClosedProfile','supportRoot.installation','loggingSupport.binding',
    'runtime.queryProjection','runtime.absentConfigRefusal','runtime.bundleReaderUnavailable',
    'wpf.publicSurface','wpf.closedChoices','wpf.noExportSurface')
if((@($taskSupport.consumer.checks.PSObject.Properties.Name | Sort-Object) -join '|') -cne
    (@($taskSupportChecks | Sort-Object) -join '|') -or
    (Get-FileHash -LiteralPath $taskSupport.consumerExe).Hash -cne $taskSupport.packages.Consumer) {
    throw 'T58 exact consumer check set or executed consumer identity differs.'
}
foreach($taskLibrary in 'Abstractions','Runtime','Wpf') {
    $taskPackagePath=Join-Path $Run ('packages/SharpInspect.NET.'+$taskLibrary+'.0.1.0-dev.1.nupkg')
    if((Get-FileHash -LiteralPath $taskPackagePath).Hash -cne $taskSupport.packages.($taskLibrary+'Package')) {
        throw 'T58 consumer package changed.'
    }
    $taskZip=[IO.Compression.ZipFile]::OpenRead($taskPackagePath)
    try {
        $taskAssets=@($taskZip.Entries | Where-Object {$_.FullName -like 'lib/*' -and $_.Name -ceq ('SharpInspect.'+$taskLibrary+'.dll')})
        if($taskAssets.Count -ne 1){throw 'T58 consumer package asset ambiguous.'}
        $taskStream=$taskAssets[0].Open()
        try{$taskAssetHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream))}finally{$taskStream.Dispose()}
        $taskConsumerAssembly=Join-Path (Split-Path -Parent $taskSupport.consumerExe) ('SharpInspect.'+$taskLibrary+'.dll')
        $taskFramework=if($taskLibrary -eq 'Wpf'){'net6.0-windows'}else{'net6.0'}
        $taskBuiltAssembly=Join-Path (Get-Location) ('src/SharpInspect.'+$taskLibrary+'/bin/Release/'+$taskFramework+'/SharpInspect.'+$taskLibrary+'.dll')
        if($taskAssetHash -cne $taskSupport.packages.($taskLibrary+'Assembly') -or
            (Get-FileHash -LiteralPath $taskBuiltAssembly).Hash -cne $taskAssetHash -or
            (Get-FileHash -LiteralPath $taskConsumerAssembly).Hash -cne $taskAssetHash){throw 'T58 loaded package assembly differs.'}
    } finally {$taskZip.Dispose()}
}
if($taskSupport.result -cne 'Pass' -or $taskSupport.packages.RuntimeAssembly -cne $taskRuntimeHash -or
    $taskSupport.packages.RuntimePackage -cne $taskPackageHash -or
    $taskSupport.productionQualificationAuthority -cne $false -or
    $taskSupport.captureExportRuntime -cne 'NotRun' -or $taskSupport.wpfRuntimeExercise -cne 'NotRun' -or
    (@($taskSupport.verificationIds | Sort-Object) -join ',') -cne 'V158_K01,V158_K02,V158_K03' -or
    @($taskSupport.consumer.checks.PSObject.Properties | Where-Object Value -cne $true).Count -ne 0) {
    throw 'T58 package consumer scope or binding incomplete.'
}
$taskSupportMigrationPath=Join-Path $Run 'diagnostic-support-migration-39-consumer.json'
$taskSupportReports=Get-Content -LiteralPath $taskSupportMigrationPath -Raw | ConvertFrom-Json
$taskSupportMigration=$taskSupportReports.migration
$taskMigrationAbstractions=@()
foreach($taskSide in 'old','current') {
    $taskAbstractionsPackage=if($taskSide -eq 'old'){
        'F:\si57c31\ticket57\20260916-160130-463\packages\SharpInspect.NET.Abstractions.0.1.0-dev.1.nupkg'
    }else{Join-Path $Run 'packages/SharpInspect.NET.Abstractions.0.1.0-dev.1.nupkg'}
    $taskZip=[IO.Compression.ZipFile]::OpenRead($taskAbstractionsPackage)
    try {
        $taskAsset=$taskZip.GetEntry('lib/net6.0/SharpInspect.Abstractions.dll')
        if($null -eq $taskAsset){throw 'Migration Abstractions package asset missing.'}
        $taskStream=$taskAsset.Open()
        try{$taskAssetHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream))}finally{$taskStream.Dispose()}
        $taskLoaded=Join-Path (Split-Path -Parent $taskSupportMigration.$taskSide.runtime) 'SharpInspect.Abstractions.dll'
        if((Get-FileHash -LiteralPath $taskLoaded).Hash -cne $taskAssetHash){throw 'Migration loaded Abstractions differs from its preserved/candidate package.'}
        $taskMigrationAbstractions+=@{side=$taskSide;assembly=$taskLoaded;sha256=$taskAssetHash;
            package=$taskAbstractionsPackage;packageSha256=(Get-FileHash -LiteralPath $taskAbstractionsPackage).Hash}
    }finally{$taskZip.Dispose()}
}
$taskOld39=(Get-FileHash -LiteralPath 'F:\si57c31\ticket57\20260916-160130-463\packages\SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg').Hash
if($taskSupportMigration.result -cne 'Pass' -or $taskSupportMigration.sourceSchemaVersion -ne 39 -or
    $taskSupportMigration.targetSchemaVersion -ne 40 -or -not $taskSupportMigration.fullPhaseCoverage -or
    $taskSupportMigration.cases.Count -ne 14 -or
    (@($taskSupportMigration.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
    $taskSupportMigration.current.runtimeHash -cne $taskRuntimeHash -or
    $taskSupportMigration.current.packageHash -cne $taskPackageHash -or
    $taskSupportMigration.old.packageHash -cne $taskOld39 -or
    (Get-FileHash -LiteralPath $taskSupportMigration.old.runtime).Hash -cne $taskSupportMigration.old.runtimeHash -or
    (Get-FileHash -LiteralPath $taskSupportMigration.current.runtime).Hash -cne $taskRuntimeHash -or
    @($taskSupportMigration.cases | Where-Object {
        $_.result -cne 'Pass' -or -not $_.sourceRowsPreserved -or -not $_.diagnosticSupportConfigured -or
        -not $_.oldWriterAuditUnchanged -or -not $_.oldDatabaseAndWalUnchanged -or
        $_.seedPostWriteAuditSequence -le 0 -or $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence -or
        $_.currentPostWriteAuditSequence -le 0 -or $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence -or
        $_.currentDiagnosticSupport.capturePhase -cne 'Baseline' -or $_.currentDiagnosticSupport.elevated -cne $false -or
        $_.currentDiagnosticSupport.bundlePhase -cne 'Idle' -or $_.currentDiagnosticSupport.readAvailable -cne $false
    }).Count -ne 0 -or $taskSupportReports.previousWriterRefusal.result -cne 'Pass' -or
    $taskSupportReports.previousWriterRefusal.cases.Count -ne 14 -or
    @($taskSupportReports.previousWriterRefusal.cases | Where-Object {
        $_.reason -cne 'TraceStoreUnavailable' -or $_.auditLedgerUnchanged -cne $true
    }).Count -ne 0) { throw 'T58 old writer/migration identity or phase coverage incomplete.' }
foreach($taskFeed in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'preserved-feeds.json') -Raw | ConvertFrom-Json)) {
    foreach($taskPackage in $taskFeed.packages) {
        if((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) { throw 'T58 preserved package changed.' }
    }
}
$taskMigrations+=@{path=$taskSupportMigrationPath;sha256=(Get-FileHash -LiteralPath $taskSupportMigrationPath).Hash;
    phases=14;oldRuntimeHash=$taskSupportMigration.old.runtimeHash;runtimeHash=$taskRuntimeHash}
if($FinalizeManifest) {
    if(-not $ManifestPath -or $taskManifest.result -cne 'PendingEvidenceAudit') {throw 'Only a fresh composite manifest may be finalized.'}
    $taskManifest.result='Pass'
    $taskManifest | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
    $taskManifest | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
}
[ordered]@{
    kind='V1-58-final-development-evidence';result='PassWithRecordedDevelopmentFailures';run=$Run
    manifest=$taskManifestPath;manifestSha256=(Get-FileHash -LiteralPath $taskManifestPath).Hash
    sourceFiles=$taskManifest.sourceHashes.Count;candidateAssemblies=$taskBinaryProof.binaries.Count
    candidateAssembliesUnchanged=$true;binaryBindingsSha256=(Get-FileHash -LiteralPath $taskBinaryProofPath).Hash
    solutionCases=$taskCases.Count;v158Cases=$taskNew.Count;consumerCases=$taskConsumerCount;selectedFailures=0;skipped=0
    coverage=$CoveragePath;suites=$taskSuites
    packages=@($taskPackages | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    migrationPaths=$taskMigrations.Count;migrationPhases=$taskMigrations.Count*14;migrations=@($taskMigrations)
    legacyAbi='Pass';abiEvidence=$taskAbiPath;abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash
    fatalConsumer=$taskFatalPath;fatalConsumerSha256=(Get-FileHash -LiteralPath $taskFatalPath).Hash;fatalModes=$taskFatal.runs
    performanceConsumer=$taskPerformancePath;performanceConsumerSha256=(Get-FileHash -LiteralPath $taskPerformancePath).Hash
    supportConsumer=$taskSupportPath;supportConsumerSha256=(Get-FileHash -LiteralPath $taskSupportPath).Hash
    supportMigrationAbstractions=$taskMigrationAbstractions
    actualPerformanceDocument=$taskRawPath;actualPerformanceDocumentSha256=(Get-FileHash -LiteralPath $taskRawPath).Hash
    runtimeSha256=$taskRuntimeHash;protectedFiles=$taskProtected.Count;protectedFilesUnchanged=$true
    earlierDevelopmentFailuresRetained=$true;review='.scratch/t58/review-resolution.md'
    productionQualification='NotRun';physicalStation='NotRun';publicNuget='NotRun';manualVisualAcceptance='NotRun'
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'cache-cleanup-readback59.json') -Encoding utf8
Write-Output 'T58 final evidence collected.'

