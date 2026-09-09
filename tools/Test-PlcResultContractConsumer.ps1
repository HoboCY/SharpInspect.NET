param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)

$ErrorActionPreference = 'Stop'
$consumerRepo = Split-Path -Parent $PSScriptRoot
$consumerRun = [IO.Path]::GetFullPath($Run)
$consumerFeed = [IO.Path]::GetFullPath($PackageFeed)
$consumerCopy = Join-Path $consumerRun 'plc-result-contract-consumer'
$consumerEvidence = Join-Path $consumerRun 'plc-result-contract-demo'
if (Test-Path -LiteralPath $consumerCopy) { throw 'Use a fresh PLC result-contract consumer validation directory.' }
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

Invoke-ConsumerDotnet 'plc-result-contract-consumer-restore.log' @(
    'restore', $consumerProject, '-p:UseLocalPackages=true', '--configfile', $consumerConfig,
    '--packages', (Join-Path $consumerRun 'plc-result-contract-consumer-cache'))
Invoke-ConsumerDotnet 'plc-result-contract-consumer-build.log' @(
    'build', $consumerProject, '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')

$consumerAssets = Get-Content -LiteralPath (Join-Path $consumerCopy 'obj/project.assets.json') -Raw |
    ConvertFrom-Json
if (@($consumerAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'PLC result-contract consumer still references a source project.'
}
foreach ($consumerPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $consumerAssets.libraries.PSObject.Properties[
            ('SharpInspect.NET.' + $consumerPackage + '/0.1.0-dev.1')]) {
        throw "PLC result-contract consumer package is missing: $consumerPackage"
    }
}

$consumerDll = Join-Path $consumerCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$consumerPriorPath = [Environment]::GetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_CONSUMER', 'Process')
$consumerPriorEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_CONSUMER', $consumerDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_EVIDENCE_ROOT', $consumerEvidence, 'Process')
    Invoke-ConsumerDotnet 'plc-result-contract-consumer-tests.log' @(
        'test', (Join-Path $consumerRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~PlcResultContractConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $consumerRun 'plc-result-contract-consumer-tests'))

    foreach ($consumerArtifactName in 'evidence.json', 'contract-evidence.json',
            'contract-restart.json', 'process.log', 'restart.log') {
        $consumerArtifact = Join-Path $consumerEvidence $consumerArtifactName
        if (-not (Test-Path -LiteralPath $consumerArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $consumerArtifact).Length -eq 0) {
            throw "PLC result-contract consumer evidence is missing or empty: $consumerArtifactName"
        }
    }
    $consumerResult = Get-Content -LiteralPath (Join-Path $consumerEvidence 'evidence.json') -Raw | ConvertFrom-Json
    $consumerDetail = Get-Content -LiteralPath (Join-Path $consumerEvidence 'contract-evidence.json') -Raw | ConvertFrom-Json
    $consumerRestart = Get-Content -LiteralPath (Join-Path $consumerEvidence 'contract-restart.json') -Raw | ConvertFrom-Json
    $consumerHash = (Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash
    if ($consumerResult.Result -cne 'Pass' -or $consumerResult.ExternalNuGetConsumer -cne $true -or
        $consumerResult.ConsumerSha256 -cne $consumerHash -or
        $consumerResult.IndependentRestart -cne $true -or
        $consumerResult.DatabaseUnchangedByRestart -cne $true -or
        $consumerResult.ReleasedVersions -ne 1 -or $consumerResult.Available -cne $true -or
        $consumerResult.Active -cne $false -or $consumerResult.Armed -cne $false -or
        $consumerResult.Ready -cne $false -or $consumerResult.ContractRevisionCount -ne 1 -or
        $consumerDetail.ContractRevisionPosition -ne 1 -or $consumerDetail.ReleaseHighWatermark -ne 1 -or
        $consumerDetail.SchemaValidationCount -ne 1 -or $consumerDetail.BindingCount -ne 1 -or
        $consumerDetail.InvalidGrantRejected -cne $true -or
        $consumerDetail.InvalidGrantReasonCode -cne 'StepUpInvalid' -or
        $consumerDetail.InvalidGrantRevisionCount -ne 0 -or
        $consumerDetail.ChangedIntentRejected -cne $true -or
        $consumerDetail.ChangedIntentReasonCode -cne 'StepUpInvalid' -or
        $consumerDetail.ChangedIntentRevisionCount -ne 0 -or
        $consumerDetail.Production -cne 'NotRun' -or $consumerDetail.PlcConnection -cne 'NotRun' -or
        $consumerDetail.PayloadExecution -cne 'NotRun' -or $consumerDetail.Activation -cne 'NotRun' -or
        $consumerRestart.Result -cne 'Pass' -or $consumerRestart.ReadOnlyQuery -cne $true -or
        $consumerRestart.DatabaseUnchanged -cne $true -or $consumerRestart.RevisionCount -ne 1 -or
        $consumerRestart.OpenedDevices -ne 0 -or $consumerRestart.Ready -ne $false) {
        throw 'PLC result-contract consumer evidence failed its command, binding, restart or boundary checks.'
    }
    $consumerHashFields = @(
        $consumerResult.ContractRevisionContentHash, $consumerResult.BindingContentHash,
        $consumerResult.ReleaseRecordContentHash, $consumerDetail.ContractRevisionContentHash,
        $consumerDetail.Contract.ContentHash, $consumerDetail.Schema.ContentHash,
        $consumerDetail.Binding.BindingContentHash, $consumerDetail.Release.RecordContentHash,
        $consumerRestart.ContractRevisionContentHash, $consumerRestart.BindingContentHash)
    if (@($consumerHashFields | Where-Object { $_ -notmatch '^[0-9A-F]{64}$' }).Count -ne 0 -or
        $consumerDetail.Binding.RecipeContentHash -cne $consumerDetail.Release.RecipeContentHash -or
        $consumerDetail.Binding.RecipeContentHash -ceq $consumerDetail.Release.RecordContentHash) {
        throw 'PLC result-contract consumer evidence has an invalid or mismatched immutable hash binding.'
    }
    [ordered]@{
        Result = 'Pass'
        ValidationIds = @('V131-C01', 'V131-C02')
        ExternalNuGetConsumer = $true
        ConsumerSha256 = $consumerHash
        IndependentRestart = $true
        DatabaseUnchangedByRestart = $true
        ReleasedVersions = 1
        ContractRevisionCount = 1
        BindingCount = 1
        InvalidGrantRejected = $true
        ChangedIntentRejected = $true
        Ready = $false
        Active = $false
        Armed = $false
        Production = 'NotRun'
        PlcConnection = 'NotRun'
        PayloadExecution = 'NotRun'
        Activation = 'NotRun'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $consumerEvidence 'acceptance.json') -Encoding utf8
    Write-Output 'V131-C01/C02 PLC result-contract NuGet consumer and independent cold query PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_CONSUMER', $consumerPriorPath, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PLC_RESULT_CONTRACT_EVIDENCE_ROOT', $consumerPriorEvidence, 'Process')
}
