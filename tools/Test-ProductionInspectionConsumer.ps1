param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)

$ErrorActionPreference = 'Stop'
$consumerRoot = Join-Path ([IO.Path]::GetFullPath($Run)) 'production-inspection-consumer'
if (Test-Path -LiteralPath $consumerRoot) {
    throw 'Use a fresh production inspection consumer directory.'
}
[void][IO.Directory]::CreateDirectory($consumerRoot)

$consumerProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" />
  </ItemGroup>
</Project>
'@

$consumerProgram = @'
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

var communicationPolicy = new PlcCommunicationPolicy(
    "Consumer.ProductionCommunication", "1",
    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100),
    TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40),
    TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
    TimeSpan.FromMilliseconds(200), 2,
    TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
var communicationBinding = new ModbusCommunicationBinding(
    communicationPolicy, 300, 400);
var profile = new ModbusProductionProfile(
    "Consumer.ProductionProfile", "1", "127.0.0.1", 1502, 1,
    100, 200, communicationBinding, TimeSpan.FromSeconds(1));
var document = new ProductionPolicyDocument(
    "Consumer.Policy", "1", "isolated consumer declaration");
var manifest = new ProductionDeploymentManifest(
    "Consumer.Deployment", "1", document, document, document, document,
    document, document, document, Array.Empty<string>());
var inspectionOptions = new ProductionInspectionOptions(
    "ConsumerStation", ProductionEvidenceRequirement.None, profile,
    1, new string('A', 64), TimeSpan.FromSeconds(2),
    TimeSpan.FromSeconds(2), manifest);
if (string.IsNullOrWhiteSpace(profile.EndpointBindingHash) ||
    string.IsNullOrWhiteSpace(profile.ContentHash) ||
    string.IsNullOrWhiteSpace(manifest.ContentHash) ||
    string.IsNullOrWhiteSpace(inspectionOptions.ContentHash))
    throw new InvalidOperationException("ProductionContractHashMissing");

// The public query is callable, but the consumer has no identity/audit authority
// and therefore cannot read or fabricate a production history record.
var storeOptions = new ProductionStoreOptions
{
    ProductionInspections = new ProductionInspectionStoreOptions()
};
var historyQuery = new SqliteProductionInspectionHistoryQuery(storeOptions);
var history = await historyQuery.ReadCurrentAsync();
if (history.Available || history.ReasonCode != "ProductionInspectionRequiresIdentityAndAudit")
    throw new InvalidOperationException("ProductionHistoryBoundaryChanged");

await using var runtime = new StationRuntime();
var runtimeSnapshot = await runtime.GetSnapshotAsync();
if (runtimeSnapshot.Ready || runtimeSnapshot.ArmState != ProductionArmState.Disarmed)
    throw new InvalidOperationException("UnconfiguredRuntimeGrantedProductionReadiness");

var coreType = typeof(ProductionInspectionCore);
var requiredCoreProperties = new[]
{
    "Admission", "State", "ExecutionStatus", "Decision", "ReasonCode",
    "FrameMetadata", "FrameProvenance", "Result", "Overlay", "PlcPayload",
    "ContentHash"
};
foreach (var property in requiredCoreProperties)
{
    if (coreType.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.CanRead != true)
        throw new InvalidOperationException("ProductionCorePropertyMissing:" + property);
}
var publicCoreConstructors = coreType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
if (publicCoreConstructors.Length != 0)
    throw new InvalidOperationException("ProductionCoreConstructionMustRemainRuntimeOwned");
if (!typeof(IProductionInspectionHistoryQuery).IsAssignableFrom(historyQuery.GetType()))
    throw new InvalidOperationException("ProductionHistoryQueryContractMissing");

