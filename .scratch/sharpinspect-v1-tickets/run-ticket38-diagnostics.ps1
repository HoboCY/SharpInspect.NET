$ErrorActionPreference = 'Stop'
$taskResults = @()
for ($taskIndex = 1; $taskIndex -le 10; $taskIndex++) {
    $taskName = '34-runtime-diagnostic-{0:D2}' -f $taskIndex
    & (Join-Path $PSScriptRoot 'test-ticket38-runtime.ps1') -RunName $taskName -Filter 'FullyQualifiedName~V133_P09|FullyQualifiedName~V137_R16|FullyQualifiedName~V137_R22|FullyQualifiedName~V137_S15'
    $taskExit = $LASTEXITCODE
    $taskResults += [ordered]@{iteration=$taskIndex;run=$taskName;exitCode=$taskExit;finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    $taskResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath 'F:\temp\SharpInspect.NET-validation-artifacts\ticket38\34-runtime-diagnostic-summary.json' -Encoding utf8
    if ($taskExit -ne 0) { exit $taskExit }
}
