$ErrorActionPreference = 'Stop'
$env:TEMP='F:\t57'
$env:TMP=$env:TEMP
$taskObserver = Start-Process pwsh -ArgumentList @('-NoProfile','-File','D:\projects\SharpInspect.NET\.scratch\t57\clipboard-observer24.ps1') -WindowStyle Hidden -PassThru
& dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore -m:1 --filter FullyQualifiedName~IdentityConsumerAcceptanceTests --logger trx --results-directory .scratch/t57/identity-observed24-tests *> .scratch/t57/identity-observed24-tests.log
$taskExit = $LASTEXITCODE
if (-not $taskObserver.WaitForExit(40000)) { throw 'Clipboard observer did not finish.' }
exit $taskExit
