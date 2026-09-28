param([Parameter(Mandatory)][string]$RunName,
    [Parameter(Mandatory)][string]$Filter)
$ErrorActionPreference = 'Stop'
$taskResults = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket36'
$taskLog = Join-Path $taskResults ($RunName + '.log')
if (Test-Path -LiteralPath $taskLog) { throw 'Do not overwrite a validation log.' }
$env:TEMP = Join-Path $taskResults 'process-temp'
$env:TMP = $env:TEMP
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-restore -p:UseSharedCompilation=false -nodeReuse:false -m:1 --filter $Filter --logger ('trx;LogFileName=' + $RunName + '.trx') --results-directory $taskResults *> $taskLog
$taskExit = $LASTEXITCODE
Get-Content -LiteralPath $taskLog -Tail 45
exit $taskExit
