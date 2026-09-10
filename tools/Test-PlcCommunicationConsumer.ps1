param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$PackageFeed)
$ErrorActionPreference = 'Stop'
$plcConsumer = Join-Path $Run 'plc-communication-consumer'
if (Test-Path -LiteralPath $plcConsumer) { throw 'Use a fresh PLC communication consumer directory.' }
[void][IO.Directory]::CreateDirectory($plcConsumer)
$plcProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" /></ItemGroup>
</Project>
'@
$plcProgram = @'
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Qualification;
var policy = new PlcCommunicationPolicy("Consumer.Plc", "1", TimeSpan.FromMilliseconds(20),
    TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40),
    TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(200),
    2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
var legacy = new ModbusQualificationProfile("Consumer.Modbus", "1", "Scenario.A", "127.0.0.1", 1502,
    1, 100, 200, 1, new string('A', 64), policy.PollInterval, policy.OperationTimeout, TimeSpan.FromSeconds(1),
    QualificationEvidenceCaptureMode.None);
var binding = new ModbusCommunicationBinding(policy, 300, 400);
var bound = new ModbusQualificationProfile("Consumer.Modbus", "1", "Scenario.A", "127.0.0.1", 1502,
    1, 100, 200, 1, new string('A', 64), policy.PollInterval, policy.OperationTimeout, TimeSpan.FromSeconds(1),
    QualificationEvidenceCaptureMode.None, binding);
if (legacy.CommunicationBinding is not null || bound.ContentHash == legacy.ContentHash)
    throw new Exception("ProfileBindingNotExplicit");
if (new PlcCommunicationHealth(true, false, false, 1, false, false, 0, 1, "Connecting").Healthy)
    throw new Exception("TransportWasTreatedAsHealth");
if (new PlcCommunicationHealth(true, true, true, 0, true, false, 0, 1, "InvalidEpoch").Healthy)
    throw new Exception("ZeroEpochWasTreatedAsHealth");
await using var runtime = new StationRuntime();
var state = await runtime.GetSnapshotAsync();
if (state.Ready || state.ArmState != ProductionArmState.Disarmed || state.PlcCommunication is not null)
    throw new Exception("UnconfiguredRuntimeGrantedCommunicationHealth");
Console.WriteLine(JsonSerializer.Serialize(new { CaseId = "V141_N01", Result = "Pass",
    PolicyHash = policy.ContentHash, LegacyProfileHash = legacy.ContentHash, ProfileHash = bound.ContentHash,
    TransportOnlyHealthy = false, RuntimeReady = state.Ready, PlcNetworkUsed = false,
    AbstractionsAssembly = typeof(PlcCommunicationPolicy).Assembly.Location,
    RuntimeAssembly = typeof(ModbusQualificationProfile).Assembly.Location,
    PhysicalQualification = "NotRun" }));
'@
[IO.File]::WriteAllText((Join-Path $plcConsumer 'Consumer.csproj'), $plcProject)
[IO.File]::WriteAllText((Join-Path $plcConsumer 'Program.cs'), $plcProgram)
$plcConfig = '<configuration><packageSources><clear/><add key="ticket" value="' +
    [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageFeed)) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>' +
    '<packageSourceMapping><packageSource key="ticket"><package pattern="SharpInspect.NET.*"/></packageSource>' +
    '<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
[IO.File]::WriteAllText((Join-Path $plcConsumer 'NuGet.Config'), $plcConfig)
$plcProjectPath = Join-Path $plcConsumer 'Consumer.csproj'
& dotnet restore $plcProjectPath --configfile (Join-Path $plcConsumer 'NuGet.Config') --packages (Join-Path $plcConsumer 'packages') *> (Join-Path $Run 'plc-communication-consumer-restore.log')
if ($LASTEXITCODE -ne 0) { throw 'PLC communication consumer restore failed.' }
& dotnet build $plcProjectPath -c Release --no-restore *> (Join-Path $Run 'plc-communication-consumer-build.log')
if ($LASTEXITCODE -ne 0) { throw 'PLC communication consumer build failed.' }
$plcAssets = Get-Content -LiteralPath (Join-Path $plcConsumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($plcAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'PLC communication consumer references source projects.'
}
$plcPackageBindings = @($plcAssets.libraries.PSObject.Properties | Where-Object {
    $_.Name -like 'SharpInspect.NET.*' -and $_.Value.type -ceq 'package'
} | ForEach-Object {
    $plcParts = $_.Name.Split('/')
    $plcPackageName = $plcParts[0] + '.' + $plcParts[1] + '.nupkg'
    $plcCandidate = Join-Path $PackageFeed $plcPackageName
    $plcRestored = Join-Path (Join-Path $plcConsumer 'packages') ($_.Value.path + '/' + $plcPackageName.ToLowerInvariant())
    $plcCandidateHash = (Get-FileHash -LiteralPath $plcCandidate -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $plcRestored -Algorithm SHA256).Hash -cne $plcCandidateHash) {
        throw "PLC communication consumer restored a different candidate package: $($_.Name)"
    }
    [ordered]@{ Package=$_.Name; Candidate=$plcCandidate; Restored=$plcRestored; Sha256=$plcCandidateHash }
})
if ($plcPackageBindings.Count -lt 2 -or
    @($plcPackageBindings | Where-Object { $_.Package -like 'SharpInspect.NET.Abstractions/*' }).Count -ne 1 -or
    @($plcPackageBindings | Where-Object { $_.Package -like 'SharpInspect.NET.Runtime/*' }).Count -ne 1) {
    throw 'PLC communication consumer did not bind the required candidate packages.'
}
$plcDll = Join-Path $plcConsumer 'bin/Release/net6.0/Consumer.dll'
$plcOutput = & dotnet $plcDll
if ($LASTEXITCODE -ne 0) { throw 'PLC communication consumer run failed.' }
$plcEvidence = $plcOutput | ConvertFrom-Json
if ($plcEvidence.CaseId -cne 'V141_N01' -or $plcEvidence.Result -cne 'Pass') { throw 'PLC communication consumer evidence invalid.' }
$plcEvidence | Add-Member -NotePropertyName ConsumerSha256 -NotePropertyValue (Get-FileHash -LiteralPath $plcDll -Algorithm SHA256).Hash
$plcEvidence | Add-Member -NotePropertyName CandidatePackageBindings -NotePropertyValue $plcPackageBindings
$plcEvidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'plc-communication-consumer-evidence.json') -Encoding utf8
Write-Output 'V141_N01 isolated NuGet communication policy and admission API PASS; physical qualification NOT_RUN.'
