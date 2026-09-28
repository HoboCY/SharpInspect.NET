$ErrorActionPreference = 'Stop'
$env:TEMP = 'F:\temp\SharpInspect-T56'
[void][IO.Directory]::CreateDirectory($env:TEMP)
$env:TMP = $env:TEMP
$env:NUGET_HTTP_CACHE_PATH = Join-Path $env:TEMP 'nuget-http'
$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'safe.directory'
$env:GIT_CONFIG_VALUE_0 = 'D:/projects/SharpInspect.NET'
$PSDefaultParameterValues['*:DependencyPackageFeed'] = 'E:\nuget\.nuget\packages'
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
& tools/Test-Development.ps1 -Ticket 56 -ArtifactRoot F:\si56v1 @taskFeeds
exit $LASTEXITCODE


