param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)

$ErrorActionPreference = 'Stop'
$productionRepo = Split-Path -Parent $PSScriptRoot
$productionRun = [IO.Path]::GetFullPath($Run)
$productionFeed = [IO.Path]::GetFullPath($PackageFeed)
$productionCopy = Join-Path $productionRun 'production-admission-consumer'
$productionEvidence = Join-Path $productionRun 'production-admission-demo'
if (Test-Path -LiteralPath $productionCopy) {
    throw 'Use a fresh production admission consumer validation directory.'
}
[void][IO.Directory]::CreateDirectory($productionCopy)
[void][IO.Directory]::CreateDirectory($productionEvidence)

$productionSource = Join-Path $productionRepo 'samples\SharpInspect.SampleHost'
foreach ($productionFile in Get-ChildItem -LiteralPath $productionSource -Recurse -File) {
    $productionRelative = [IO.Path]::GetRelativePath($productionSource, $productionFile.FullName)
    if ($productionRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $productionTarget = Join-Path $productionCopy $productionRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $productionTarget))
    Copy-Item -LiteralPath $productionFile.FullName -Destination $productionTarget
}
Copy-Item -LiteralPath (Join-Path $productionRepo 'Directory.Build.props') -Destination $productionCopy

$productionProject = Join-Path $productionCopy 'SharpInspect.SampleHost.csproj'
$productionConfig = Join-Path $productionCopy 'NuGet.Config'
$productionXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($productionFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($productionConfig, $productionXml, [Text.UTF8Encoding]::new($false))

function Invoke-ProductionConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $productionRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}

Invoke-ProductionConsumerDotnet 'production-admission-consumer-restore.log' @(
    'restore', $productionProject, '-p:UseLocalPackages=true', '--configfile', $productionConfig,
    '--packages', (Join-Path $productionRun 'production-admission-consumer-cache'))
Invoke-ProductionConsumerDotnet 'production-admission-consumer-build.log' @(
    'build', $productionProject, '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')

$productionAssets = Get-Content -LiteralPath (Join-Path $productionCopy 'obj/project.assets.json') -Raw |
    ConvertFrom-Json
if (@($productionAssets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Production admission consumer still references a source project.'
}
foreach ($productionPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $productionAssets.libraries.PSObject.Properties[
            ('SharpInspect.NET.' + $productionPackage + '/0.1.0-dev.1')]) {
        throw "Production admission consumer package is missing: $productionPackage"
    }
}

$productionDll = Join-Path $productionCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$productionPriorConsumer = [Environment]::GetEnvironmentVariable(
    'SHARPINSPECT_PRODUCTION_ADMISSION_CONSUMER', 'Process')
