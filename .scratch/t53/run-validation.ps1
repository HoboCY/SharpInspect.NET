$ErrorActionPreference = 'Stop'
$taskTemp = 'D:\si53tmp'
New-Item -ItemType Directory -Force -Path $taskTemp | Out-Null
$env:TEMP = $taskTemp
$env:TMP = $taskTemp
$env:RestoreSources = 'E:\nuget\.nuget\packages'
$taskFeeds = Get-Content -LiteralPath '.scratch/t53/preserved-feeds.json' -Raw | ConvertFrom-Json
foreach ($taskFeed in $taskFeeds) {
    foreach ($taskPackage in $taskFeed.packages) {
        if ((Get-FileHash -LiteralPath $taskPackage.path -Algorithm SHA256).Hash -ne $taskPackage.sha256) {
            throw 'Preserved package hash changed'
        }
    }
}
$taskArgs = @{
    ArtifactRoot = 'D:\si53v'
    Schema32PackageFeed = ($taskFeeds | Where-Object schema -eq 32).feed
    Schema33PackageFeed = ($taskFeeds | Where-Object schema -eq 33).feed
    Schema34PackageFeed = ($taskFeeds | Where-Object schema -eq 34).feed
    Schema35PackageFeed = ($taskFeeds | Where-Object schema -eq 35).feed
    Schema36PackageFeed = ($taskFeeds | Where-Object schema -eq 36).feed
}
& ./tools/Test-Ticket53.ps1 @taskArgs *> .scratch/t53/validation-04.log
$taskResult = $LASTEXITCODE
Get-Content -LiteralPath .scratch/t53/validation-04.log -Tail 35
exit $taskResult
