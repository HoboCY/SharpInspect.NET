param(
    [string]$SolutionRun='D:\si53v\ticket53\20260914-235726-414',
    [string]$PostTestRun='D:\si53p\ticket53\20260915-000957-806',
    [string]$Destination='D:\projects\SharpInspect.NET\.scratch\t53\final-evidence',
    [string]$Migration35Run='D:\si53m35',
    [string]$Migration36Run='D:\si53m36')
$ErrorActionPreference='Stop'
$taskRepo=(Get-Location).Path
$taskSolution=Get-Content -LiteralPath (Join-Path $SolutionRun 'validation.json') -Raw | ConvertFrom-Json
$taskPost=Get-Content -LiteralPath (Join-Path $PostTestRun 'validation.json') -Raw | ConvertFrom-Json
if($taskSolution.kind -cne 'V1-53-development-validation' -or $taskSolution.result -cne 'Fail' -or
    $taskPost.kind -cne 'V1-53-post-test-validation' -or $taskPost.result -cne 'Fail' -or
    $taskPost.executionMode -cne 'PostTestOnly' -or $taskPost.solutionTests -cne 'RecordedSeparately') {
    throw 'The two preserved original failed invocations are required.'
}
$taskSeedRoot=Join-Path $PostTestRun 'outbox-migration-35-consumer/phase-2'
$taskSeed=Get-Content -LiteralPath (Join-Path $taskSeedRoot 'seed-writer.json') -Raw | ConvertFrom-Json
if($taskSeed.schema -ne 35 -or $taskSeed.audit -cne 'Unavailable' -or
    $taskSeed.ReasonCode -cne 'TraceAuditUnavailable' -or $null -ne $taskSeed.postWriteAuditSequence -or
    $taskSeed.auditVerifiedThroughSequence -ne 12 -or $taskSeed.auditLedgerAfter -notmatch '^13\|13\|' -or
    (Test-Path -LiteralPath (Join-Path $taskSeedRoot 'pause-phase.json'))) { throw 'Original post-test failure differs.' }
