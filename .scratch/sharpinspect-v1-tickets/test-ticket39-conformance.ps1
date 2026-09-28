$ErrorActionPreference = 'Stop'
$env:TEMP = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket39\process-temp'
$env:TMP = $env:TEMP
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$taskRun = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket39\32-conformance-precheck'
$taskFeed = Join-Path $taskRun 'packages'
if (Test-Path -LiteralPath $taskRun) { throw 'Use a fresh conformance precheck directory.' }
[void][IO.Directory]::CreateDirectory($taskFeed)
Get-ChildItem -LiteralPath 'F:\temp\SharpInspect.NET-validation-artifacts\ticket39\20260911-014728-817\packages' -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike 'SharpInspect.NET.Cameras.Conformance.*' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskFeed }
& dotnet test tests/SharpInspect.Cameras.Conformance.Tests/SharpInspect.Cameras.Conformance.Tests.csproj -c Release -p:UseSharedCompilation=false --logger trx --results-directory (Join-Path $taskRun 'tests') *> (Join-Path $taskRun 'conformance-tests.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskRun 'conformance-tests.log') -Tail 35; throw 'Conformance tests failed.' }
& dotnet pack src/SharpInspect.Cameras.Conformance/SharpInspect.Cameras.Conformance.csproj -c Release --no-build --no-restore -o $taskFeed *> (Join-Path $taskRun 'conformance-pack.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskRun 'conformance-pack.log') -Tail 20; throw 'Conformance pack failed.' }
$taskConfig = Join-Path $taskRun 'NuGet.Config'
$taskXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' + [Security.SecurityElement]::Escape($taskFeed) + '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($taskConfig,$taskXml,[Text.UTF8Encoding]::new($false))
& './tools/Test-CameraConformanceConsumer.ps1' -RunDirectory $taskRun -NugetConfig $taskConfig -SourceRevision (git rev-parse HEAD).Trim()
