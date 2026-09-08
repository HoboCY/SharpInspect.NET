param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 16 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
