param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t33Repo = (Get-Location).Path
$t33Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t33Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t33Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t33Current = @($t33Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t33Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t33Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t33Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t33Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t33Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t33Counters = $t33Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t33Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t33Counters.total;passed=[int]$t33Counters.passed;failed=[int]$t33Counters.failed;
        notExecuted=[int]$t33Counters.notExecuted}
})
if ($t33Tests.Count -ne 7 -or @($t33Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t33Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t33Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t33Assemblies)
foreach ($t33Test in $t33Tests) { Copy-Item -LiteralPath $t33Test.assembly -Destination $t33Assemblies }
$t33Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t33Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t33Directory in @('preview-session-demo','preview-consumer-tests','recipe-activation-demo','recipe-activation-consumer-tests','plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t33Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t33Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html' })
}
foreach ($t33Consumer in @(
    'preview-session-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'recipe-activation-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'plc-result-contract-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'hikrobot-consumer/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll',
    'camera-conformance-consumer/bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll',
    'calibration-consumer/bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll')) {
    $t33Artifacts += Get-Item -LiteralPath (Join-Path $Run $t33Consumer)
}
$t33Hashes = @($t33Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t33Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t33Current.Count;sourceDrift=0;
    testTotal=($t33Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t33Hashes.Count;
    calibrationConsumerSha256=($t33Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4


