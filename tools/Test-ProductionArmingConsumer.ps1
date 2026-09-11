param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$armingRun = [IO.Path]::GetFullPath($Run)
$armingFeed = [IO.Path]::GetFullPath($PackageFeed)
$armingConsumer = Join-Path $armingRun 'production-arming-consumer'
if (Test-Path -LiteralPath $armingConsumer) { throw 'Use a fresh production arming consumer directory.' }
[void][IO.Directory]::CreateDirectory($armingConsumer)
$armingProject = Join-Path $armingConsumer 'ProductionArmingConsumer.csproj'
$armingConfig = Join-Path $armingConsumer 'NuGet.Config'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $armingProject -Encoding utf8
$armingXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($armingFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($armingConfig, $armingXml, [Text.UTF8Encoding]::new($false))
@'
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;
var startup = new StartupProductionPolicy("V147.Consumer.Startup", "1", StartupProductionMode.AutomaticArm);
var post = new PostActivationArmPolicy("V147.Consumer.Post", "1", PostActivationArmMode.AutomaticRearmAfterPlcActivation);
ProductionPolicyDocument Doc(string id) => new(id, "1", "Consumer declaration");
var old = new ProductionDeploymentManifest("V147.Consumer.Deployment", "1", Doc("Logging"), Doc("Diagnostics"),
    Doc("Backup"), Doc("Startup"), Doc("Performance"), Doc("Conformance"), Doc("Ui"), Array.Empty<string>());
var explicitPolicies = new ProductionDeploymentManifest("V147.Consumer.Deployment", "1", Doc("Logging"), Doc("Diagnostics"),
    Doc("Backup"), Doc("Startup"), Doc("Performance"), Doc("Conformance"), Doc("Ui"), Array.Empty<string>(), startup, post);
var policy = new PlcCommunicationPolicy("V147.Consumer.Communication", "1",
    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
    TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
var communication = new ModbusCommunicationBinding(policy, 300, 400);
var legacy = new ModbusProductionProfile("V147.Consumer.Profile", "1", "127.0.0.1", 1502, 1,
    100, 200, communication, TimeSpan.FromSeconds(2));
var status = new ModbusProductionArmStatusBinding("V147.Consumer.Status", "1", 700);
var profile = new ModbusProductionProfile("V147.Consumer.Profile", "1", "127.0.0.1", 1502, 1,
    100, 200, communication, TimeSpan.FromSeconds(2), null,
    new ModbusRecipeChangeBinding(500, 600, TimeSpan.FromSeconds(10)), status);
var options = new ProductionArmStoreOptions();
if (StartupProductionPolicy.Default.Mode != StartupProductionMode.ManualArm ||
    PostActivationArmPolicy.Default.Mode != PostActivationArmMode.ManualRearm ||
    old.HasExplicitArmingPolicies || !explicitPolicies.HasExplicitArmingPolicies ||
    old.ContentHash == explicitPolicies.ContentHash || explicitPolicies.StartupProduction != startup ||
    explicitPolicies.PostActivationArm != post || legacy.ProductionArmStatus is not null ||
    profile.ProductionArmStatus != status || profile.ContentHash == legacy.ContentHash ||
    profile.EndpointBindingHash != legacy.EndpointBindingHash || options.MaxEvents < 1 ||
    typeof(ProductionArmReadyReceipt).GetConstructors().Length != 0 ||
    typeof(ProductionArmInputStabilityEvidence).GetConstructors().Length != 0 ||
    typeof(ProductionArmHistoryEvent).GetConstructors().Length != 0 ||
    !SystemPrincipalCatalog.Runtime.Permissions.Contains(SystemPermission.AttemptPolicyControlledProductionArm))
    throw new InvalidOperationException("V147PublicConsumerContractMismatch");
Console.WriteLine(JsonSerializer.Serialize(new
{
    id = "V147_N01", result = "Pass", scope = "public package contracts; no runtime or hardware admission",
    startup = startup.ContentHash, post = post.ContentHash, deployment = explicitPolicies.ContentHash,
    profile = profile.ContentHash,
    contracts = new[] { typeof(IProductionArmHistoryQuery).FullName, typeof(SqliteProductionArmHistoryQuery).FullName,
        typeof(ProductionArmStoreOptions).FullName, typeof(ProductionArmPolicyStatus).FullName }
}));
'@ | Set-Content -LiteralPath (Join-Path $armingConsumer 'Program.cs') -Encoding utf8

function Invoke-ArmingConsumerDotnet([string]$Log, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $armingRun $Log)
    if ($LASTEXITCODE -ne 0) { throw "Production arming consumer failed: $Log" }
}
Invoke-ArmingConsumerDotnet 'production-arming-restore.log' @('restore', $armingProject,
    '--configfile', $armingConfig, '--packages', (Join-Path $armingRun 'production-arming-consumer-cache'))
Invoke-ArmingConsumerDotnet 'production-arming-build.log' @('build', $armingProject, '-c', 'Release', '--no-restore')
$armingAssets = Get-Content -LiteralPath (Join-Path $armingConsumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($armingAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Production arming consumer contains source project references.'
}
foreach ($armingPackage in 'Runtime', 'Abstractions') {
    if (-not $armingAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $armingPackage + '/0.1.0-dev.1')]) {
        throw "Production arming consumer package missing: $armingPackage"
    }
}
$armingDll = Join-Path $armingConsumer 'bin/Release/net6.0/ProductionArmingConsumer.dll'
$armingOutput = & dotnet $armingDll
if ($LASTEXITCODE -ne 0) { throw 'Production arming package consumer execution failed.' }
$armingEvidence = $armingOutput | ConvertFrom-Json
if ($armingEvidence.id -cne 'V147_N01' -or $armingEvidence.result -cne 'Pass') {
    throw 'Production arming package consumer result is invalid.'
}
$armingEvidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $armingRun 'production-arming-consumer-evidence.json') -Encoding utf8
$armingOutput
