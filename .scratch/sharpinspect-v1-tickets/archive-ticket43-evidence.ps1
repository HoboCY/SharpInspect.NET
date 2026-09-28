param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t43Repo = (Get-Location).Path
$t43Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t43Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t43Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t43Current = @($t43Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t43Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t43Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t43Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t43Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t43Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t43Counters = $t43Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t43Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t43Counters.total;passed=[int]$t43Counters.passed;failed=[int]$t43Counters.failed;
        notExecuted=[int]$t43Counters.notExecuted}
})
if ($t43Tests.Count -ne 7 -or @($t43Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t43Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t43Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t43Assemblies)
foreach ($t43Test in $t43Tests) { Copy-Item -LiteralPath $t43Test.assembly -Destination $t43Assemblies }
$t43Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t43Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t43Directory in @('prior-run-evidence','modbus-qualification-demo','trace-storage-policy-demo','trace-storage-policy-consumer-tests','recipe-transfer-demo','station-qualification-demo','production-admission-demo','production-admission-consumer-tests','manual-session-demo','manual-consumer-tests','calibration-import-demo','calibration-import-consumer-tests','preview-session-demo','preview-consumer-tests','recipe-activation-demo','recipe-activation-consumer-tests','plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t43Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t43Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html','.sqlite','.txt' })
}
foreach ($t43Consumer in @(
    'part-identity-consumer/bin/Release/net6.0/Consumer.dll',
    'production-inspection-consumer/bin/Release/net6.0/Consumer.dll',
    'plc-communication-consumer/bin/Release/net6.0/Consumer.dll',
    'modbus-qualification-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'trace-storage-policy-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'recipe-transfer-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'station-qualification-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'production-admission-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'manual-session-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'calibration-import-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'preview-session-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'recipe-activation-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'plc-result-contract-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'hikrobot-consumer/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll',
    'camera-conformance-consumer/bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll',
    'calibration-consumer/bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll')) {
    $t43Artifacts += Get-Item -LiteralPath (Join-Path $Run $t43Consumer)
}
$t43Hashes = @($t43Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t43Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t43Current.Count;sourceDrift=0;
    testTotal=($t43Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t43Hashes.Count;
    calibrationConsumerSha256=($t43Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4







