param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 24 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
