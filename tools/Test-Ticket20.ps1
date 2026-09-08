param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 20 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
