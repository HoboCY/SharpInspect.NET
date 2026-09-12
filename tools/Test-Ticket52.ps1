param([string]$ArtifactRoot,
    [string]$Schema32PackageFeed = $env:SHARPINSPECT_SCHEMA32_PACKAGE_FEED,
    [string]$Schema33PackageFeed = $env:SHARPINSPECT_SCHEMA33_PACKAGE_FEED,
    [string]$Schema34PackageFeed = $env:SHARPINSPECT_SCHEMA34_PACKAGE_FEED,
    [string]$Schema35PackageFeed = $env:SHARPINSPECT_SCHEMA35_PACKAGE_FEED)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 52 -ArtifactRoot $ArtifactRoot -Schema32PackageFeed $Schema32PackageFeed -Schema33PackageFeed $Schema33PackageFeed -Schema34PackageFeed $Schema34PackageFeed -Schema35PackageFeed $Schema35PackageFeed
exit $LASTEXITCODE
