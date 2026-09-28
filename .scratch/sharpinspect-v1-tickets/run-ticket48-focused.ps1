param([Parameter(Mandatory)][string]$RunName, [string]$Filter = 'FullyQualifiedName~V148_')
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-z0-9-]+$') { throw 'Use a simple new development run name.' }
$t48Run = Join-Path 'F:\temp\SharpInspect.NET-validation-artifacts\ticket48\development' $RunName
if (Test-Path -LiteralPath $t48Run) { throw 'Preserve existing development evidence; choose a fresh run name.' }
$t48Temp = Join-Path $t48Run 'temp'
New-Item -ItemType Directory -Path $t48Temp -Force | Out-Null
$t48PreviousTemp = $env:TEMP
$t48PreviousTmp = $env:TMP
try {
    $env:TEMP = $t48Temp
    $env:TMP = $t48Temp
    [pscustomobject]@{Temp=$env:TEMP;Tmp=$env:TMP;filter=$Filter;head=(git rev-parse HEAD);
        utc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $t48Run 'environment.json')
    dotnet test 'D:\projects\SharpInspect.NET\tests\SharpInspect.Runtime.Tests\SharpInspect.Runtime.Tests.csproj' `
        --no-restore -c Release -p:Platform=x64 -p:UseSharedCompilation=false --disable-build-servers `
        --filter $Filter --logger 'trx;LogFileName=tests.trx' --results-directory $t48Run -v:minimal `
        *> (Join-Path $t48Run 'test.log')
    $t48Exit = $LASTEXITCODE
}
finally { $env:TEMP = $t48PreviousTemp; $env:TMP = $t48PreviousTmp }
Get-Content -LiteralPath (Join-Path $t48Run 'test.log') -Tail 30
exit $t48Exit
