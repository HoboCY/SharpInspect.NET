param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 18 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
