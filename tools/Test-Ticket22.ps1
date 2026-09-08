param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 22 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
