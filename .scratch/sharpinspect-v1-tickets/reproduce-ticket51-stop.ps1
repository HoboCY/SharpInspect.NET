$ErrorActionPreference = 'Stop'
$taskPreviousTemp = $env:TEMP
$taskPreviousTmp = $env:TMP
$taskRoot = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket51/stop-diagnostics-01'
$taskExit = 1
try {
    $env:TEMP = $env:TMP = 'F:/temp'
    [void][IO.Directory]::CreateDirectory($taskRoot)
    [ordered]@{ CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Repetitions=3;
        Filter='FullyQualifiedName~V145_S01_GracefulStopPreservesCurrentCycleAndRequiresSeparateArm';
        Rule='Three fixed repetitions. Preserve all results; do not stop at the first pass or retry until green.';
        SourceHashes=@('tests/SharpInspect.Runtime.Tests/ProductionInspectionRuntimeTests.cs',
            'tests/SharpInspect.Runtime.Tests/ProductionStopAbortTests.cs') | ForEach-Object {
                [ordered]@{ Path=$_; Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
            }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'plan.json') -Encoding utf8
    & dotnet build tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-restore --nologo -m:1 2>&1 |
        Tee-Object -FilePath (Join-Path $taskRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic candidate build failed.' }
    $taskOutcomes = @()
    foreach ($taskIndex in 1..3) {
        $taskRun = Join-Path $taskRoot ('run-' + $taskIndex)
        [void][IO.Directory]::CreateDirectory($taskRun)
        & dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~V145_S01_GracefulStopPreservesCurrentCycleAndRequiresSeparateArm' --logger 'trx;LogFileName=stop.trx' --results-directory $taskRun 2>&1 |
            Tee-Object -FilePath (Join-Path $taskRun 'tests.log')
        $taskOutcomes += [ordered]@{ Repetition=$taskIndex; ExitCode=$LASTEXITCODE; Directory=$taskRun }
        $taskOutcomes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskRoot 'outcomes.json') -Encoding utf8
    }
    $taskExit = [int](@($taskOutcomes | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0)
} finally {
    $env:TEMP = $taskPreviousTemp
    $env:TMP = $taskPreviousTmp
}
exit $taskExit
