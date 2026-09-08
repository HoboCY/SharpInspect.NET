param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 21 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
