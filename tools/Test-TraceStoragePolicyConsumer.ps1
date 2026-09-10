param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$PackageFeed)
$ErrorActionPreference = 'Stop'
$traceStorageRepo = Split-Path -Parent $PSScriptRoot
$traceStorageRun = [IO.Path]::GetFullPath($Run)
$traceStorageCopy = Join-Path $traceStorageRun 'trace-storage-policy-consumer'
$traceStorageEvidence = Join-Path $traceStorageRun 'trace-storage-policy-demo'
if (Test-Path -LiteralPath $traceStorageCopy) { throw 'Use a fresh Trace storage policy consumer directory.' }
[void][IO.Directory]::CreateDirectory($traceStorageCopy)
[void][IO.Directory]::CreateDirectory($traceStorageEvidence)
$traceStorageSource = Join-Path $traceStorageRepo 'samples/SharpInspect.SampleHost'
foreach ($traceStorageFile in Get-ChildItem -LiteralPath $traceStorageSource -Recurse -File) {
    $traceStorageRelative = [IO.Path]::GetRelativePath($traceStorageSource, $traceStorageFile.FullName)
    if ($traceStorageRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $traceStorageTarget = Join-Path $traceStorageCopy $traceStorageRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $traceStorageTarget))
    Copy-Item -LiteralPath $traceStorageFile.FullName -Destination $traceStorageTarget
}
Copy-Item -LiteralPath (Join-Path $traceStorageRepo 'Directory.Build.props') -Destination $traceStorageCopy
$traceStorageProject = Join-Path $traceStorageCopy 'SharpInspect.SampleHost.csproj'
$traceStorageConfig = Join-Path $traceStorageCopy 'NuGet.Config'
$traceStorageXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageFeed)) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($traceStorageConfig, $traceStorageXml, [Text.UTF8Encoding]::new($false))
function Invoke-TraceStorageDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $traceStorageRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Trace storage policy consumer failed: $LogName ($LASTEXITCODE)" }
}
Invoke-TraceStorageDotnet 'trace-storage-policy-consumer-restore.log' @('restore', $traceStorageProject,
    '-p:UseLocalPackages=true', '--configfile', $traceStorageConfig, '--packages', (Join-Path $traceStorageRun 'trace-storage-policy-consumer-cache'))
Invoke-TraceStorageDotnet 'trace-storage-policy-consumer-build.log' @('build', $traceStorageProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$traceStorageAssets = Get-Content -LiteralPath (Join-Path $traceStorageCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($traceStorageAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Trace storage policy consumer still references source projects.'
}
foreach ($traceStoragePackage in 'Abstractions', 'Runtime', 'Wpf') {
    if (-not $traceStorageAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $traceStoragePackage + '/0.1.0-dev.1')]) {
        throw "Trace storage policy consumer package missing: $traceStoragePackage"
    }
}
$traceStorageDll = Join-Path $traceStorageCopy 'bin/Release/net6.0-windows/SharpInspect.SampleHost.dll'
$traceStoragePreviousConsumer = $env:SHARPINSPECT_TRACE_STORAGE_CONSUMER
$traceStoragePreviousRoot = $env:SHARPINSPECT_TRACE_STORAGE_EVIDENCE_ROOT
try {
    $env:SHARPINSPECT_TRACE_STORAGE_CONSUMER = $traceStorageDll
    $env:SHARPINSPECT_TRACE_STORAGE_EVIDENCE_ROOT = $traceStorageEvidence
    Invoke-TraceStorageDotnet 'trace-storage-policy-consumer-tests.log' @('test',
        (Join-Path $traceStorageRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~V139_N01_RealUiPublishesTwoPoliciesAndIndependentRestartPreservesOldSnapshot',
        '--logger', 'trx', '--results-directory', (Join-Path $traceStorageRun 'trace-storage-policy-consumer-tests'))
}
finally {
    $env:SHARPINSPECT_TRACE_STORAGE_CONSUMER = $traceStoragePreviousConsumer
    $env:SHARPINSPECT_TRACE_STORAGE_EVIDENCE_ROOT = $traceStoragePreviousRoot
}
$traceStorageDetail = Get-Content -LiteralPath (Join-Path $traceStorageEvidence 'trace-storage-policy-evidence.json') -Raw | ConvertFrom-Json
$traceStorageCold = Get-Content -LiteralPath (Join-Path $traceStorageEvidence 'trace-storage-policy-restart.json') -Raw | ConvertFrom-Json
if ($traceStorageDetail.Result -cne 'Pass' -or $traceStorageDetail.CaseId -cne 'V139_N01' -or
    $traceStorageDetail.ConsumerSha256 -cne (Get-FileHash -LiteralPath $traceStorageDll).Hash -or
    $traceStorageDetail.UiPublished -cne $true -or $traceStorageDetail.PublishedVersions -ne 2 -or
    $traceStorageDetail.OldSnapshotUnchanged -cne $true -or $traceStorageDetail.NoImplicitEditorDefaults -cne $true -or
    $traceStorageDetail.ProductionReady -cne $false -or $traceStorageDetail.PreflightCanAdmit -cne $false -or
    $traceStorageCold.CaseId -cne 'V139_N02' -or $traceStorageCold.Result -cne 'Pass' -or
    $traceStorageCold.ReadOnly -cne $true -or $traceStorageCold.WriterStarted -cne $false -or
    $traceStorageCold.MainDatabaseUnchanged -cne $true -or
    $traceStorageCold.DatabaseUnchanged -cne $true -or $traceStorageCold.OldSnapshotUnchanged -cne $true) {
    throw 'Trace storage policy consumer evidence failed.'
}
$traceStorageSetUnchanged = $true
foreach ($traceStorageArtifact in 'Database','Wal','Shm','Journal') {
    if ($traceStorageCold.SqliteArtifactsBefore.$traceStorageArtifact -cne $traceStorageCold.SqliteArtifactsAfter.$traceStorageArtifact) {
        $traceStorageSetUnchanged = $false
    }
}
if ($traceStorageCold.SqliteArtifactSetUnchanged -cne $traceStorageSetUnchanged) {
    throw 'Trace storage policy SQLite artifact comparison is inconsistent.'
}
Write-Output 'V139_N01/N02 independent UI and NuGet trace storage policy consumer PASS; production qualification NotRun.'
