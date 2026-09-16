param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$PackageFeed,
    [string]$DependencyFeed = $env:NUGET_PACKAGES)
$ErrorActionPreference = 'Stop'
$consumerRoot = Join-Path ([IO.Path]::GetFullPath($Run)) 'diagnostics-consumer'
if (Test-Path -LiteralPath $consumerRoot) { throw 'Use a fresh diagnostics consumer directory.' }
[void][IO.Directory]::CreateDirectory($consumerRoot)
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net6.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" />
    <PackageReference Include="SharpInspect.NET.Wpf" Version="0.1.0-dev.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="6.0.1" />
  </ItemGroup>
</Project>
'@
$program = @'
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
namespace SharpInspect.SampleHost;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        var services = new ServiceCollection().AddSharpInspectRuntime();
        var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IDiagnosticPipelineHealthQuery>().ReadHealth();
        if (health.Configured || health.PolicyHash is not null || health.Safe is not null) return 3;
        _ = provider.GetRequiredService<IDiagnosticHistoryQuery>();
        _ = provider.GetRequiredService<IManagedFaultBoundary>();
        provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return DiagnosticFatalProbe.Run(args[0], args[1]);
    }
}
'@
Set-Content -LiteralPath (Join-Path $consumerRoot 'Consumer.csproj') -Value $project -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerRoot 'Program.cs') -Value $program -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../samples/SharpInspect.SampleHost/DiagnosticFatalProbe.cs') `
    -Destination (Join-Path $consumerRoot 'DiagnosticFatalProbe.cs')
$config = Join-Path $consumerRoot 'NuGet.Config'
$feed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageFeed))
if ([string]::IsNullOrWhiteSpace($DependencyFeed)) {
    $DependencyFeed = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*','').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the local dependency feed.' }
}
if (-not (Test-Path -LiteralPath $DependencyFeed -PathType Container)) { throw 'Local dependency feed missing.' }
$dependencyFeed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($DependencyFeed))
Set-Content -LiteralPath $config -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/>
<add key="ticket" value="$feed"/><add key="dependencies" value="$dependencyFeed"/>
</packageSources></configuration>
"@
$projectPath = Join-Path $consumerRoot 'Consumer.csproj'
& dotnet restore $projectPath --configfile $config --packages (Join-Path $consumerRoot 'packages') *> (Join-Path $consumerRoot 'restore.log')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics consumer restore failed.' }
& dotnet build $projectPath -c Release --no-restore -m:1 *> (Join-Path $consumerRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics consumer build failed.' }
$consumerExe = Join-Path $consumerRoot 'bin/Release/net6.0-windows/Consumer.exe'
$observations = @()
foreach ($mode in @('dispatcher','domain','hang','domain-race','process-exit')) {
    $manifest = Join-Path $consumerRoot ($mode + '.json')
    $start = [Diagnostics.ProcessStartInfo]::new($consumerExe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add($manifest); $start.ArgumentList.Add($mode)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw 'Diagnostic fatal consumer deadline exceeded.' }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $consumerRoot ($mode + '.log')) -Value $text -Encoding utf8
        if (-not [string]::IsNullOrWhiteSpace($text)) { throw 'Fatal consumer unexpectedly wrote console text.' }
        $evidence = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($process.ExitCode -ne 1 -or $evidence.Calls -ne 1 -or $evidence.Lifecycle -cne 'Stopped' -or
            $evidence.Ready -ne $false -or $evidence.Arm -cne 'Disarmed' -or $evidence.ControlledShutdownObserved -ne $true) {
            throw 'Diagnostic fatal consumer contract failed.'
        }
        $verificationId = if ($mode -eq 'domain-race') { 'V156_H02' } elseif ($mode -eq 'process-exit') { 'V156_H03' } else { 'V156_H01' }
        if ($evidence.VerificationId -cne $verificationId) { throw 'Fatal probe identity differs.' }
        if ($mode -eq 'domain-race' -and
            ((Get-Content -LiteralPath ($manifest + '.exit-started') -Raw).Trim() -cne '1' -or
             (Get-Content -LiteralPath ($manifest + '.domain-raised') -Raw).Trim() -cne '1' -or
             (Get-Content -LiteralPath ($manifest + '.exit-calls') -Raw).Trim() -cne '2')) { throw 'Fatal exit race was not covered.' }
        if (Test-Path -LiteralPath ($manifest + '.process-exit')) { throw 'Fatal termination entered ProcessExit callbacks.' }
        $observations += [ordered]@{mode=$mode;verificationId=$verificationId;publicApiOnly=($mode -ne 'domain-race');exitCode=$process.ExitCode;elapsedMs=$watch.ElapsedMilliseconds;
            evidenceSha256=(Get-FileHash -LiteralPath $manifest).Hash;productionAcceptance='NotRun'}
    }
    finally { $process.Dispose() }
}
[ordered]@{verificationId='V156_H01';verificationIds=@('V156_H01','V156_H02','V156_H03');result='Pass';runs=$observations;
    runtimeAssemblySha256=(Get-FileHash (Join-Path $consumerRoot 'bin/Release/net6.0-windows/SharpInspect.Runtime.dll')).Hash;
    wpfAssemblySha256=(Get-FileHash (Join-Path $consumerRoot 'bin/Release/net6.0-windows/SharpInspect.Wpf.dll')).Hash} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $consumerRoot 'validation.json') -Encoding utf8
Write-Output 'V156_H01 packaged Runtime/WPF fatal consumer PASS (5 modes, including internal exit-race injection; no production acceptance).'
