$ErrorActionPreference='Stop'
$taskOriginal='F:\si55mfinal\continuation-manifest.json'
$taskRun='F:\si55m38final'
if(Test-Path -LiteralPath $taskRun){throw 'Remaining migration requires a fresh directory.'}
$taskManifest=Get-Content -LiteralPath $taskOriginal -Raw | ConvertFrom-Json
$taskInterruption=Get-Content -LiteralPath .scratch/t55/migration-interruption-56.json -Raw | ConvertFrom-Json
if($taskManifest.result -cne 'NotRun' -or ($taskManifest.completedSchemas -join ',') -cne '35,36,37' -or
    (Get-FileHash -LiteralPath $taskOriginal).Hash -cne $taskInterruption.manifestSha256 -or
    $taskInterruption.matchingValidationProcesses -ne 0 -or $taskInterruption.retentionPhaseDirectories -ne 0) {
    throw 'Verified interrupted state required.'
}
foreach($taskSource in $taskManifest.sourceHashes){
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256){throw 'Source changed since completed migration paths.'}
}
$taskReports=foreach($taskSchema in 35,36,37){
    $taskPath='F:\si55mfinal\evidence-reconciliation-migration-'+$taskSchema+'-consumer.json'
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or $taskReport.cases.Count -ne 14 -or @($taskReport.cases | Where-Object result -cne 'Pass').Count){throw 'Earlier path incomplete.'}
    [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash}
}
[void][IO.Directory]::CreateDirectory($taskRun)
$taskPath=Join-Path $taskRun 'continuation-manifest.json'
$taskManifest | Add-Member -NotePropertyMembers @{
    interruptedManifest=$taskOriginal;interruptedManifestSha256=$taskInterruption.manifestSha256;
    interruptionEvidence='.scratch/t55/migration-interruption-56.json';
    interruptionEvidenceSha256=(Get-FileHash -LiteralPath '.scratch/t55/migration-interruption-56.json').Hash;
    reusedMigrationReports=@($taskReports);remainingRun=$taskRun;remainingStartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskPath -Encoding utf8
$env:TEMP='F:\temp\SharpInspect-T55'
$env:TMP=$env:TEMP
$env:NUGET_HTTP_CACHE_PATH=Join-Path $env:TEMP 'nuget-http'
try {
    & tools/Test-RetentionMigrationConsumer.ps1 -Run $taskRun -PackageFeed $taskManifest.packageFeed `
        -SourceSchema 38 -SourcePackageFeed 'E:\si54p1\ticket54\20260915-030352-254\packages' `
        -DependencyPackageFeed 'E:\nuget\.nuget\packages'
    $taskManifest.completedSchemas += 38
    foreach($taskSource in $taskManifest.sourceHashes){
        if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256){throw 'Source changed during final migration.'}
    }
    if((Get-FileHash -LiteralPath $taskOriginal).Hash -cne $taskInterruption.manifestSha256){throw 'Interrupted manifest changed.'}
    $taskManifest.result='Pass'
    $taskManifest | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
    $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskPath -Encoding utf8
    Write-Output 'T55 remaining migration PASS: source 38, all 14 phases; earlier 42 phases and interruption preserved.'
} catch {
    $taskManifest.result='Fail'
    $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskPath -Encoding utf8
    throw
}
