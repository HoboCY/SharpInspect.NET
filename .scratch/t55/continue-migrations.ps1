$ErrorActionPreference='Stop'
$taskBase='F:\si55v3\ticket55\20260916-011049-854'
$taskRun='F:\si55mfinal'
if(Test-Path -LiteralPath $taskRun) {throw 'Use a fresh bounded continuation directory.'}
$taskBaseManifest=Get-Content -LiteralPath (Join-Path $taskBase 'validation.json') -Raw | ConvertFrom-Json
if($taskBaseManifest.result -cne 'Fail') {throw 'The original full-run failure must remain recorded.'}
$taskAllowed=@('tools\evidence-reconciliation-migration-probe\Program.cs','tools\Test-EvidenceReconciliationMigrationConsumer.ps1')
$taskSources=foreach($taskSource in $taskBaseManifest.sourceHashes) {
    $taskHash=(Get-FileHash -LiteralPath $taskSource.path).Hash
    if($taskSource.path -cnotin $taskAllowed -and $taskHash -cne $taskSource.sha256) {throw ('Unrelated frozen source changed: '+$taskSource.path)}
    [ordered]@{path=$taskSource.path;sha256=$taskHash}
}
$taskChanges=@(foreach($taskSource in $taskBaseManifest.sourceHashes | Where-Object path -cin $taskAllowed) {
    $taskHash=(Get-FileHash -LiteralPath $taskSource.path).Hash
    if($taskHash -ceq $taskSource.sha256) {throw 'Expected probe repair is not present.'}
    [ordered]@{path=$taskSource.path;before=$taskSource.sha256;after=$taskHash}
})
if($taskChanges.Count -ne 2) {throw 'Exactly two probe files are expected.'}
$taskFeeds=@{}
foreach($taskFeed in Get-Content -LiteralPath .scratch/t54/preserved-feeds.json -Raw | ConvertFrom-Json) {
    foreach($taskPackage in $taskFeed.packages) {
        if((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) {throw 'Preserved package changed.'}
    }
    $taskFeeds[[int]$taskFeed.schema]=$taskFeed.feed
}
$taskFeeds[38]='E:\si54p1\ticket54\20260915-030352-254\packages'
$taskPackageFeed=Join-Path $taskBase 'packages'
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
if($taskRuntimeHash -cne 'F0C61B284BA560D3C06EFFEA95801C2FB8FAC9DA3F4F39A3E4BA141B8779B7A2') {throw 'Validated product binary changed.'}
[void][IO.Directory]::CreateDirectory($taskRun)
$taskManifestPath=Join-Path $taskRun 'continuation-manifest.json'
$taskManifest=[ordered]@{kind='V1-55-bounded-migration-continuation';result='NotRun';originalRun=$taskBase;
    originalResult='Fail';originalManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskBase 'validation.json')).Hash;
    runtimeSha256=$taskRuntimeHash;sourceChanges=$taskChanges;sourceHashes=@($taskSources);
    packageFeed=$taskPackageFeed;completedSchemas=@();startedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
$env:TEMP='F:\temp\SharpInspect-T55'
$env:TMP=$env:TEMP
$env:NUGET_HTTP_CACHE_PATH=Join-Path $env:TEMP 'nuget-http'
try {
    foreach($taskSchema in 35,36,37) {
        & tools/Test-EvidenceReconciliationMigrationConsumer.ps1 -Run $taskRun -PackageFeed $taskPackageFeed `
            -SourceSchema $taskSchema -SourcePackageFeed $taskFeeds[$taskSchema] -DependencyPackageFeed 'E:\nuget\.nuget\packages'
        $taskManifest.completedSchemas += $taskSchema
        $taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
    }
    & tools/Test-RetentionMigrationConsumer.ps1 -Run $taskRun -PackageFeed $taskPackageFeed `
        -SourceSchema 38 -SourcePackageFeed $taskFeeds[38] -DependencyPackageFeed 'E:\nuget\.nuget\packages'
    $taskManifest.completedSchemas += 38
    foreach($taskSource in $taskSources) {
        if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) {throw 'Source changed during bounded validation.'}
    }
    foreach($taskFile in (Get-Content -LiteralPath .scratch/t55/migration-failure-53.json -Raw | ConvertFrom-Json).files) {
        if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Retained failure evidence changed.'}
    }
    $taskManifest.result='Pass'
    $taskManifest.completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
    Write-Output 'T55 bounded migration continuation PASS: schemas 35,36,37,38; 56 phases; product binary unchanged.'
} catch {
    $taskManifest.result='Fail'
    $taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
    throw
}
