param([string]$ArtifactRoot,
    [string]$Schema32PackageFeed = $env:SHARPINSPECT_SCHEMA32_PACKAGE_FEED,
    [string]$Schema33PackageFeed = $env:SHARPINSPECT_SCHEMA33_PACKAGE_FEED)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 50 -ArtifactRoot $ArtifactRoot -Schema32PackageFeed $Schema32PackageFeed -Schema33PackageFeed $Schema33PackageFeed
exit $LASTEXITCODE
