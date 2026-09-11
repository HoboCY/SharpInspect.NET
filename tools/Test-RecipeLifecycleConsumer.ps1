param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$lifecycleRun = [IO.Path]::GetFullPath($Run)
$lifecycleFeed = [IO.Path]::GetFullPath($PackageFeed)
$lifecycleConsumer = Join-Path $lifecycleRun 'recipe-lifecycle-consumer'
if (Test-Path -LiteralPath $lifecycleConsumer) { throw 'Use a fresh recipe lifecycle consumer directory.' }
[void][IO.Directory]::CreateDirectory($lifecycleConsumer)
$lifecycleProject = Join-Path $lifecycleConsumer 'RecipeLifecycleConsumer.csproj'
$lifecycleConfig = Join-Path $lifecycleConsumer 'NuGet.Config'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $lifecycleProject -Encoding utf8
$lifecycleXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($lifecycleFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($lifecycleConfig, $lifecycleXml, [Text.UTF8Encoding]::new($false))
@'
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
var hash = new string('A', 64);
var correlation = Guid.NewGuid();
var invocation = new CommandInvocation(CommandSource.PhysicalConsole, "lifecycle-consumer");
var draft = Guid.NewGuid();
var abandon = new AbandonRecipeDraftCommand(correlation, invocation, draft, 1, hash, "Unused trial");
var changedReason = new AbandonRecipeDraftCommand(correlation, invocation, draft, 1, hash, "Different reason");
var recipe = new RecipeReference("V148.Consumer.Recipe", "1", hash);
var release = Guid.NewGuid();
var retire = new RetireReleasedRecipeCommand(correlation, invocation, recipe, release, hash, null, "Withdraw version");
var active = new RecipeActivationReference(1, Guid.NewGuid(), hash);
var withActive = new RetireReleasedRecipeCommand(correlation, invocation, recipe, release, hash, active, "Withdraw version");
var transition = new RecipeLifecycleReference(1, Guid.NewGuid(), hash);
var derive = new RecipeDraftDerivationRequest(Guid.NewGuid(), Guid.NewGuid(), transition, "New process trial", invocation);
var store = new RecipeLifecycleStoreOptions();
var runtime = new RecipeLifecycleRuntimeOptions();
if (abandon.AuthorizationTarget == changedReason.AuthorizationTarget ||
    retire.AuthorizationTarget == withActive.AuthorizationTarget ||
    derive.NewDraftId == draft || derive.Source != transition ||
    !AuthorizationPolicy.Development.RequiresStepUp(Permission.AbandonRecipeDraft) ||
    !AuthorizationPolicy.Development.RequiresStepUp(Permission.RetireRecipe) ||
    typeof(RecipeLifecycleRecord).GetConstructors().Length != 0 ||
    typeof(RecipeDraftLifecycleLineage).GetConstructors().Length != 0 ||
    store.MaxEvents < 1 || runtime.QuiescenceTimeout <= TimeSpan.Zero ||
    (byte)RecipeDraftLifecycleState.Abandoned != 2 || (byte)ReleasedRecipeLifecycleState.Retired != 2)
    throw new InvalidOperationException("V148PublicConsumerContractMismatch");
Console.WriteLine(JsonSerializer.Serialize(new
{
    id = "V148_N01", result = "Pass", scope = "public package contracts; no lifecycle transition or hardware admission",
    abandon = abandon.AuthorizationTarget, retire = retire.AuthorizationTarget,
    contracts = new[] { typeof(IRecipeLifecycleService).FullName, typeof(IRecipeLifecycleHistoryQuery).FullName,
        typeof(IRecipeDraftDerivationService).FullName, typeof(SqliteRecipeLifecycleQuery).FullName }
}));
'@ | Set-Content -LiteralPath (Join-Path $lifecycleConsumer 'Program.cs') -Encoding utf8
function Invoke-LifecycleConsumerDotnet([string]$Log, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $lifecycleRun $Log)
    if ($LASTEXITCODE -ne 0) { throw "Recipe lifecycle consumer failed: $Log" }
}
Invoke-LifecycleConsumerDotnet 'recipe-lifecycle-restore.log' @('restore', $lifecycleProject,
    '--configfile', $lifecycleConfig, '--packages', (Join-Path $lifecycleRun 'recipe-lifecycle-consumer-cache'))
Invoke-LifecycleConsumerDotnet 'recipe-lifecycle-build.log' @('build', $lifecycleProject, '-c', 'Release', '--no-restore')
$lifecycleAssets = Get-Content -LiteralPath (Join-Path $lifecycleConsumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($lifecycleAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Recipe lifecycle consumer contains source project references.'
}
foreach ($lifecyclePackage in 'Runtime', 'Abstractions') {
    if (-not $lifecycleAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $lifecyclePackage + '/0.1.0-dev.1')]) {
        throw "Recipe lifecycle consumer package missing: $lifecyclePackage"
    }
}
$lifecycleDll = Join-Path $lifecycleConsumer 'bin/Release/net6.0/RecipeLifecycleConsumer.dll'
$lifecycleOutput = & dotnet $lifecycleDll
if ($LASTEXITCODE -ne 0) { throw 'Recipe lifecycle package consumer execution failed.' }
$lifecycleEvidence = $lifecycleOutput | ConvertFrom-Json
if ($lifecycleEvidence.id -cne 'V148_N01' -or $lifecycleEvidence.result -cne 'Pass') {
    throw 'Recipe lifecycle package consumer result is invalid.'
}
$lifecycleEvidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $lifecycleRun 'recipe-lifecycle-consumer-evidence.json') -Encoding utf8
$lifecycleOutput
