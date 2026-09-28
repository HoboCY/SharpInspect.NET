$ErrorActionPreference = 'Stop'
$env:TEMP = 'F:\t57'
$env:TMP = $env:TEMP
$env:SHARPINSPECT_IDENTITY_CONSUMER = 'F:\si56composite53\ticket56\20260916-130538-173\consumer\bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
& dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore -m:1 --filter FullyQualifiedName~IdentityConsumerAcceptanceTests --logger trx --results-directory .scratch/t57/identity-parent23-tests *> .scratch/t57/identity-parent23-tests.log
$taskParentExit = $LASTEXITCODE
[ordered]@{kind='UnchangedParentConsumerIdentityControl';consumer=$env:SHARPINSPECT_IDENTITY_CONSUMER;consumerSha256=(Get-FileHash -LiteralPath $env:SHARPINSPECT_IDENTITY_CONSUMER).Hash;exit=$taskParentExit} | ConvertTo-Json | Set-Content -LiteralPath .scratch/t57/identity-parent23.json
Remove-Item Env:\SHARPINSPECT_IDENTITY_CONSUMER
& dotnet test tests/SharpInspect.Wpf.Tests/SharpInspect.Wpf.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory .scratch/t57/wpf23-tests *> .scratch/t57/wpf23-tests.log
exit $LASTEXITCODE