Console.WriteLine(JsonSerializer.Serialize(new
{
    CaseId = "V142_N01",
    Result = "Pass",
    ProfileHash = profile.ContentHash,
    EndpointBindingHash = profile.EndpointBindingHash,
    ProductionOptionsHash = inspectionOptions.ContentHash,
    DeploymentManifestHash = manifest.ContentHash,
    HistoryQueryType = historyQuery.GetType().FullName,
    HistoryReadAvailable = history.Available,
    HistoryReadReason = history.ReasonCode,
    RuntimeReady = runtimeSnapshot.Ready,
    RuntimeArmState = runtimeSnapshot.ArmState.ToString(),
    CoreType = coreType.FullName,
    CoreConstructibleFromPublicApi = publicCoreConstructors.Length != 0,
    ProductionHistoryWriteAttempted = false,
    ProductionExecution = "NotRun",
    PhysicalProduction = "NotRun",
    LoadedAssemblies = new[] { typeof(ProductionInspectionCore).Assembly, typeof(StationRuntime).Assembly }
        .Distinct().Select(assembly => new
        {
            Name = assembly.GetName().Name,
            Path = assembly.Location,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        }).ToArray()
}));
'@

[IO.File]::WriteAllText((Join-Path $consumerRoot 'Consumer.csproj'), $consumerProject,
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'), $consumerProgram,
    [Text.UTF8Encoding]::new($false))

$packageSource = [IO.Path]::GetFullPath($PackageFeed)
$consumerConfig = '<configuration><packageSources><clear/><add key="ticket" value="' +
    [Security.SecurityElement]::Escape($packageSource) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>' +
    '<packageSourceMapping><packageSource key="ticket"><package pattern="SharpInspect.NET.*"/></packageSource>' +
    '<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
[IO.File]::WriteAllText((Join-Path $consumerRoot 'NuGet.Config'), $consumerConfig,
    [Text.UTF8Encoding]::new($false))

function Invoke-ProductionConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 |
        Tee-Object -FilePath (Join-Path ([IO.Path]::GetFullPath($Run)) $LogName)
    if ($LASTEXITCODE -ne 0) {
        throw "Production inspection consumer failed: $LogName ($LASTEXITCODE)"
    }
}

$packages = Join-Path $consumerRoot 'packages'
$projectPath = Join-Path $consumerRoot 'Consumer.csproj'
Invoke-ProductionConsumerDotnet 'production-inspection-consumer-restore.log' @(
    'restore', $projectPath, '--configfile', (Join-Path $consumerRoot 'NuGet.Config'),
    '--packages', $packages)
Invoke-ProductionConsumerDotnet 'production-inspection-consumer-build.log' @(
    'build', $projectPath, '-c', 'Release', '--no-restore')

$assets = Get-Content -LiteralPath (Join-Path $consumerRoot 'obj/project.assets.json') -Raw |
    ConvertFrom-Json
