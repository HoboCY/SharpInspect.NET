param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t41Repo = (Get-Location).Path
$t41Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t41Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t41Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t41Current = @($t41Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t41Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t41Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t41Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t41Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t41Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t41Counters = $t41Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t41Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t41Counters.total;passed=[int]$t41Counters.passed;failed=[int]$t41Counters.failed;
        notExecuted=[int]$t41Counters.notExecuted}
})
if ($t41Tests.Count -ne 7 -or @($t41Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t41Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t41Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t41Assemblies)
foreach ($t41Test in $t41Tests) { Copy-Item -LiteralPath $t41Test.assembly -Destination $t41Assemblies }
$t41Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t41Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t41Directory in @('prior-run-evidence','modbus-qualification-demo','trace-storage-policy-demo','trace-storage-policy-consumer-tests','recipe-transfer-demo','station-qualification-demo','production-admission-demo','production-admission-consumer-tests','manual-session-demo','manual-consumer-tests','calibration-import-demo','calibration-import-consumer-tests','preview-session-demo','preview-consumer-tests','recipe-activation-demo','recipe-activation-consumer-tests','plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t41Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t41Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html' })
}
foreach ($t41Consumer in @(
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
    $t41Artifacts += Get-Item -LiteralPath (Join-Path $Run $t41Consumer)
}
$t41Hashes = @($t41Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t41Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t41Current.Count;sourceDrift=0;
    testTotal=($t41Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t41Hashes.Count;
    calibrationConsumerSha256=($t41Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4





