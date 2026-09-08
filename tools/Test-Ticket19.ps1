param([string]$ArtifactRoot)
& (Join-Path $PSScriptRoot 'Test-Development.ps1') -Ticket 19 -ArtifactRoot $ArtifactRoot
exit $LASTEXITCODE
