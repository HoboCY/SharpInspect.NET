param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)

$ErrorActionPreference = 'Stop'
$consumerRepo = Split-Path -Parent $PSScriptRoot
$consumerRun = [IO.Path]::GetFullPath($Run)
$consumerFeed = [IO.Path]::GetFullPath($PackageFeed)
$consumerCopy = Join-Path $consumerRun 'recipe-activation-consumer'
$consumerEvidence = Join-Path $consumerRun 'recipe-activation-demo'
if (Test-Path -LiteralPath $consumerCopy) { throw 'Use a fresh recipe activation consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($consumerCopy)
[void][IO.Directory]::CreateDirectory($consumerEvidence)

$consumerSource = Join-Path $consumerRepo 'samples\SharpInspect.SampleHost'
foreach ($consumerFile in Get-ChildItem -LiteralPath $consumerSource -Recurse -File) {
    $consumerRelative = [IO.Path]::GetRelativePath($consumerSource, $consumerFile.FullName)
    if ($consumerRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $consumerTarget = Join-Path $consumerCopy $consumerRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $consumerTarget))
    Copy-Item -LiteralPath $consumerFile.FullName -Destination $consumerTarget
}
Copy-Item -LiteralPath (Join-Path $consumerRepo 'Directory.Build.props') -Destination $consumerCopy

$consumerProject = Join-Path $consumerCopy 'SharpInspect.SampleHost.csproj'
$consumerConfig = Join-Path $consumerCopy 'NuGet.Config'
$consumerXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($consumerFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($consumerConfig, $consumerXml, [Text.UTF8Encoding]::new($false))

function Invoke-ConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $consumerRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}

Invoke-ConsumerDotnet 'recipe-activation-consumer-restore.log' @(
    'restore', $consumerProject, '-p:UseLocalPackages=true', '--configfile', $consumerConfig,
    '--packages', (Join-Path $consumerRun 'recipe-activation-consumer-cache'))
Invoke-ConsumerDotnet 'recipe-activation-consumer-build.log' @(
    'build', $consumerProject, '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')

$consumerAssets = Get-Content -LiteralPath (Join-Path $consumerCopy 'obj/project.assets.json') -Raw |
    ConvertFrom-Json
if (@($consumerAssets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Recipe activation consumer still references a source project.'
}
foreach ($consumerPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $consumerAssets.libraries.PSObject.Properties[
            ('SharpInspect.NET.' + $consumerPackage + '/0.1.0-dev.1')]) {
        throw "Recipe activation consumer package is missing: $consumerPackage"
    }
}

