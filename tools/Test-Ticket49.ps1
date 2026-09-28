param([string]$ArtifactRoot, [string]$Schema32PackageFeed = $env:SHARPINSPECT_SCHEMA32_PACKAGE_FEED)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 49 -ArtifactRoot $ArtifactRoot -Schema32PackageFeed $Schema32PackageFeed
exit $LASTEXITCODE
