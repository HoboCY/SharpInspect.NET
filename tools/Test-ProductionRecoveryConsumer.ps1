param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed,
    [string]$Database,
    [string]$IdentityPolicy,
    # Retained for older Test-Development invocations; this new database uses an independent test key.
    [string]$AuditKey,
    [string]$AuditKeyDirectory
)

$ErrorActionPreference = 'Stop'
$recoveryRepo = Split-Path -Parent $PSScriptRoot
$recoveryRun = [IO.Path]::GetFullPath($Run)
$recoveryFeed = [IO.Path]::GetFullPath($PackageFeed)
$recoveryCopy = Join-Path $recoveryRun 'production-recovery-consumer'
$recoveryEvidence = Join-Path $recoveryRun 'production-recovery-demo'
if (Test-Path -LiteralPath $recoveryCopy) {
    throw 'Use a fresh production recovery consumer validation directory.'
}
[void][IO.Directory]::CreateDirectory($recoveryCopy)
[void][IO.Directory]::CreateDirectory($recoveryEvidence)

if ([string]::IsNullOrWhiteSpace($Database)) {
    $Database = Join-Path $recoveryEvidence 'station.sqlite'
}
$recoveryDatabase = [IO.Path]::GetFullPath($Database)
$recoveryDatabaseDirectory = Split-Path -Parent $recoveryDatabase
[void][IO.Directory]::CreateDirectory($recoveryDatabaseDirectory)
foreach ($databaseSuffix in @('', '-wal', '-shm')) {
    $databaseCandidate = $recoveryDatabase + $databaseSuffix
    if (Test-Path -LiteralPath $databaseCandidate) {
        throw "Production recovery consumer requires a new schema30 database; target already exists: $databaseCandidate"
    }
}
if ([string]::IsNullOrWhiteSpace($IdentityPolicy)) {
    $IdentityPolicy = Join-Path $recoveryRun 'development-identity-policy.json'
}
if (-not (Test-Path -LiteralPath $IdentityPolicy -PathType Leaf)) {
    throw "Production recovery consumer identity policy is missing: $IdentityPolicy"
}

$recoverySource = Join-Path $recoveryRepo 'samples\SharpInspect.SampleHost'
foreach ($recoveryFile in Get-ChildItem -LiteralPath $recoverySource -Recurse -File) {
    $recoveryRelative = [IO.Path]::GetRelativePath($recoverySource, $recoveryFile.FullName)
    if ($recoveryRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $recoveryTarget = Join-Path $recoveryCopy $recoveryRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $recoveryTarget))
    Copy-Item -LiteralPath $recoveryFile.FullName -Destination $recoveryTarget
}
Copy-Item -LiteralPath (Join-Path $recoveryRepo 'Directory.Build.props') -Destination $recoveryCopy

$recoveryProject = Join-Path $recoveryCopy 'SharpInspect.SampleHost.csproj'
$recoveryConfig = Join-Path $recoveryCopy 'NuGet.Config'
$recoveryXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($recoveryFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($recoveryConfig, $recoveryXml, [Text.UTF8Encoding]::new($false))

function Invoke-RecoveryConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Production recovery consumer failed: $LogName ($LASTEXITCODE)" }
}

Invoke-RecoveryConsumerDotnet 'production-recovery-consumer-restore.log' @(
    'restore', $recoveryProject, '-p:UseLocalPackages=true', '--configfile', $recoveryConfig,
    '--packages', (Join-Path $recoveryRun 'production-recovery-consumer-cache'))
Invoke-RecoveryConsumerDotnet 'production-recovery-consumer-build.log' @(
    'build', $recoveryProject, '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')

$recoveryAssets = Get-Content -LiteralPath (Join-Path $recoveryCopy 'obj/project.assets.json') -Raw |
    ConvertFrom-Json
if (@($recoveryAssets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Production recovery consumer still references a source project.'
}
foreach ($recoveryPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $recoveryAssets.libraries.PSObject.Properties[
            ('SharpInspect.NET.' + $recoveryPackage + '/0.1.0-dev.1')]) {
        throw "Production recovery consumer package is missing: $recoveryPackage"
    }
}

