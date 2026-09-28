param([Parameter(Mandatory)][string]$Run, [string]$AbiRun='F:\si57abi', [string]$CoveragePath, [string]$ManifestPath, [string]$PerformancePath, [switch]$FinalizeManifest)
$ErrorActionPreference='Stop'
$taskManifestPath=if($ManifestPath){$ManifestPath}else{Join-Path $Run 'validation.json'}
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if(($taskManifest.result -cne 'Pass' -and -not ($FinalizeManifest -and $taskManifest.result -ceq 'PendingEvidenceAudit')) -or $taskManifest.kind -notin 'V1-57-development-validation','V1-57-composite-development-validation') {throw 'Completed T57 validation required.'}
foreach($taskSource in $taskManifest.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) {throw ('Validated source changed: '+$taskSource.path)}
}
$taskBinaryProofPath=Join-Path $PSScriptRoot 'binary-bindings30.json'
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
$taskOwned=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-paths.json') -Raw | ConvertFrom-Json)
$taskProtected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-baseline.json') -Raw | ConvertFrom-Json | Where-Object { $_.path -notin $taskOwned })
foreach($taskFile in $taskProtected) {if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Unrelated baseline file changed.'}}
if($FinalizeManifest) {
    if(-not $ManifestPath -or $taskManifest.result -cne 'PendingEvidenceAudit') {throw 'Only a fresh composite manifest may be finalized.'}
    $taskManifest.result='Pass'
    $taskManifest | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
    $taskManifest | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
}
[ordered]@{kind='V1-57-final-development-evidence';result='PassWithRecordedDevelopmentFailures';run=$Run;
    manifestSha256=(Get-FileHash -LiteralPath $taskManifestPath).Hash;sourceFiles=$taskManifest.sourceHashes.Count;
    candidateAssemblies=$taskBinaryProof.binaries.Count;candidateAssembliesUnchanged=$true;binaryBindingsSha256=(Get-FileHash -LiteralPath $taskBinaryProofPath).Hash;
    solutionCases=$taskCases.Count;v157Cases=$taskNew.Count;consumerCases=$taskConsumerCount;selectedFailures=0;skipped=0;
    coverage=$CoveragePath;coverageSha256=if($CoveragePath){(Get-FileHash -LiteralPath $CoveragePath).Hash}else{$null};
    suites=$taskSuites;packages=@($taskPackages | ForEach-Object {[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}});
    migrationPaths=$taskMigrations.Count;migrationPhases=$taskMigrations.Count*14;migrations=@($taskMigrations);
    legacyAbi='Pass';abiEvidence=$taskAbiPath;abiEvidenceSha256=(Get-FileHash -LiteralPath $taskAbiPath).Hash;
    fatalConsumer=$taskFatalPath;fatalConsumerSha256=(Get-FileHash -LiteralPath $taskFatalPath).Hash;fatalModes=$taskFatal.runs;
    performanceConsumer=$taskPerformancePath;performanceConsumerSha256=(Get-FileHash -LiteralPath $taskPerformancePath).Hash;
    actualPerformanceDocument=$taskRawPath;actualPerformanceDocumentSha256=(Get-FileHash -LiteralPath $taskRawPath).Hash;
    runtimeSha256=$taskRuntimeHash;protectedFiles=$taskProtected.Count;protectedFilesUnchanged=$true;
    earlierDevelopmentFailuresRetained=$true;abortedOriginal='.scratch/t57/validation21-status.json';
    pathProbe='.scratch/t57/sqlite-path-probe.log';crossRuntimeJsonProbe='.scratch/t57/canonical-cross-runtime22.json';
    parentClipboardFailure='.scratch/t57/identity-parent23.json';clipboardOwnerObservation='.scratch/t57/clipboard-observer24.json';
    clipboardCorrection='.scratch/t57/identity25-tests';readyRaceRed='.scratch/t57/ready-red27-tests';
    readyRaceGreen='.scratch/t57/ready-green28-tests';readyRevocationNegatives='.scratch/t57/ready-negatives29-tests';
    productionQualification='NotRun';physicalStation='NotRun';fullSixScenarioProfile='NotRun';publicNuget='NotRun'
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'final-evidence.json') -Encoding utf8
Write-Output 'T57 final evidence collected.'
