param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$importRepo = Split-Path -Parent $PSScriptRoot
$importRun = [IO.Path]::GetFullPath($Run)
$importFeed = [IO.Path]::GetFullPath($PackageFeed)
$importCopy = Join-Path $importRun 'calibration-import-consumer'
$importEvidence = Join-Path $importRun 'calibration-import-demo'
if (Test-Path -LiteralPath $importCopy) { throw 'Use a fresh calibration import consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($importCopy)
[void][IO.Directory]::CreateDirectory($importEvidence)
$importSource = Join-Path $importRepo 'samples\SharpInspect.SampleHost'
foreach ($importFile in Get-ChildItem -LiteralPath $importSource -Recurse -File) {
    $importRelative = [IO.Path]::GetRelativePath($importSource, $importFile.FullName)
    if ($importRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $importTarget = Join-Path $importCopy $importRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $importTarget))
    Copy-Item -LiteralPath $importFile.FullName -Destination $importTarget
}
Copy-Item -LiteralPath (Join-Path $importRepo 'Directory.Build.props') -Destination $importCopy
$importProject = Join-Path $importCopy 'SharpInspect.SampleHost.csproj'
$importConfig = Join-Path $importCopy 'NuGet.Config'
$importXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($importFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($importConfig, $importXml, [Text.UTF8Encoding]::new($false))
function Invoke-CalibrationImportConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $importRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}
Invoke-CalibrationImportConsumerDotnet 'calibration-import-consumer-restore.log' @('restore', $importProject,
    '-p:UseLocalPackages=true', '--configfile', $importConfig,
    '--packages', (Join-Path $importRun 'calibration-import-consumer-cache'))
Invoke-CalibrationImportConsumerDotnet 'calibration-import-consumer-build.log' @('build', $importProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$importAssets = Get-Content -LiteralPath (Join-Path $importCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($importAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Calibration import consumer still references a source project.'
}
foreach ($importPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $importAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $importPackage + '/0.1.0-dev.1')]) {
        throw "Calibration import consumer package is missing: $importPackage"
    }
}
$importDll = Join-Path $importCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$importPreviousConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_CONSUMER', 'Process')
$importPreviousEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_CONSUMER', $importDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_EVIDENCE_ROOT', $importEvidence, 'Process')
    Invoke-CalibrationImportConsumerDotnet 'calibration-import-consumer-tests.log' @('test',
        (Join-Path $importRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~CalibrationImportConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $importRun 'calibration-import-consumer-tests'))
    foreach ($importName in 'calibration-import-evidence.json', 'acceptance.json', 'process.log') {
        $importArtifact = Join-Path $importEvidence $importName
        if (-not (Test-Path -LiteralPath $importArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $importArtifact).Length -eq 0) {
            throw "Calibration import evidence is missing or empty: $importName"
        }
    }
    $importDetail = Get-Content -LiteralPath (Join-Path $importEvidence 'calibration-import-evidence.json') -Raw | ConvertFrom-Json
    $importHash = (Get-FileHash -LiteralPath $importDll -Algorithm SHA256).Hash
    if ($importDetail.Result -cne 'Pass' -or $importDetail.CaseId -cne 'V134_N01' -or
        $importDetail.ExternalNuGetConsumer -cne $true -or
        $importDetail.ExternalPackageReference -cne $true -or
        $importDetail.NoProjectReferences -cne $true -or
        $importDetail.ConsumerSha256 -cne $importHash -or
        $importDetail.CandidateCanPublish -cne $false -or
        $importDetail.CandidateCanActivate -cne $false -or
        $importDetail.TamperedDisposition -cne 'Rejected' -or
        $importDetail.FirstCandidateRetained -cne $true -or
        $importDetail.Ready -cne $false -or $importDetail.Active -cne $false) {
        throw 'Calibration import public package evidence does not prove quarantine and tamper rejection.'
    }
    Write-Output 'V134_N01 Calibration import public NuGet consumer PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_CONSUMER', $importPreviousConsumer, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_IMPORT_EVIDENCE_ROOT', $importPreviousEvidence, 'Process')
}
