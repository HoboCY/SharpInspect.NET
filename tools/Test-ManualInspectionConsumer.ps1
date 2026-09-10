param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$manualRepo = Split-Path -Parent $PSScriptRoot
$manualRun = [IO.Path]::GetFullPath($Run)
$manualFeed = [IO.Path]::GetFullPath($PackageFeed)
$manualCopy = Join-Path $manualRun 'manual-session-consumer'
$manualEvidence = Join-Path $manualRun 'manual-session-demo'
if (Test-Path -LiteralPath $manualCopy) { throw 'Use a fresh Manual Inspection consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($manualCopy)
[void][IO.Directory]::CreateDirectory($manualEvidence)
$manualSource = Join-Path $manualRepo 'samples\SharpInspect.SampleHost'
foreach ($manualFile in Get-ChildItem -LiteralPath $manualSource -Recurse -File) {
    $manualRelative = [IO.Path]::GetRelativePath($manualSource, $manualFile.FullName)
    if ($manualRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $manualTarget = Join-Path $manualCopy $manualRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $manualTarget))
    Copy-Item -LiteralPath $manualFile.FullName -Destination $manualTarget
}
Copy-Item -LiteralPath (Join-Path $manualRepo 'Directory.Build.props') -Destination $manualCopy
$manualProject = Join-Path $manualCopy 'SharpInspect.SampleHost.csproj'
$manualConfig = Join-Path $manualCopy 'NuGet.Config'
$manualXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($manualFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($manualConfig, $manualXml, [Text.UTF8Encoding]::new($false))
function Invoke-ManualInspectionConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $manualRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}
Invoke-ManualInspectionConsumerDotnet 'manual-consumer-restore.log' @('restore', $manualProject,
    '-p:UseLocalPackages=true', '--configfile', $manualConfig,
    '--packages', (Join-Path $manualRun 'manual-consumer-cache'))
Invoke-ManualInspectionConsumerDotnet 'manual-consumer-build.log' @('build', $manualProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$manualAssets = Get-Content -LiteralPath (Join-Path $manualCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($manualAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Manual Inspection consumer still references a source project.'
}
foreach ($manualPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $manualAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $manualPackage + '/0.1.0-dev.1')]) {
        throw "Manual Inspection consumer package is missing: $manualPackage"
    }
}
$manualDll = Join-Path $manualCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$manualPreviousConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_CONSUMER', 'Process')
$manualPreviousEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_CONSUMER', $manualDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_EVIDENCE_ROOT', $manualEvidence, 'Process')
    Invoke-ManualInspectionConsumerDotnet 'manual-consumer-tests.log' @('test',
        (Join-Path $manualRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~ManualInspectionConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $manualRun 'manual-consumer-tests'))
    foreach ($manualName in 'manual-inspection-evidence.json', 'acceptance.json', 'process.log') {
        $manualArtifact = Join-Path $manualEvidence $manualName
        if (-not (Test-Path -LiteralPath $manualArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $manualArtifact).Length -eq 0) {
            throw "Manual Inspection evidence is missing or empty: $manualName"
        }
    }
    $manualDetail = Get-Content -LiteralPath (Join-Path $manualEvidence 'manual-inspection-evidence.json') -Raw | ConvertFrom-Json
    $manualAcceptance = Get-Content -LiteralPath (Join-Path $manualEvidence 'acceptance.json') -Raw | ConvertFrom-Json
    $manualHash = (Get-FileHash -LiteralPath $manualDll -Algorithm SHA256).Hash
    $manualSessionIds = @($manualDetail.SessionId) + @($manualDetail.EdgeCases.Runs.SessionId)
    $manualRunIds = @($manualDetail.Runs.RunId) + @($manualDetail.EdgeCases.Runs.RunId)
    if ($manualDetail.Result -cne 'Pass' -or $manualAcceptance.ExternalNuGetConsumer -cne $true -or
        $manualAcceptance.Result -cne 'Pass' -or $manualAcceptance.ValidationId -cne 'V135_N01' -or
        $manualAcceptance.ConsumerSha256 -cne $manualHash -or
        @($manualSessionIds | Sort-Object -Unique).Count -ne 3 -or
        @($manualRunIds | Sort-Object -Unique).Count -ne 5 -or
        ($manualDetail.AcceptedRunCorrelations -join ',') -cne ($manualDetail.Runs.CommandCorrelationId -join ',') -or
        $manualDetail.ConsumerAssemblySha256 -cne $manualHash -or
        $manualDetail.Restoration -cne 'NoActiveBaselineClosed' -or
        $manualDetail.HistoryRunCount -ne 3 -or $manualDetail.Source.Kind -cne 'Draft' -or
        $manualDetail.Ready -cne $false -or $manualDetail.Active -cne $false -or
        $manualDetail.CameraOpenAfterExit -cne $false -or
        $manualDetail.EdgeCases.BusyReason -cne 'ManualInspectionRunInProgress' -or
        $manualDetail.EdgeCases.PreviewConflictReason -cne 'ManualInspectionSessionInProgress' -or
        ($manualDetail.EdgeCases.Runs.ExecutionStatus -join ',') -cne 'Timeout,Error' -or
        ($manualDetail.Runs.Decision -join ',') -cne 'Pass,Fail,Unknown') {
        throw 'Manual public package evidence does not prove the required Draft inspection and safe closure.'
    }
    Write-Output 'V135_N01 Manual Inspection public NuGet consumer PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_CONSUMER', $manualPreviousConsumer, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_MANUAL_INSPECTION_EVIDENCE_ROOT', $manualPreviousEvidence, 'Process')
}
