$ErrorActionPreference = 'Stop'
$taskBase = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/consumer-diagnostic-01'
$taskDll = Join-Path $taskBase 'production-recovery-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll'
$taskIdentity = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/20260911-231805-790/development-identity-policy.json'
$taskKeyDirectory = Join-Path $taskBase 'probe-03/private-keys'
$taskRows = @()
foreach ($taskNumber in 3,4) {
    $taskProbe = Join-Path $taskBase ('probe-{0:D2}' -f $taskNumber)
    if (Test-Path -LiteralPath $taskProbe) { throw 'Probe must use a fresh database directory.' }
    [void][IO.Directory]::CreateDirectory($taskProbe)
    $taskLog = Join-Path $taskBase ('probe-{0:D2}.log' -f $taskNumber)
    $taskKeyExisted = Test-Path -LiteralPath $taskKeyDirectory
    & dotnet $taskDll --smoke --production-recovery-ui --trace-db (Join-Path $taskProbe 'station.sqlite') --audit-key 'SharpInspect.T46.ConsumerDiagnostic.20260911.03' --audit-key-directory $taskKeyDirectory --identity-policy $taskIdentity *> $taskLog
    $taskExit = $LASTEXITCODE
    $taskRows += [ordered]@{probe=$taskNumber;consumerSha256=(Get-FileHash -LiteralPath $taskDll).Hash;databaseWasAbsent=$true;keyDirectoryExisted=$taskKeyExisted;exitCode=$taskExit;log=$taskLog}
    Get-Content -LiteralPath $taskLog -Tail 13
}
$taskRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskBase 'key-reuse-probes.json') -Encoding utf8
