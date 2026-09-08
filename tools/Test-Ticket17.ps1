param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 17 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