$databaseBefore = 'absent'
$recoveryDll = Join-Path $recoveryCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
# Initial provisioning of a new database rejects an already existing signing key.
# Reopening an initialized database may reuse its key; this fixture always creates a new database.
$recoverySigningKey = 'SharpInspect.ProductionRecoveryConsumer.' + [Guid]::NewGuid().ToString('N')
$recoverySigningDirectory = Join-Path $recoveryEvidence 'private-keys'
if (Test-Path -LiteralPath $recoverySigningDirectory) {
    throw 'Production recovery consumer requires a fresh private key directory.'
}
$recoverySigningHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($recoverySigningKey)))
$recoverySigningPath = [IO.Path]::GetFullPath((Join-Path $recoverySigningDirectory ($recoverySigningHash + '.key')))
$recoveryKeyCreated = $false
$recoveryRunFailure = $null
try {
    Invoke-RecoveryConsumerDotnet 'production-recovery-consumer-run.log' @(
        $recoveryDll, '--smoke', '--production-recovery-ui', '--trace-db', $recoveryDatabase,
        '--audit-key', $recoverySigningKey, '--audit-key-directory', $recoverySigningDirectory,
        '--identity-policy', $IdentityPolicy)
    $recoveryKeyCreated = Test-Path -LiteralPath $recoverySigningPath -PathType Leaf
    if (-not $recoveryKeyCreated) { throw 'Consumer did not provision its independent signing identity.' }
}
catch {
    $recoveryRunFailure = $_
    throw
}
finally {
    try {
        $recoveryEvidenceRoot = [IO.Path]::GetFullPath($recoveryEvidence) + [IO.Path]::DirectorySeparatorChar
        if (-not $recoverySigningPath.StartsWith($recoveryEvidenceRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Disposable consumer key escaped its evidence directory.'
        }
        if (Test-Path -LiteralPath $recoverySigningPath -PathType Leaf) {
            Remove-Item -LiteralPath $recoverySigningPath
        }
    }
    catch {
        if ($null -eq $recoveryRunFailure) { throw }
        Write-Warning 'Disposable consumer key cleanup failed; the original consumer failure is preserved.'
    }
}

if (-not (Test-Path -LiteralPath $recoveryDatabase -PathType Leaf)) {
    throw 'Production recovery consumer did not create the independent schema30 database.'
}

$recoveryOutput = Get-Content -LiteralPath (Join-Path $recoveryRun 'production-recovery-consumer-run.log') -Raw
$expectedMarker = 'V144-N01 production-recovery-ui PASS configured=true ready=false canRecover=false safetyProvider=none physicalProduction=NotRun'
if ($recoveryOutput -notmatch [Regex]::Escape($expectedMarker)) {
    throw 'Production recovery consumer did not prove the mounted fail-closed UI boundary.'
}
$databaseAfter = (Get-FileHash -LiteralPath $recoveryDatabase -Algorithm SHA256).Hash
$consumerHash = (Get-FileHash -LiteralPath $recoveryDll -Algorithm SHA256).Hash
[ordered]@{
    CaseId = 'V144-N01'
    Result = 'Pass'
    ExternalNuGetConsumer = $true
    ConsumerSha256 = $consumerHash
    NoProjectReferences = $true
    MountedProductionRecoveryViewModel = $true
    Configured = $true
    Ready = $false
    CanRecoverBeforeAuthentication = $false
    SafetyProvider = 'None'
    PhysicalProduction = 'NotRun'
    DatabaseHashBefore = $databaseBefore
    DatabaseHashAfter = $databaseAfter
    CandidatePackageFeed = $recoveryFeed
    IndependentSigningKeyCreated = $recoveryKeyCreated
    DisposableSigningKeyRemoved = -not (Test-Path -LiteralPath $recoverySigningPath)
    ParentAuditKeyInputsUsed = $false
    ParentAuditKeyProvided = -not [string]::IsNullOrWhiteSpace($AuditKey)
    ParentAuditKeyDirectoryProvided = -not [string]::IsNullOrWhiteSpace($AuditKeyDirectory)
    IndependentSigningKeyName = $recoverySigningKey
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
    Join-Path $recoveryEvidence 'production-recovery-consumer-evidence.json') -Encoding utf8

Write-Output 'V144-N01 independent NuGet production recovery UI consumer PASS; safety provider and physical recovery NOT_RUN.'
