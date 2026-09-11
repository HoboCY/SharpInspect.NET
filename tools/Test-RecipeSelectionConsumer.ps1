param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$selectionRun = [IO.Path]::GetFullPath($Run)
$selectionFeed = [IO.Path]::GetFullPath($PackageFeed)
$selectionConsumer = Join-Path $selectionRun 'recipe-selection-consumer'
if (Test-Path -LiteralPath $selectionConsumer) { throw 'Use a fresh recipe selection consumer directory.' }
[void][IO.Directory]::CreateDirectory($selectionConsumer)
$selectionProject = Join-Path $selectionConsumer 'RecipeSelectionConsumer.csproj'
$selectionConfig = Join-Path $selectionConsumer 'NuGet.Config'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $selectionProject -Encoding utf8
$selectionXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($selectionFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($selectionConfig, $selectionXml, [Text.UTF8Encoding]::new($false))
@'
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

var recipe = new RecipeReference("V146.Consumer.Recipe", "exact-version-3", new string('A', 64));
var releaseId = Guid.Parse("83608691-240c-4817-b22d-1a49b91de596");
var entry = new RecipeSelectionMapEntry(7, recipe, releaseId, new string('B', 64));
var map = new RecipeSelectionMap("V146.Consumer.Map", "1", new[] { entry });
var selection = new RecipeSelectionPolicy("V146.Consumer.Selection", "1", RecipeSelectionMode.PlcRequestedActivation);
var command = new ChangeRecipeSelectionCommand(Guid.NewGuid(),
    new CommandInvocation(CommandSource.PhysicalConsole, "consumer"), selection, map, null, "Consumer exact map");
var policy = new PlcCommunicationPolicy("V146.Consumer.Communication", "1",
    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
    TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
var communication = new ModbusCommunicationBinding(policy, 300, 400);
var legacy = new ModbusProductionProfile("V146.Consumer.Profile", "1", "127.0.0.1", 1502, 1,
    100, 200, communication, TimeSpan.FromSeconds(2));
var binding = new ModbusRecipeChangeBinding(500, 600, TimeSpan.FromSeconds(10));
var mapped = new ModbusProductionProfile("V146.Consumer.Profile", "1", "127.0.0.1", 1502, 1,
    100, 200, communication, TimeSpan.FromSeconds(2), null, binding);
if (RecipeSelectionPolicy.Default.Mode != RecipeSelectionMode.LocalOperatorOnly ||
    map.Entries.Single().Recipe != recipe || map.Entries.Single().ReleaseId != releaseId ||
    command.ExpectedCurrent is not null || command.AuthorizationTarget.Length != 64 ||
    legacy.RecipeChange is not null || mapped.RecipeChange != binding ||
    mapped.ContentHash == legacy.ContentHash || mapped.EndpointBindingHash != legacy.EndpointBindingHash)
    throw new InvalidOperationException("V146PublicConsumerContractMismatch");
Console.WriteLine(JsonSerializer.Serialize(new
{
    id = "V146_N01", result = "Pass", scope = "public package contracts; no runtime or hardware admission",
    selection = selection.ContentHash, map = map.ContentHash, profile = mapped.ContentHash,
    contracts = new[] { typeof(IRecipeSelectionService).FullName, typeof(IRecipeSelectionQuery).FullName,
        typeof(IRecipeChangeHistoryQuery).FullName, typeof(SqliteRecipeSelectionQuery).FullName,
        typeof(RecipeSelectionStoreOptions).FullName }
}));
'@ | Set-Content -LiteralPath (Join-Path $selectionConsumer 'Program.cs') -Encoding utf8

function Invoke-SelectionConsumerDotnet([string]$Log, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $selectionRun $Log)
    if ($LASTEXITCODE -ne 0) { throw "Recipe selection consumer failed: $Log" }
}
Invoke-SelectionConsumerDotnet 'recipe-selection-restore.log' @('restore', $selectionProject,
    '--configfile', $selectionConfig, '--packages', (Join-Path $selectionRun 'recipe-selection-consumer-cache'))
Invoke-SelectionConsumerDotnet 'recipe-selection-build.log' @('build', $selectionProject, '-c', 'Release', '--no-restore')
$selectionAssets = Get-Content -LiteralPath (Join-Path $selectionConsumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($selectionAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Recipe selection consumer contains source project references.'
}
foreach ($selectionPackage in 'Runtime', 'Abstractions') {
    if (-not $selectionAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $selectionPackage + '/0.1.0-dev.1')]) {
        throw "Recipe selection consumer package missing: $selectionPackage"
    }
}
$selectionDll = Join-Path $selectionConsumer 'bin/Release/net6.0/RecipeSelectionConsumer.dll'
$selectionOutput = & dotnet $selectionDll
if ($LASTEXITCODE -ne 0) { throw 'Recipe selection package consumer execution failed.' }
$selectionEvidence = $selectionOutput | ConvertFrom-Json
if ($selectionEvidence.id -cne 'V146_N01' -or $selectionEvidence.result -cne 'Pass') {
    throw 'Recipe selection package consumer result is invalid.'
}
$selectionEvidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $selectionRun 'recipe-selection-consumer-evidence.json') -Encoding utf8
$selectionOutput
