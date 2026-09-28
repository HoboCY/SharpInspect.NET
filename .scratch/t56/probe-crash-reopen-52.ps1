$ErrorActionPreference='Stop'
$taskRoot='F:\si56crash52'
if(Test-Path -LiteralPath $taskRoot){throw 'Fresh evidence root required.'}
[void][IO.Directory]::CreateDirectory($taskRoot)
$env:TEMP='F:\temp\SharpInspect-T56'; $env:TMP=$env:TEMP
$taskProject=@'
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>SharpInspect.Runtime.Tests</AssemblyName><IsTestProject>true</IsTestProject><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>
<PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1"/>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
<PackageReference Include="xunit" Version="2.9.3"/>
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
</ItemGroup>
</Project>
'@
Set-Content -LiteralPath (Join-Path $taskRoot 'Probe.csproj') -Value $taskProject -Encoding utf8
Copy-Item -LiteralPath 'F:\si56pre49\diagnostics-consumer\NuGet.Config' -Destination (Join-Path $taskRoot 'NuGet.Config')
$taskSource=Get-Content -LiteralPath 'tests/SharpInspect.Runtime.Tests/ProductionStopCrashTests.cs' -Raw
$taskSource=$taskSource.Replace('            var marker = Path.Combine(childRoot, "accepted.tmp");', '            await File.WriteAllTextAsync(Path.Combine(childRoot, "owner.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));'+[Environment]::NewLine+'            var marker = Path.Combine(childRoot, "accepted.tmp");')
$taskSource=$taskSource.Replace('            // Only the explicitly created test process tree is terminated.', '            using var owner = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "owner.pid"))));'+[Environment]::NewLine+'            _ = owner.Handle;'+[Environment]::NewLine+'            // Only the explicitly created test process tree is terminated.')
$taskSource=$taskSource.Replace('            var correlation = Guid.Parse(await File.ReadAllTextAsync(acceptedPath));','            var ownerExitedAfterLauncher = owner.HasExited;'+[Environment]::NewLine+'            var awaitOwner = Environment.GetEnvironmentVariable("SHARPINSPECT_PROBE_AWAIT_OWNER") == "1";'+[Environment]::NewLine+'            if (awaitOwner) await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));'+[Environment]::NewLine+'            var correlation = Guid.Parse(await File.ReadAllTextAsync(acceptedPath));')
$taskSource=$taskSource.Replace('            Assert.True((await store.Initialization).Committed);','            var initialization = await store.Initialization;'+[Environment]::NewLine+'            await File.WriteAllTextAsync(Path.Combine(root, "observation.json"), System.Text.Json.JsonSerializer.Serialize(new { initialization.Committed, initialization.ReasonCode, OwnerId=owner.Id, ownerExitedAfterLauncher, OwnerExitedAfterInitialization=owner.HasExited, awaitOwner }));'+[Environment]::NewLine+'            Console.WriteLine("PROBE_EVIDENCE=" + root);'+[Environment]::NewLine+'            Assert.True(initialization.Committed, initialization.ReasonCode);')
Set-Content -LiteralPath (Join-Path $taskRoot 'ProductionStopCrashTests.cs') -Value $taskSource -Encoding utf8
& dotnet restore (Join-Path $taskRoot 'Probe.csproj') --configfile (Join-Path $taskRoot 'NuGet.Config') --packages (Join-Path $taskRoot 'packages') *> (Join-Path $taskRoot 'restore.log')
if($LASTEXITCODE -ne 0){throw 'Probe restore failed.'}
& dotnet build (Join-Path $taskRoot 'Probe.csproj') -c Release --no-restore --no-incremental -m:1 *> (Join-Path $taskRoot 'build.log')
if($LASTEXITCODE -ne 0){throw 'Probe build failed.'}
$taskDll=Join-Path $taskRoot 'bin/Release/net6.0/SharpInspect.Runtime.Tests.dll'
$taskRows=@()
foreach($taskAttempt in 1..12) {
    $env:SHARPINSPECT_PROBE_AWAIT_OWNER=if($taskAttempt -le 8){'0'}else{'1'}
    $taskLog=Join-Path $taskRoot ('attempt-'+$taskAttempt+'.log')
    & dotnet vstest $taskDll /Logger:trx ('/ResultsDirectory:'+(Join-Path $taskRoot ('attempt-'+$taskAttempt))) *> $taskLog
    $taskRows += [ordered]@{attempt=$taskAttempt;awaitOwner=($env:SHARPINSPECT_PROBE_AWAIT_OWNER -eq '1');exitCode=$LASTEXITCODE;log=$taskLog}
    $taskRows[-1] | ConvertTo-Json -Compress | Write-Output
}
$taskRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot 'attempts.json') -Encoding utf8