$productionPriorEvidence = [Environment]::GetEnvironmentVariable(
    'SHARPINSPECT_PRODUCTION_ADMISSION_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PRODUCTION_ADMISSION_CONSUMER',
        $productionDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PRODUCTION_ADMISSION_EVIDENCE_ROOT',
        $productionEvidence, 'Process')
    Invoke-ProductionConsumerDotnet 'production-admission-consumer-tests.log' @(
        'test', (Join-Path $productionRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~ProductionAdmissionConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $productionRun 'production-admission-consumer-tests'))

    foreach ($productionArtifactName in 'production-admission-evidence.json',
            'production-admission-restart.json', 'evidence.json', 'process.log', 'restart.log') {
        $productionArtifact = Join-Path $productionEvidence $productionArtifactName
        if (-not (Test-Path -LiteralPath $productionArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $productionArtifact).Length -eq 0) {
            throw "Production admission consumer evidence is missing or empty: $productionArtifactName"
        }
    }

    $productionDetail = Get-Content -LiteralPath (Join-Path $productionEvidence 'production-admission-evidence.json') -Raw | ConvertFrom-Json
    $productionRestart = Get-Content -LiteralPath (Join-Path $productionEvidence 'production-admission-restart.json') -Raw | ConvertFrom-Json
    $productionSummary = Get-Content -LiteralPath (Join-Path $productionEvidence 'evidence.json') -Raw |
        ConvertFrom-Json
    $productionHash = (Get-FileHash -LiteralPath $productionDll -Algorithm SHA256).Hash
    if ($productionDetail.Result -cne 'Pass' -or
        $productionDetail.CaseId -cne 'V136_N01' -or
        $productionDetail.ExternalNuGetConsumer -cne $true -or
        $productionDetail.ConsumerSha256 -cne $productionHash -or
        $productionDetail.SchemaVersion -ne 22 -or
        $productionDetail.StartupFenceCleared -cne $true -or
        $productionDetail.AuthenticationSucceeded -cne $true -or
        $productionDetail.ArmPermissionGranted -cne $true -or
        $productionDetail.ArmRequiresStepUp -cne $false -or
        $productionDetail.StepUpServiceRegistered -cne $true -or
        $productionDetail.ArmDisposition -cne 'Rejected' -or
        $productionDetail.ArmAudit -cne 'Persisted' -or
        $productionDetail.GateCount -ne 24 -or
        $productionDetail.BlockerCount -le 0 -or
        $productionDetail.CanArm -cne $false -or
        $productionDetail.ReadyBefore -cne $false -or $productionDetail.ReadyAfter -cne $false -or
        $productionDetail.ArmedBefore -cne $false -or $productionDetail.ArmedAfter -cne $false -or
        $productionDetail.ActiveBefore -cne $false -or $productionDetail.ActiveAfter -cne $false -or
        $productionDetail.ProductionFactsCreated -cne $false -or
        $productionDetail.PhysicalIoStarted -cne $false -or
        $productionDetail.DatabaseChangedByAdmission -cne $true) {
        throw 'Production admission consumer evidence failed its identity, gate, rejection or safety checks.'
    }
    foreach ($productionGate in 'FrameworkQualification', 'ProviderQualification',
            'PlcCommunication', 'ProductionCycle') {
        $productionGateValue = @($productionDetail.Gates |
            Where-Object { $_.Gate -ceq $productionGate })
        if ($productionGateValue.Count -ne 1 -or $productionGateValue[0].Status -ceq 'Passed') {
            throw "Production admission gate was unexpectedly passed or missing: $productionGate"
        }
    }
    if (@($productionDetail.Gates).Count -ne 24 -or
        @($productionDetail.Blockers).Count -ne $productionDetail.BlockerCount) {
        throw 'Production admission evidence does not expose the complete blocker set.'
    }

    $productionHashFields = @(
        $productionDetail.ConsumerSha256,
        $productionDetail.DatabaseHashBeforeAdmission,
        $productionDetail.DatabaseHashAfterAdmission,
        $productionDetail.ReportContentHash,
        $productionRestart.DatabaseHashBefore,
        $productionRestart.DatabaseHashAfter,
        $productionRestart.ReportContentHash,
        $productionRestart.HistoryContentHash)
    if (@($productionHashFields | Where-Object { $_ -notmatch '^[0-9A-F]{64}$' }).Count -ne 0 -or
        $productionRestart.Result -cne 'Pass' -or
        $productionRestart.CaseId -cne 'V136_N02' -or
        $productionRestart.ReadOnlyQuery -cne $true -or
        $productionRestart.WriterStarted -cne $false -or
        $productionRestart.AlgorithmFactoryCreated -cne $false -or
        $productionRestart.ProviderFactoryCreated -cne $false -or
        $productionRestart.ProductionFactsCreated -cne $false -or
        $productionRestart.DatabaseUnchanged -cne $true -or
        $productionRestart.RecordCount -ne 1 -or
        $productionRestart.Kind -cne 'Rejected' -or
        $productionRestart.GateCount -ne 24 -or
        $productionRestart.Ready -cne $false -or
        $productionRestart.Active -cne $false -or
        $productionRestart.ArmState -cne 'Disarmed' -or
        $productionRestart.ReportContentHash -cne $productionDetail.ReportContentHash -or
        $productionRestart.DatabaseHashBefore -ne $productionRestart.DatabaseHashAfter -or
        $productionRestart.DatabaseHashBefore -ne $productionDetail.DatabaseHashAfterAdmission -or
        $productionRestart.CorrelationId -ne $productionDetail.CorrelationId -or
        $productionRestart.ActorPrincipalId -ne $productionDetail.PrincipalId -or
        $productionRestart.ActorSessionId -ne $productionDetail.SessionId) {
        throw 'Production admission cold-read evidence has an invalid hash, identity or mutation binding.'
    }
    if ($productionSummary.Result -cne 'Pass' -or
        $productionSummary.ExternalNuGetConsumer -cne $true -or
        $productionSummary.SchemaVersion -ne 22 -or
        $productionSummary.GateCount -ne 24 -or
        $productionSummary.ArmPermissionGranted -cne $true -or
        $productionSummary.ArmDisposition -cne 'Rejected' -or
        $productionSummary.Ready -cne $false -or
        $productionSummary.Active -cne $false -or
        $productionSummary.Armed -cne $false -or
        $productionSummary.ProductionFactsCreated -cne $false -or
        $productionSummary.IndependentColdRead -cne $true -or
        $productionSummary.DatabaseUnchangedByColdRead -cne $true -or
        $productionSummary.ReportContentHash -cne $productionDetail.ReportContentHash -or
        $productionSummary.HistoryContentHash -cne $productionRestart.HistoryContentHash) {
        throw 'Production admission summary does not prove the independent read-only boundary.'
    }
    [ordered]@{
        Result = 'Pass'
        ValidationIds = @('V136_N01', 'V136_N02')
        ExternalNuGetConsumer = $true
        ConsumerSha256 = $productionHash
        SchemaVersion = 22
        GateCount = 24
        BlockerCount = $productionDetail.BlockerCount
        ArmPermissionGranted = $true
        ArmDisposition = 'Rejected'
        Ready = $false
        Active = $false
        Armed = $false
        ProductionFactsCreated = $false
        IndependentColdRead = $true
        DatabaseUnchangedByColdRead = $true
        ReportContentHash = $productionDetail.ReportContentHash
        HistoryContentHash = $productionRestart.HistoryContentHash
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $productionEvidence 'acceptance.json') -Encoding utf8
    Write-Output 'V136_N01/N02 production-admission public NuGet consumer and independent cold query PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PRODUCTION_ADMISSION_CONSUMER',
        $productionPriorConsumer, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PRODUCTION_ADMISSION_EVIDENCE_ROOT',
        $productionPriorEvidence, 'Process')
}
