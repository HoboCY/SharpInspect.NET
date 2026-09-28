$ErrorActionPreference = 'Stop'
$env:TEMP = 'F:\t58'
$env:TMP = $env:TEMP
$env:NUGET_HTTP_CACHE_PATH = Join-Path $env:TEMP 'nuget-http'
$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'safe.directory'
$env:GIT_CONFIG_VALUE_0 = 'D:/projects/SharpInspect.NET'
$PSDefaultParameterValues['*:DependencyPackageFeed'] = 'E:\nuget\.nuget\packages'
$PSDefaultParameterValues['*:DependencyFeed'] = 'E:\nuget\.nuget\packages'
$taskFeeds = @{}
foreach ($taskFeed in (Get-Content -LiteralPath .scratch/t54/preserved-feeds.json -Raw | ConvertFrom-Json)) {
    foreach ($taskPackage in $taskFeed.packages) {
        if ((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) {
            throw 'Preserved migration package identity changed.'
        }
    }
    $taskFeeds['Schema' + $taskFeed.schema + 'PackageFeed'] = $taskFeed.feed
}
$taskFeeds['Schema38PackageFeed'] = 'E:\si54p1\ticket54\20260915-030352-254\packages'
$taskFeeds['Schema39PackageFeed'] = 'F:\si57c31\ticket57\20260916-160130-463\packages'
foreach ($taskFeed in (Get-Content -LiteralPath .scratch/t58/preserved-feeds.json -Raw | ConvertFrom-Json)) {
    foreach ($taskPackage in $taskFeed.packages) {
        if ((Get-FileHash -LiteralPath $taskPackage.path).Hash -cne $taskPackage.sha256) {
            throw 'T58 preserved migration package identity changed.'
        }
    }
}
& tools/Test-Ticket58.ps1 -ArtifactRoot F:\si58v16 @taskFeeds
exit $LASTEXITCODE