if (@($assets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Production inspection consumer references source projects.'
}

$packageBindings = @($assets.libraries.PSObject.Properties | Where-Object {
    $_.Name -like 'SharpInspect.NET.*' -and $_.Value.type -ceq 'package'
} | ForEach-Object {
    $parts = $_.Name.Split('/')
    if ($parts.Count -ne 2) { throw "Unexpected package asset identity: $($_.Name)" }
    $packageFile = $parts[0] + '.' + $parts[1] + '.nupkg'
    $candidate = Join-Path $packageSource $packageFile
    $restored = Join-Path (Join-Path $packages $_.Value.path) $packageFile.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Candidate package missing: $candidate"
    }
    if (-not (Test-Path -LiteralPath $restored)) {
        throw "Restored package missing: $restored"
    }
    $candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    $restoredHash = (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash
    if ($restoredHash -cne $candidateHash) {
        throw "Restored package differs from candidate: $($_.Name)"
    }
    [ordered]@{
        Package = $_.Name
        Candidate = $candidate
        Restored = $restored
        Sha256 = $candidateHash
    }
})
if (@($packageBindings | Where-Object { $_.Package -like 'SharpInspect.NET.Abstractions/*' }).Count -ne 1 -or
    @($packageBindings | Where-Object { $_.Package -like 'SharpInspect.NET.Runtime/*' }).Count -ne 1) {
    throw 'Production inspection consumer did not bind candidate Abstractions and Runtime packages.'
}

$consumerDll = Join-Path $consumerRoot 'bin/Release/net6.0/Consumer.dll'
Invoke-ProductionConsumerDotnet 'production-inspection-consumer-run.log' @($consumerDll)
$consumerOutput = Get-Content -LiteralPath (Join-Path ([IO.Path]::GetFullPath($Run)) 'production-inspection-consumer-run.log') -Raw
$evidence = $consumerOutput | ConvertFrom-Json
if ($evidence.CaseId -cne 'V142_N01' -or $evidence.Result -cne 'Pass' -or
    $evidence.RuntimeReady -cne $false -or $evidence.RuntimeArmState -cne 'Disarmed' -or
    $evidence.HistoryReadAvailable -cne $false -or
    $evidence.HistoryReadReason -cne 'ProductionInspectionRequiresIdentityAndAudit' -or
    $evidence.ProductionHistoryWriteAttempted -cne $false -or
    $evidence.ProductionExecution -cne 'NotRun' -or
    $evidence.PhysicalProduction -cne 'NotRun' -or
    $evidence.CoreConstructibleFromPublicApi -cne $false) {
    throw 'Production inspection consumer evidence failed the public boundary checks.'
}
$evidence | Add-Member -NotePropertyName ConsumerSha256 -NotePropertyValue (
    Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash
$evidence | Add-Member -NotePropertyName CandidatePackageBindings -NotePropertyValue $packageBindings
$assemblyBindings = @($packageBindings | ForEach-Object {
    $packageName = $_.Package.Split('/')[0]
    $assemblyName = $packageName.Replace('SharpInspect.NET.', 'SharpInspect.')
    $assemblyFile = $assemblyName + '.dll'
    $entryPath = 'lib/net6.0/' + $assemblyFile
    $archive = [IO.Compression.ZipFile]::OpenRead($_.Candidate)
    try {
        $entry = $archive.GetEntry($entryPath)
        if ($null -eq $entry) { throw "Candidate package assembly missing: $entryPath" }
        $entryStream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $candidateAssemblyHash = [Convert]::ToHexString($hasher.ComputeHash($entryStream)) }
        finally { $hasher.Dispose(); $entryStream.Dispose() }
    }
    finally { $archive.Dispose() }
    $expandedAssembly = Join-Path (Split-Path -Parent $_.Restored) $entryPath
    $outputAssembly = Join-Path (Split-Path -Parent $consumerDll) $assemblyFile
    $loadedAssembly = @($evidence.LoadedAssemblies | Where-Object Name -CEQ $assemblyName)
    if ($loadedAssembly.Count -ne 1 -or
        [IO.Path]::GetFullPath($loadedAssembly[0].Path) -ine [IO.Path]::GetFullPath($outputAssembly) -or
        $loadedAssembly[0].Sha256 -cne $candidateAssemblyHash -or
        (Get-FileHash -LiteralPath $expandedAssembly -Algorithm SHA256).Hash -cne $candidateAssemblyHash -or
        (Get-FileHash -LiteralPath $outputAssembly -Algorithm SHA256).Hash -cne $candidateAssemblyHash) {
        throw "Actual consumer assembly differs from candidate package: $assemblyName"
    }
    [ordered]@{ Package=$_.Package; Entry=$entryPath; LoadedPath=$loadedAssembly[0].Path;
        Sha256=$candidateAssemblyHash; ExpandedAndOutputMatch=$true }
})
if ($assemblyBindings.Count -ne 2) { throw 'Expected exactly two loaded SharpInspect package assemblies.' }
$evidence | Add-Member -NotePropertyName CandidateAssemblyBindings -NotePropertyValue $assemblyBindings
$evidence | Add-Member -NotePropertyName NoProjectReferences -NotePropertyValue $true
$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path ([IO.Path]::GetFullPath($Run)) 'production-inspection-consumer-evidence.json') -Encoding utf8
Write-Output 'V142_N01 isolated NuGet production contracts and read-only history boundary PASS; production execution NOT_RUN.'
