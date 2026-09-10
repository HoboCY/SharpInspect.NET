param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$PackageFeed)
$ErrorActionPreference = 'Stop'
$transferRepo = Split-Path -Parent $PSScriptRoot
$transferRun = [IO.Path]::GetFullPath($Run)
$transferCopy = Join-Path $transferRun 'recipe-transfer-consumer'
$transferEvidence = Join-Path $transferRun 'recipe-transfer-demo'
if (Test-Path -LiteralPath $transferCopy) { throw 'Use a fresh Recipe transfer consumer directory.' }
[void][IO.Directory]::CreateDirectory($transferCopy)
[void][IO.Directory]::CreateDirectory($transferEvidence)
$transferSource = Join-Path $transferRepo 'samples/SharpInspect.SampleHost'
foreach ($transferFile in Get-ChildItem -LiteralPath $transferSource -Recurse -File) {
    $transferRelative = [IO.Path]::GetRelativePath($transferSource, $transferFile.FullName)
    if ($transferRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $transferTarget = Join-Path $transferCopy $transferRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $transferTarget))
    Copy-Item -LiteralPath $transferFile.FullName -Destination $transferTarget
}
Copy-Item -LiteralPath (Join-Path $transferRepo 'Directory.Build.props') -Destination $transferCopy
$transferProject = Join-Path $transferCopy 'SharpInspect.SampleHost.csproj'
$transferConfig = Join-Path $transferCopy 'NuGet.Config'
$transferXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageFeed)) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($transferConfig, $transferXml, [Text.UTF8Encoding]::new($false))
function Invoke-TransferDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $transferRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Recipe transfer consumer failed: $LogName ($LASTEXITCODE)" }
}
Invoke-TransferDotnet 'recipe-transfer-consumer-restore.log' @('restore', $transferProject,
    '-p:UseLocalPackages=true', '--configfile', $transferConfig, '--packages', (Join-Path $transferRun 'recipe-transfer-consumer-cache'))
Invoke-TransferDotnet 'recipe-transfer-consumer-build.log' @('build', $transferProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$transferAssets = Get-Content -LiteralPath (Join-Path $transferCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($transferAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Recipe transfer consumer still references source projects.'
}
foreach ($transferPackage in 'Abstractions', 'Runtime', 'Wpf') {
    if (-not $transferAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $transferPackage + '/0.1.0-dev.1')]) {
        throw "Recipe transfer consumer package missing: $transferPackage"
    }
}
$transferDll = Join-Path $transferCopy 'bin/Release/net6.0-windows/SharpInspect.SampleHost.dll'
Invoke-TransferDotnet 'recipe-transfer-consumer-run.log' @($transferDll, '--recipe-transfer-check', $transferEvidence)
Invoke-TransferDotnet 'recipe-transfer-consumer-query.log' @($transferDll, '--recipe-transfer-query', $transferEvidence)
$transferDetail = Get-Content -LiteralPath (Join-Path $transferEvidence 'recipe-transfer-evidence.json') -Raw | ConvertFrom-Json
$transferCold = Get-Content -LiteralPath (Join-Path $transferEvidence 'recipe-transfer-restart.json') -Raw | ConvertFrom-Json
if ($transferDetail.Result -cne 'Pass' -or $transferDetail.CaseId -cne 'V138_N01' -or $transferDetail.SchemaVersion -ne 24 -or
    $transferDetail.ConsumerSha256 -cne (Get-FileHash -LiteralPath $transferDll).Hash -or
    $transferDetail.Disposition -cne 'Rejected' -or $transferDetail.Audit -cne 'Persisted' -or
    $transferDetail.ImportedDraftCount -ne 0 -or $transferDetail.ProductionAuthority -cne $false -or
    $transferCold.CaseId -cne 'V138_N02' -or $transferCold.Result -cne 'Pass' -or
    $transferCold.ReadOnly -cne $true -or $transferCold.WriterStarted -cne $false -or $transferCold.DatabaseUnchanged -cne $true) {
    throw 'Recipe transfer consumer evidence failed.'
}
Write-Output 'V138 independent NuGet consumer PASS; authenticated positive transfer is covered by controlled Runtime tests.'
