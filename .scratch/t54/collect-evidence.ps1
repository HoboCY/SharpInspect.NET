param([string]$SolutionRun='E:\si54s1',
    [string]$PostRun='E:\si54p1\ticket54\20260915-030352-254',
    [string]$AbiRun='E:\s54abi')
$ErrorActionPreference='Stop'
$taskSolution=Get-Content -LiteralPath (Join-Path $SolutionRun 'manifest.json') -Raw | ConvertFrom-Json
$taskPost=Get-Content -LiteralPath (Join-Path $PostRun 'validation.json') -Raw | ConvertFrom-Json
if($taskSolution.result -cne 'Pass' -or $taskPost.result -cne 'Fail' -or
   $taskPost.kind -cne 'V1-54-post-test-validation' -or $taskPost.executionMode -cne 'PostTestOnly') {
    throw 'Expected passing solution and explicitly retained failed original post-test run.'
}
$taskOldFailure=Get-Content .scratch/t54/old-seed-cold-query.json -Raw | ConvertFrom-Json
if((Get-FileHash -LiteralPath $taskOldFailure.database).Hash -cne $taskOldFailure.sha256 -or
   $taskOldFailure.commandAttempts -ne 0 -or $taskOldFailure.commandFacts -ne 0 -or
   $taskOldFailure.internalAppendFailureReason -cne 'Unproven' -or
   (Get-FileHash tools/outbox-migration-probe/Program.cs).Hash -cne $taskOldFailure.sourceProbeSha256) {
    throw 'Original failed seed or unchanged probe identity differs'
}
foreach($taskSource in @($taskSolution.sourceHashes)+@($taskPost.sourceHashes)) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) {throw 'Validated source changed'}
}
foreach($taskBinary in $taskSolution.binaries) {
    if((Get-FileHash -LiteralPath $taskBinary.path).Hash -cne $taskBinary.sha256) {throw 'Validated binary changed'}
}
$taskCases=@()
$taskSuites=@()
foreach($taskRoot in @((Join-Path $SolutionRun 'tests'),$PostRun)) {
    foreach($taskFile in Get-ChildItem -LiteralPath $taskRoot -Recurse -File -Filter '*.trx') {
        [xml]$taskXml=Get-Content -LiteralPath $taskFile.FullName -Raw
        $taskCounters=$taskXml.TestRun.ResultSummary.Counters
        $taskRows=@($taskXml.TestRun.Results.UnitTestResult)
        if([int]$taskCounters.total -le 0 -or [int]$taskCounters.total -ne [int]$taskCounters.passed -or
           [int]$taskCounters.notExecuted -ne 0 -or $taskRows.Count -ne [int]$taskCounters.total -or
           @($taskRows | Where-Object outcome -cne 'Passed').Count) {throw ('Incomplete TRX: '+$taskFile.FullName)}
        $taskSuites += [ordered]@{path=$taskFile.FullName;sha256=(Get-FileHash $taskFile.FullName).Hash;total=$taskRows.Count}
        if($taskRoot -eq (Join-Path $SolutionRun 'tests')) {$taskCases += $taskRows}
    }
}
$taskNew=@($taskCases | Where-Object testName -match 'V154_')
if($taskNew.Count -ne 55 -or $taskCases.Count -ne 3035) {throw ('Unexpected solution totals: '+$taskCases.Count+'; T54='+$taskNew.Count)}
$taskPreservedFeeds=Get-Content .scratch/t54/preserved-feeds.json -Raw | ConvertFrom-Json
foreach($taskFeed in $taskPreservedFeeds) { foreach($taskPackage in $taskFeed.packages) {
    if((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) {throw 'Preserved source package changed'}
}}
$taskOldPackageHashes=@($taskPreservedFeeds | ForEach-Object { $_.packages } |
    Where-Object { $_.path -match 'SharpInspect\.NET\.Runtime\.' } | ForEach-Object { $_.sha256 })
$taskCurrentPackageHash=(Get-FileHash -LiteralPath (Join-Path $PostRun 'packages/SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg')).Hash
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
$taskMigrationNames=@('store-migration-consumer.json','image-evidence-migration-consumer.json','image-finalization-migration-consumer.json')+
    @(32..36 | ForEach-Object {'outbox-migration-'+$_+'-consumer.json'})+
    @(35..37 | ForEach-Object {'evidence-reconciliation-migration-'+$_+'-consumer.json'})
$taskMigrations=foreach($taskName in $taskMigrationNames) {
    $taskMigrationRoot = switch -Regex ($taskName) {
        '^outbox-migration-35-' { 'E:\si54m35'; break }
        '^outbox-migration-36-' { 'E:\si54m36'; break }
        '^evidence-reconciliation-migration-(35|36|37)-' { 'E:\si54r'+$Matches[1]; break }
        default { $PostRun }
    }
    $taskPath=Join-Path $taskMigrationRoot $taskName
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or $taskReport.cases.Count -ne 14 -or
       (@($taskReport.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
       @($taskReport.cases | Where-Object result -cne 'Pass').Count -or
       $taskReport.current.runtimeHash -cne $taskRuntimeHash -or
       (Get-FileHash -LiteralPath $taskReport.current.runtime).Hash -cne $taskRuntimeHash -or
       (Get-FileHash -LiteralPath $taskReport.old.runtime).Hash -cne $taskReport.old.runtimeHash -or
       $taskReport.old.packageHash -cnotin $taskOldPackageHashes -or
       $taskReport.current.packageHash -cne $taskCurrentPackageHash) {
        throw ('Migration identity or phases incomplete: '+$taskName)
    }
    if($taskName -match '^(outbox|evidence-reconciliation)-migration') {
        if(-not $taskReport.fullPhaseCoverage -or @($taskReport.cases | Where-Object {
            -not $_.oldDatabaseAndWalUnchanged -or -not $_.oldWriterAuditUnchanged -or
            $_.currentPostWriteAuditSequence -le 0 -or $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence -or
            $_.seedPostWriteAuditSequence -le 0 -or $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence
        }).Count) {throw ('Migration writer invariants incomplete: '+$taskName)}
    }
    if($taskName -match '^evidence-reconciliation' -and
       ($taskReport.targetSchemaVersion -ne 38 -or @($taskReport.cases | Where-Object {
            -not $_.sourceRowsPreserved -or -not $_.operations.startupCompleted -or
            -not $_.operations.serviceResolved -or $_.operations.PendingQuarantines -ne 0 -or
            $_.operations.IntegrityFaultRecorded
       }).Count)) {throw ('T54 migration invariants incomplete: '+$taskName)}
    [ordered]@{path=$taskPath;sha256=(Get-FileHash $taskPath).Hash;phases=14;oldRuntimeHash=$taskReport.old.runtimeHash;runtimeHash=$taskRuntimeHash}
}
$taskPackages=@(Get-ChildItem -LiteralPath (Join-Path $PostRun 'packages') -File -Filter '*.nupkg')
if($taskPackages.Count -ne 8) {throw 'Eight final packages required'}
$taskAbi=Get-Content -LiteralPath (Join-Path $AbiRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
if($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or $taskAbi.libraries.Count -ne 4) {throw 'Legacy ABI evidence missing'}
foreach($taskLibrary in $taskAbi.libraries) {
    $taskArchive=[IO.Compression.ZipFile]::OpenRead((Join-Path $PostRun ('packages/'+$taskLibrary.package)))
    try {
        $taskEntry=$taskArchive.GetEntry($taskLibrary.entry)
        if($null -eq $taskEntry) {throw 'ABI package entry missing'}
        $taskStream=$taskEntry.Open()
        try {$taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream))} finally {$taskStream.Dispose()}
        if($taskHash -cne $taskLibrary.sha256) {throw 'ABI package binding differs'}
    } finally {$taskArchive.Dispose()}
}
$taskEvidence=[ordered]@{kind='V1-54-final-development-evidence';result='PassWithRecordedFailures';originalPostTestResult='Fail';
    retainedPostTestFailure=$taskOldFailure;old35MigrationRecheck='Complete14StageBoundedRecheck';remainingMigrationScopes='ExecutedSeparatelyAfterOriginalFailure';
    retainedFailureArtifacts=@('.scratch/t54/old-seed-cold-query.json',
        '.scratch/t54/post-test-validation.log','.scratch/t54/post-test-terminal-error.txt',
        (Join-Path $PostRun 'outbox-migration-35-consumer/phase-6/seed-writer.json'),
        (Join-Path $PostRun 'outbox-migration-35-consumer/phase-6/seed.stdout.log'),
        (Join-Path $PostRun 'outbox-migration-35-consumer/phase-6/seed.stderr.log')) | ForEach-Object {
            [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}
        };
    continuationLogSha256=(Get-FileHash .scratch/t54/migration-continuation.log).Hash;
    scopeReviewSha256=(Get-FileHash .scratch/t54/architecture-evidence-scope-review.md).Hash;
    solutionRun=$SolutionRun;postTestRun=$PostRun;solutionManifestSha256=(Get-FileHash (Join-Path $SolutionRun 'manifest.json')).Hash;
    postTestManifestSha256=(Get-FileHash (Join-Path $PostRun 'validation.json')).Hash;
    sourceFiles=$taskPost.sourceHashes.Count;solutionCases=$taskCases.Count;v154Cases=$taskNew.Count;
    failed=0;skipped=0;packages=@($taskPackages | ForEach-Object {[ordered]@{path=$_.FullName;sha256=(Get-FileHash $_.FullName).Hash}});
    suites=$taskSuites;migrations=@($taskMigrations);migrationPaths=$taskMigrations.Count;migrationPhases=($taskMigrations.Count*14);
    legacyAbi='Pass';abiEvidence=(Join-Path $AbiRun 'abi-package-binding.json');
    abiEvidenceSha256=(Get-FileHash (Join-Path $AbiRun 'abi-package-binding.json')).Hash;
    review='.scratch/t54/architecture-final-review.md';
    reviewSha256=(Get-FileHash .scratch/t54/architecture-final-review.md).Hash;
    developmentFailuresRetained=$true;productionQualification='NotRun';publicNugetPublication='NotRun';
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$taskEvidence | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath .scratch/t54/final-evidence.json -Encoding utf8
[pscustomobject]$taskEvidence | Select-Object result,sourceFiles,solutionCases,v154Cases,migrationPaths,migrationPhases,legacyAbi | ConvertTo-Json
