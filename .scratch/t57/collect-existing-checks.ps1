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
$taskNew=@($taskCases | Where-Object testName -match 'V157_')
if($taskNew.Count -ne 39 -or $taskCases.Count -ne 3204 -or $taskConsumerCount -ne 25) {throw 'Unexpected test denominator.'}
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