$taskSources=@((Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File)+
    (Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln) |
    Where-Object FullName -NotMatch '\\(bin|obj|TestResults)\\' | Sort-Object FullName |
    ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
if(($taskSources | ConvertTo-Json -Compress) -cne ($taskSolution.sourceHashes | ConvertTo-Json -Compress) -or
    ($taskSources | ConvertTo-Json -Compress) -cne ($taskPost.sourceHashes | ConvertTo-Json -Compress)) {
    throw 'Source set/content differs between scopes or current candidate.'
}
$taskHead=(git -c safe.directory=D:/projects/SharpInspect.NET rev-parse HEAD).Trim()
if($taskHead -cne '4302f5cece2e4eb8fae56ebbaa64bb427b5066c2' -or
    $taskSolution.startingHead -cne $taskHead -or $taskPost.startingHead -cne $taskHead) { throw 'Baseline changed.' }
$taskArtifacts=[Collections.Generic.List[object]]::new()
function Add-Artifact([string]$Path,[string]$Scope) {
    $taskArtifacts.Add([ordered]@{path=[IO.Path]::GetFullPath($Path);scope=$Scope;sha256=(Get-FileHash -LiteralPath $Path).Hash})
}
function Read-Trx([string]$Path,[bool]$AllowFailure=$false) {
    [xml]$taskXml=Get-Content -LiteralPath $Path -Raw
    $taskCounts=$taskXml.TestRun.ResultSummary.Counters
    $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
    if($taskRows.Count -le 0 -or $taskRows.Count -ne [int]$taskCounts.total -or
        [int]$taskCounts.notExecuted -ne 0 -or [int]$taskCounts.total -ne [int]$taskCounts.executed -or
        @($taskRows | Where-Object outcome -NotIn 'Passed','Failed').Count -or
        (-not $AllowFailure -and @($taskRows | Where-Object outcome -cne 'Passed').Count)) {
        throw ('Incomplete or unexpected TRX: '+$Path)
    }
    Add-Artifact $Path 'TestExecution'
    return $taskRows
}
$taskSolutionTrx=@(Get-ChildItem -LiteralPath (Join-Path $SolutionRun 'tests') -File -Filter '*.trx')
if($taskSolutionTrx.Count -ne 7) { throw 'All seven solution test assemblies are required.' }
$taskOriginalRows=@($taskSolutionTrx | ForEach-Object { Read-Trx $_.FullName $true })
$taskFailed=@($taskOriginalRows | Where-Object outcome -cne 'Passed')
$taskExpectedFailure='SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests.V145_S01_GracefulStopPreservesCurrentCycleAndRequiresSeparateArm(stage: "AckHigh")'
if($taskOriginalRows.Count -ne 2980 -or $taskFailed.Count -ne 1 -or $taskFailed[0].testName -cne $taskExpectedFailure) {
    throw 'Original solution coverage or exact failure differs; this bounded composite is not applicable.'
}
$taskRecheckPath=Join-Path $taskRepo '.scratch/t53/staged-stop-recheck-01/staged-stop.trx'
$taskRecheckRows=@(Read-Trx $taskRecheckPath)
if($taskRecheckRows.Count -ne 7 -or @($taskRecheckRows | Where-Object testName -Match 'V145_S01_').Count -ne 5 -or
    @($taskRecheckRows | Where-Object testName -Match 'V142_R0[67]_').Count -ne 2 -or
    @($taskRecheckRows | Where-Object testName -ceq $taskExpectedFailure).Count -ne 1) { throw 'Exact bounded recheck scope differs.' }
$taskCovered=@(@($taskOriginalRows | Where-Object outcome -ceq 'Passed') + $taskRecheckRows |
    Select-Object -ExpandProperty testName | Sort-Object -Unique)
if($taskCovered.Count -ne 2980 -or @($taskCovered | Where-Object {$_ -match 'V153_'}).Count -ne 56) {
    throw 'Deduplicated solution coverage differs.'
}
$taskConsumerRows=@(Get-ChildItem -LiteralPath $PostTestRun -Recurse -File -Filter '*.trx' |
    ForEach-Object { Read-Trx $_.FullName })
if($taskConsumerRows.Count -ne 25) { throw 'Required consumer test execution count differs.' }
$taskBinaryPath=Join-Path $taskRepo '.scratch/t53/candidate-binary-evidence.json'
$taskBinary=Get-Content -LiteralPath $taskBinaryPath -Raw | ConvertFrom-Json
foreach($taskItem in @($taskBinary.testAssemblies)+@($taskBinary.libraries)) {
    if((Get-FileHash -LiteralPath $taskItem.path).Hash -cne $taskItem.sha256) { throw 'Captured candidate binary changed.' }
}
foreach($taskItem in $taskBinary.testAssemblies | Where-Object path -Match '\.Tests.dll$') {
    if([DateTimeOffset]$taskItem.lastWriteTimeUtc -gt [DateTimeOffset]$taskSolution.startedAtUtc) {
        throw 'Test binary timestamp does not predate original run; investigate identity before reuse.'
    }
}
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
$taskMigrationNames=@('store-migration-consumer.json','image-evidence-migration-consumer.json',
    'image-finalization-migration-consumer.json')+@(32..36 | ForEach-Object {'outbox-migration-'+$_+'-consumer.json'})
$taskMigrations=@()
foreach($taskName in $taskMigrationNames) {
    $taskMigrationRoot=if($taskName -ceq 'outbox-migration-35-consumer.json') { $Migration35Run }
        elseif($taskName -ceq 'outbox-migration-36-consumer.json') { $Migration36Run } else { $PostTestRun }
    $taskPath=Join-Path $taskMigrationRoot $taskName
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or $taskReport.cases.Count -ne 14 -or
        (@($taskReport.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
        @($taskReport.cases | Where-Object result -cne 'Pass').Count -or
        $taskReport.current.runtimeHash -cne $taskRuntimeHash -or
        (Get-FileHash -LiteralPath $taskReport.current.runtime).Hash -cne $taskRuntimeHash) { throw ('Incomplete migration: '+$taskName) }
    if($taskName -match '^outbox-migration' -and (-not $taskReport.fullPhaseCoverage -or
        @($taskReport.cases | Where-Object { -not $_.oldDatabaseAndWalUnchanged -or -not $_.oldWriterAuditUnchanged -or
            $_.seedPostWriteAuditSequence -le 0 -or $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence -or
            $_.currentPostWriteAuditSequence -le 0 -or $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence }).Count)) {
        throw ('Migration immutable bytes or full audit watermark evidence incomplete: '+$taskName)
    }
    Add-Artifact $taskPath 'Migration'
    $taskMigrations += [ordered]@{path=$taskPath;phases=14;oldRuntimeHash=$taskReport.old.runtimeHash;currentRuntimeHash=$taskRuntimeHash}
}
$taskPackages=@(Get-ChildItem -LiteralPath (Join-Path $PostTestRun 'packages') -File -Filter '*.nupkg')
if($taskPackages.Count -ne 8) { throw 'Eight final packages required.' }
foreach($taskPackage in $taskPackages) { Add-Artifact $taskPackage.FullName 'Package' }
$taskAbiPath='D:\s53abi\abi-package-binding.json'
$taskAbi=Get-Content -LiteralPath $taskAbiPath -Raw | ConvertFrom-Json
if($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or $taskAbi.libraries.Count -ne 4) { throw 'Legacy ABI evidence missing.' }
foreach($taskLibrary in $taskAbi.libraries) {
    $taskZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $PostTestRun ('packages/'+$taskLibrary.package)))
    try {
        $taskEntry=$taskZip.GetEntry($taskLibrary.entry)
        if($null -eq $taskEntry) { throw 'ABI package entry missing.' }
        $taskStream=$taskEntry.Open()
        try { $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)) }
        finally { $taskStream.Dispose() }
        if($taskHash -cne $taskLibrary.sha256) { throw 'Legacy ABI proof differs from final package.' }
    } finally { $taskZip.Dispose() }
}
Add-Artifact $taskAbiPath 'LegacyAbi'
foreach($taskPath in @((Join-Path $SolutionRun 'validation.json'),(Join-Path $SolutionRun 'build.log'),
    (Join-Path $SolutionRun 'tests.log'),(Join-Path $PostTestRun 'validation.json'),$taskBinaryPath)) { Add-Artifact $taskPath 'ValidationProvenance' }
