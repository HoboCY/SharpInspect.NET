$ErrorActionPreference = 'Stop'
$env:TEMP = 'F:\t57'
$env:TMP = $env:TEMP
[void][IO.Directory]::CreateDirectory($env:TEMP)
$env:SHARPINSPECT_V157_EVIDENCE_ROOT = 'D:\projects\SharpInspect.NET\.scratch\t57\runtime-evidence-focused22'
& dotnet build SharpInspect.NET.sln -c Release --no-incremental -p:RestoreLockedMode=true -m:1 *> .scratch/t57/build-focused22.log
if ($LASTEXITCODE -ne 0) { throw 'Focused candidate build failed.' }
& dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore -m:1 --filter 'FullyQualifiedName~V157_|FullyQualifiedName~V155_M03|FullyQualifiedName~V147_R13|FullyQualifiedName~IdentityConsumerAcceptanceTests' --logger trx --results-directory .scratch/t57/focused22-tests *> .scratch/t57/focused22-tests.log
if ($LASTEXITCODE -ne 0) { throw 'Focused candidate tests failed.' }
& dotnet test tests/SharpInspect.Wpf.Tests/SharpInspect.Wpf.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory .scratch/t57/wpf22-tests *> .scratch/t57/wpf22-tests.log
if ($LASTEXITCODE -ne 0) { throw 'WPF candidate tests failed.' }
