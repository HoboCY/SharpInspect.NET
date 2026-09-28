$ErrorActionPreference = 'Stop'
$taskArtifactRoot = 'F:\temp\SharpInspect.NET-validation-artifacts'
$taskTicketRoot = Join-Path $taskArtifactRoot 'ticket40'
$taskTempRoot = Join-Path $taskTicketRoot 'process-temp'
[void][IO.Directory]::CreateDirectory($taskTempRoot)
$env:TEMP = $taskTempRoot
$env:TMP = $taskTempRoot
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
[ordered]@{
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    environmentScope = 'this PowerShell process and children only'
    temp = $env:TEMP
    tmp = $env:TMP
    MSBUILDDISABLENODEREUSE = $env:MSBUILDDISABLENODEREUSE
    DOTNET_CLI_USE_MSBUILD_SERVER = $env:DOTNET_CLI_USE_MSBUILD_SERVER
    UseSharedCompilation = $env:UseSharedCompilation
    drives = @(Get-PSDrive -Name C,D,E,F | Select-Object Name,Free,Used)
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskTicketRoot 'final-gate-environment.json') -Encoding utf8
& (Join-Path $PSScriptRoot '..\..\tools\Test-Development.ps1') -Ticket 40 -ArtifactRoot $taskArtifactRoot


