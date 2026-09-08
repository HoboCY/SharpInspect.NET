param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 23 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
