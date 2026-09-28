$ErrorActionPreference = 'Stop'
$taskSource = (Resolve-Path -LiteralPath 'src/SharpInspect.Runtime/StationRuntime.ProductionArmPolicies.cs').Path
$taskRoot = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket47\20260912-development-01'
$taskOriginal = [IO.File]::ReadAllBytes($taskSource)
$taskOriginalHash = (Get-FileHash -LiteralPath $taskSource).Hash
$taskGuard = @'
        if (_recipeChangeInProgress || owner.RecipeChangeRequest is not null ||
            _activationReservation is { PlcOwned: true } || _automaticProductionArm is { Terminal: false })
            return;
'@
$taskText = [IO.File]::ReadAllText($taskSource).Replace("`r`n", "`n")
$taskGuard = $taskGuard.Replace("`r`n", "`n")
if ($taskText.Split($taskGuard, [StringSplitOptions]::None).Count -ne 2) { throw 'Unique startup arbitration guard required.' }
$taskRedExit = $null
try {
    [IO.File]::WriteAllText($taskSource, $taskText.Replace($taskGuard, ''), [Text.UTF8Encoding]::new($false))
    dotnet build tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-restore -t:Rebuild -m:1 *> (Join-Path $taskRoot 'cause-strong-red44-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Mutation build failed.' }
    Copy-Item -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll' -Destination (Join-Path $taskRoot 'cause-strong-red44-Runtime.dll')
    dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~V147_R17' --logger 'trx;LogFileName=cause-strong-red44.trx' --results-directory $taskRoot -m:1 *> (Join-Path $taskRoot 'cause-strong-red44.log')
    $taskRedExit = $LASTEXITCODE
}
finally {
    [IO.File]::WriteAllBytes($taskSource, $taskOriginal)
    [IO.File]::SetLastWriteTimeUtc($taskSource, [DateTime]::UtcNow)
    if ((Get-FileHash -LiteralPath $taskSource).Hash -cne $taskOriginalHash) { throw 'Exact fixed source restoration failed.' }
    dotnet build tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-restore -t:Rebuild -m:1 *> (Join-Path $taskRoot 'restored-fixed45-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Restored fixed binary rebuild failed.' }
}
[xml]$taskRed = Get-Content -LiteralPath (Join-Path $taskRoot 'cause-strong-red44.trx') -Raw
$taskRows = @($taskRed.SelectNodes('//*[local-name()="UnitTestResult"]'))
if ($taskRedExit -ne 1 -or $taskRows.Count -ne 1 -or $taskRows[0].outcome -cne 'Failed' -or
    ($taskRows[0].Output.ErrorInfo.Message -notmatch 'Assert.DoesNotContain' -and
        $taskRows[0].Output.ErrorInfo.Message -notmatch 'ResetObserved, ProtocolFault')) {
    throw 'The mutation must fail forbidden Startup history or the complete handshake without ProtocolFault.'
}
[ordered]@{result='ExpectedRegressionDetected';test=$taskRows[0].testName;failure=$taskRows[0].Output.ErrorInfo.Message;
    stack=$taskRows[0].Output.ErrorInfo.StackTrace;fixedSourceSha256=$taskOriginalHash;
    fixedRuntimeSha256=(Get-FileHash -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll').Hash;
    mutatedRuntimeSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'cause-strong-red44-Runtime.dll')).Hash;
    exactSourceRestored=$true;explicitFixedRebuild=$true} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $taskRoot 'cause-strong-red44-evidence.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRoot 'cause-strong-red44-evidence.json')
