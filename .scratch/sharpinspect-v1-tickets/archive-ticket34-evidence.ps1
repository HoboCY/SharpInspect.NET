param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t34Repo = (Get-Location).Path
$t34Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t34Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t34Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t34Current = @($t34Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t34Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t34Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t34Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t34Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t34Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t34Counters = $t34Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t34Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t34Counters.total;passed=[int]$t34Counters.passed;failed=[int]$t34Counters.failed;
        notExecuted=[int]$t34Counters.notExecuted}
})
if ($t34Tests.Count -ne 7 -or @($t34Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t34Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t34Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t34Assemblies)
foreach ($t34Test in $t34Tests) { Copy-Item -LiteralPath $t34Test.assembly -Destination $t34Assemblies }
$t34Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t34Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t34Directory in @('calibration-import-demo','calibration-import-consumer-tests','preview-session-demo','preview-consumer-tests','recipe-activation-demo','recipe-activation-consumer-tests','plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t34Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t34Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html' })
}
foreach ($t34Consumer in @(
    'calibration-import-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'preview-session-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'recipe-activation-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'plc-result-contract-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'hikrobot-consumer/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll',
    'camera-conformance-consumer/bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll',
    'calibration-consumer/bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll')) {
    $t34Artifacts += Get-Item -LiteralPath (Join-Path $Run $t34Consumer)
}
$t34Hashes = @($t34Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t34Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t34Current.Count;sourceDrift=0;
    testTotal=($t34Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t34Hashes.Count;
    calibrationConsumerSha256=($t34Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4