$consumerDll = Join-Path $consumerCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$consumerPriorPath = [Environment]::GetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_CONSUMER', 'Process')
$consumerPriorEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_CONSUMER', $consumerDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_EVIDENCE_ROOT', $consumerEvidence, 'Process')
    Invoke-ConsumerDotnet 'recipe-activation-consumer-tests.log' @(
        'test', (Join-Path $consumerRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~RecipeActivationConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $consumerRun 'recipe-activation-consumer-tests'))

    foreach ($consumerArtifactName in 'evidence.json', 'activation-evidence.json',
            'activation-restart.json', 'process.log', 'restart.log') {
        $consumerArtifact = Join-Path $consumerEvidence $consumerArtifactName
        if (-not (Test-Path -LiteralPath $consumerArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $consumerArtifact).Length -eq 0) {
            throw "Recipe activation consumer evidence is missing or empty: $consumerArtifactName"
        }
    }
    $consumerResult = Get-Content -LiteralPath (Join-Path $consumerEvidence 'evidence.json') -Raw |
        ConvertFrom-Json
    $consumerDetail = Get-Content -LiteralPath (Join-Path $consumerEvidence 'activation-evidence.json') -Raw |
        ConvertFrom-Json
    $consumerRestart = Get-Content -LiteralPath (Join-Path $consumerEvidence 'activation-restart.json') -Raw |
        ConvertFrom-Json
    $consumerHash = (Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash
    if ($consumerResult.Result -cne 'Pass' -or $consumerResult.ExternalNuGetConsumer -cne $true -or
        $consumerResult.ConsumerSha256 -cne $consumerHash -or
        $consumerResult.IndependentColdRead -cne $true -or
        $consumerResult.DatabaseUnchangedByColdRead -cne $true -or
        $consumerResult.ReleasedVersions -ne 1 -or $consumerResult.PartIdentityMode -cne 'None' -or
        $consumerResult.ActivationAccessAvailable -cne $true -or
        $consumerResult.ActivationRequiresStepUp -cne $false -or
        $consumerResult.TerminalOutcome -cne 'Failed' -or
        $consumerResult.TerminalReasonCode -cne 'FrameworkQualificationAuthorityUnavailable' -or
        $consumerResult.EvidenceKind -cne 'LocalAuthority' -or
        $consumerResult.RestorationState -cne 'NotRequired' -or
        $consumerResult.AlgorithmPreparationRegistered -cne $true -or
        $consumerResult.FramePoolRegistered -cne $true -or
        $consumerResult.CameraProviderRegistered -cne $true -or
        $consumerResult.ProviderDiscoveryCalls -ne 0 -or $consumerResult.ProviderOpenCalls -ne 0 -or
        $consumerResult.ProviderApplyCalls -ne 0 -or $consumerResult.FramePoolOutstandingLeases -ne 0 -or
        $consumerResult.Ready -cne $false -or $consumerResult.Active -cne $false -or
        $consumerResult.Armed -cne $false -or $consumerResult.Production -cne 'NotRun' -or
        $consumerResult.PlcConnection -cne 'NotRun' -or $consumerResult.PayloadExecution -cne 'NotRun' -or
        $consumerResult.Activation -cne 'RejectedBeforePhysicalIo' -or
        $consumerDetail.DatabaseChangedByActivation -cne $true -or
        $consumerDetail.StartupFenceCleared -cne $true -or
        $consumerRestart.Result -cne 'Pass' -or $consumerRestart.ReadOnlyQuery -cne $true -or
        $consumerRestart.WriterStarted -cne $false -or
        $consumerRestart.AlgorithmFactoryCreated -cne $false -or
        $consumerRestart.ProviderFactoryCreated -cne $false -or
        $consumerRestart.DatabaseUnchanged -cne $true -or $consumerRestart.RecordCount -ne 2 -or
        $consumerRestart.AdmissionPosition -ne 1 -or $consumerRestart.TerminalPosition -ne 2 -or
        $consumerRestart.CurrentReasonCode -cne 'RecipeActivationNoActiveRecord' -or
        $consumerRestart.RecoveryRequired -cne $false -or $consumerRestart.Ready -cne $false -or
        $consumerRestart.Active -cne $false -or $consumerRestart.ArmState -cne 'Disarmed' -or
        $consumerRestart.ProviderDiscoveryCalls -ne 0 -or $consumerRestart.ProviderOpenCalls -ne 0 -or
        $consumerRestart.AlgorithmFactoryCreateCalls -ne 0 -or
        $consumerRestart.FramePoolOutstandingLeases -ne 0) {
        throw 'Recipe activation consumer evidence failed its admission, terminal, cold-read or boundary checks.'
    }
    $consumerHashFields = @(
        $consumerResult.ConsumerSha256, $consumerDetail.DatabaseHashBeforeActivation,
        $consumerDetail.DatabaseHashAfterActivation, $consumerDetail.AdmissionContentHash,
        $consumerDetail.TerminalContentHash, $consumerDetail.TerminalAdmissionContentHash,
        $consumerRestart.DatabaseHashBefore, $consumerRestart.DatabaseHashAfter,
        $consumerRestart.AdmissionContentHash, $consumerRestart.TerminalContentHash,
        $consumerRestart.TerminalAdmissionContentHash)
    if (@($consumerHashFields | Where-Object { $_ -notmatch '^[0-9A-F]{64}$' }).Count -ne 0 -or
        $consumerDetail.TerminalAdmissionContentHash -cne $consumerDetail.AdmissionContentHash -or
        $consumerRestart.TerminalAdmissionContentHash -cne $consumerRestart.AdmissionContentHash -or
        $consumerRestart.AdmissionContentHash -cne $consumerDetail.AdmissionContentHash -or
        $consumerRestart.TerminalContentHash -cne $consumerDetail.TerminalContentHash -or
        $consumerRestart.DatabaseHashBefore -cne $consumerDetail.DatabaseHashAfterActivation -or
        $consumerRestart.DatabaseHashAfter -cne $consumerRestart.DatabaseHashBefore) {
        throw 'Recipe activation consumer evidence has an invalid or mismatched immutable hash binding.'
    }
    [ordered]@{
        Result = 'Pass'
        ValidationIds = @('V132_N01', 'V132_N02')
        ExternalNuGetConsumer = $true
        ConsumerSha256 = $consumerHash
        ActivationAccessAvailable = $true
        ActivationRequiresStepUp = $false
        ReleasedVersions = 1
        PartIdentityMode = 'None'
        AdmissionPosition = 1
        TerminalPosition = 2
        TerminalOutcome = 'Failed'
        TerminalReasonCode = 'FrameworkQualificationAuthorityUnavailable'
        EvidenceKind = 'LocalAuthority'
        RestorationState = 'NotRequired'
        IndependentColdRead = $true
        DatabaseUnchangedByColdRead = $true
        Ready = $false
        Active = $false
        Armed = $false
        Production = 'NotRun'
        PlcConnection = 'NotRun'
        PayloadExecution = 'NotRun'
        Activation = 'RejectedBeforePhysicalIo'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $consumerEvidence 'acceptance.json') -Encoding utf8
    Write-Output 'V132_N01/N02 recipe-activation public NuGet consumer and independent cold query PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_CONSUMER', $consumerPriorPath, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_RECIPE_ACTIVATION_EVIDENCE_ROOT', $consumerPriorEvidence, 'Process')
}
