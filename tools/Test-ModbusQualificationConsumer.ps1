param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)

$ErrorActionPreference = 'Stop'
$qualificationRepo = Split-Path -Parent $PSScriptRoot
$qualificationRun = [IO.Path]::GetFullPath($Run)
$qualificationCopy = Join-Path $qualificationRun 'modbus-qualification-consumer'
$qualificationEvidence = Join-Path $qualificationRun 'modbus-qualification-demo'
if (Test-Path -LiteralPath $qualificationCopy) { throw 'Use a fresh qualification consumer directory.' }
[void][IO.Directory]::CreateDirectory($qualificationCopy)
[void][IO.Directory]::CreateDirectory($qualificationEvidence)
$qualificationSource = Join-Path $qualificationRepo 'samples/SharpInspect.SampleHost'
foreach ($qualificationFile in Get-ChildItem -LiteralPath $qualificationSource -Recurse -File) {
    $qualificationRelative = [IO.Path]::GetRelativePath($qualificationSource, $qualificationFile.FullName)
    if ($qualificationRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $qualificationTarget = Join-Path $qualificationCopy $qualificationRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $qualificationTarget))
    Copy-Item -LiteralPath $qualificationFile.FullName -Destination $qualificationTarget
}
Copy-Item -LiteralPath (Join-Path $qualificationRepo 'Directory.Build.props') -Destination $qualificationCopy
$qualificationProject = Join-Path $qualificationCopy 'SharpInspect.SampleHost.csproj'
$qualificationConfig = Join-Path $qualificationCopy 'NuGet.Config'
$qualificationXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageFeed)) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($qualificationConfig, $qualificationXml, [Text.UTF8Encoding]::new($false))

function Invoke-QualificationDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $qualificationRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Qualification consumer failed: $LogName ($LASTEXITCODE)" }
}
Invoke-QualificationDotnet 'modbus-qualification-consumer-restore.log' @(
    'restore', $qualificationProject, '-p:UseLocalPackages=true', '--configfile', $qualificationConfig,
    '--packages', (Join-Path $qualificationRun 'modbus-qualification-consumer-cache'))
Invoke-QualificationDotnet 'modbus-qualification-consumer-build.log' @(
    'build', $qualificationProject, '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$qualificationAssets = Get-Content -LiteralPath (Join-Path $qualificationCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($qualificationAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Qualification consumer still references source projects.'
}
foreach ($qualificationPackage in 'Abstractions', 'Runtime', 'Wpf') {
    if (-not $qualificationAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $qualificationPackage + '/0.1.0-dev.1')]) {
        throw "Qualification consumer package missing: $qualificationPackage"
    }
}
$qualificationDll = Join-Path $qualificationCopy 'bin/Release/net6.0-windows/SharpInspect.SampleHost.dll'
Invoke-QualificationDotnet 'modbus-qualification-consumer-run.log' @(
    $qualificationDll, '--modbus-qualification-check', $qualificationEvidence)
Invoke-QualificationDotnet 'modbus-qualification-consumer-cold-query.log' @(
    $qualificationDll, '--modbus-qualification-query', $qualificationEvidence)
$qualificationDetail = Get-Content -LiteralPath (Join-Path $qualificationEvidence 'modbus-qualification-evidence.json') -Raw | ConvertFrom-Json
$qualificationCold = Get-Content -LiteralPath (Join-Path $qualificationEvidence 'modbus-qualification-restart.json') -Raw | ConvertFrom-Json
if ($qualificationDetail.Result -cne 'Pass' -or $qualificationDetail.CaseId -cne 'V140_N01' -or
    $qualificationDetail.SchemaVersion -ne 26 -or
    $qualificationDetail.ConsumerSha256 -cne (Get-FileHash -LiteralPath $qualificationDll -Algorithm SHA256).Hash -or
    $qualificationDetail.StartDisposition -cne 'Rejected' -or $qualificationDetail.Audit -cne 'Persisted' -or
    $qualificationDetail.FacilityOpenCount -ne 0 -or $qualificationDetail.SessionEventCount -ne 0 -or
    $qualificationDetail.QualificationRunCount -ne 0 -or $qualificationDetail.Ready -cne $false -or
    $qualificationDetail.Armed -cne $false -or $qualificationDetail.ProductionAuthority -cne $false -or
    $qualificationDetail.TargetActivationPresent -cne $false -or $qualificationDetail.OperatorAuthenticated -cne $false -or
    $qualificationDetail.PhysicalHardwareQualification -cne 'NotRun' -or
    $qualificationDetail.PositiveFacilityRecovery -cne 'NotRun' -or
    $qualificationCold.Result -cne 'Pass' -or $qualificationCold.CaseId -cne 'V140_N02' -or
    $qualificationCold.ReadOnly -cne $true -or $qualificationCold.WriterStarted -cne $false -or
    $qualificationCold.DatabaseUnchanged -cne $true -or $qualificationCold.EventCount -ne 0 -or
    $qualificationCold.RunCount -ne 0 -or $qualificationCold.ProductionAuthority -cne $false) {
    throw 'Qualification consumer evidence failed its public boundary or read-only checks.'
}
Write-Output 'V140 independent NuGet consumer PASS; actual physical qualification NOT_RUN.'
