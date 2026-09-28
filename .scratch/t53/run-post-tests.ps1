$ErrorActionPreference='Stop'
$env:TEMP='D:\si53tmp'
$env:TMP=$env:TEMP
[Environment]::SetEnvironmentVariable('RestoreSources',$null,'Process')
$env:RestoreFallbackFolders='E:\nuget\.nuget\packages'
$taskParent=Get-Content -LiteralPath D:/si53v/ticket53/20260914-235726-414/validation.json -Raw | ConvertFrom-Json
foreach($taskSource in $taskParent.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Candidate differs from running full validation.' }
}
$taskFeeds=Get-Content -LiteralPath .scratch/t53/preserved-feeds.json -Raw | ConvertFrom-Json
foreach($taskFeed in $taskFeeds) { foreach($taskPackage in $taskFeed.packages) {
    if((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) { throw 'Preserved feed changed.' }
}}
$taskArgs=@{Ticket=53;ArtifactRoot='D:\si53p';
    Schema32PackageFeed=($taskFeeds | Where-Object schema -eq 32).feed;
    Schema33PackageFeed=($taskFeeds | Where-Object schema -eq 33).feed;
    Schema34PackageFeed=($taskFeeds | Where-Object schema -eq 34).feed;
    Schema35PackageFeed=($taskFeeds | Where-Object schema -eq 35).feed;
    Schema36PackageFeed=($taskFeeds | Where-Object schema -eq 36).feed}
& ./.scratch/t53/post-test-validation.generated.ps1 @taskArgs *> .scratch/t53/post-test-validation-02.log
