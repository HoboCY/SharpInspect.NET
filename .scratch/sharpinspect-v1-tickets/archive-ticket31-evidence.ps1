param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t31Repo = (Get-Location).Path
$t31Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t31Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t31Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t31Current = @($t31Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t31Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t31Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t31Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t31Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t31Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t31Counters = $t31Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t31Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t31Counters.total;passed=[int]$t31Counters.passed;failed=[int]$t31Counters.failed;
        notExecuted=[int]$t31Counters.notExecuted}
})
if ($t31Tests.Count -ne 7 -or @($t31Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t31Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t31Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t31Assemblies)
foreach ($t31Test in $t31Tests) { Copy-Item -LiteralPath $t31Test.assembly -Destination $t31Assemblies }
$t31Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t31Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t31Directory in @('plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t31Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t31Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html' })
}
foreach ($t31Consumer in @(
    'plc-result-contract-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'hikrobot-consumer/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll',
    'camera-conformance-consumer/bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll',
    'calibration-consumer/bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll')) {
    $t31Artifacts += Get-Item -LiteralPath (Join-Path $Run $t31Consumer)
}
$t31Hashes = @($t31Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t31Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t31Current.Count;sourceDrift=0;
    testTotal=($t31Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t31Hashes.Count;
    calibrationConsumerSha256=($t31Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4