foreach($taskName in 'seed-writer.json','seed.stdout.log','seed.stderr.log','store.sqlite','store.sqlite-wal') {
    Add-Artifact (Join-Path $taskSeedRoot $taskName) 'OriginalPostTestFailure'
}
foreach($taskName in 'backend-review-result.md','backend-review-disposition.md','wpf-review-result.md',
    'wpf-review-disposition.md','staged-stop-architecture.md','old-seed-architecture.md','old-seed-cold-query.json','post-test-script-provenance.json','terminal-observations.json',
    'post-test-validation-02.log','full-migration-35-recheck-01.log','full-migration-36-01.log') {
    Add-Artifact (Join-Path $taskRepo ('.scratch/t53/'+$taskName)) 'ReviewOrScriptProvenance'
}
[void][IO.Directory]::CreateDirectory($Destination)
$taskComposite=[ordered]@{kind='V1-53-composite-validation';result='PassWithRecordedFailures';
    executionMode='OriginalSolutionFail_BoundedRecheck_PostTestBoundedContinuation';
    startingHead=$taskHead;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    originalSolutionRun=$SolutionRun;originalSolutionResult='Fail';originalExecutions=$taskOriginalRows.Count;
    originalFailureCount=1;originalFailure=$taskExpectedFailure;originalSkipped=0;
    recheckExecutions=7;recheckFailures=0;distinctSolutionCases=$taskCovered.Count;v153Cases=56;
    postTestRun=$PostTestRun;originalPostTestResult='Fail';originalPostTestFailureCount=1;
    originalPostTestFailure='Schema35 phase2 old seed returned TraceAuditUnavailable before migration; no durable command facts.';
    postTestResult='CompletedByBoundedContinuation';consumerTestExecutions=$taskConsumerRows.Count;
    packages=8;legacyAbi='Pass';migrations=$taskMigrations;sourceHashes=$taskSources;artifacts=$taskArtifacts.ToArray();
    binaryIdentityBasis='Hashes captured at 2026-09-15 00:11 local while full solution execution was still running, after the bounded recheck. Test assembly last-write times predate the original run; no-build invocation and unchanged source manifests retained. These are not retrospectively claimed as pre-run hashes.';
    limit='Both original invocations remain Fail. Seven duplicate solution rechecks and repeated schema35 migration phases do not add distinct cases. The exact original trigger rejection and old-seed internal append failure remain unproven; passing rechecks are not claimed as fixes. Product and probe source were not changed for either recheck.';
    productionQualification='NotRun';publicNugetPublication='NotRun'}
$taskComposite | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $Destination 'ticket53-composite-evidence.json') -Encoding utf8
$taskComposite | Select-Object result,originalExecutions,originalFailureCount,recheckExecutions,distinctSolutionCases,v153Cases,consumerTestExecutions,packages | ConvertTo-Json
