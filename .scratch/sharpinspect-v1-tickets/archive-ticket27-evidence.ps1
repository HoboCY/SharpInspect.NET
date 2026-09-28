param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t27Repo = (Get-Location).Path
$t27Validation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($t27Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t27Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t27Current = @($t27Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t27Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t27Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t27Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t27Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t27Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t27Counters = $t27Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t27Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t27Counters.total;passed=[int]$t27Counters.passed;failed=[int]$t27Counters.failed;
        notExecuted=[int]$t27Counters.notExecuted}
})
if ($t27Tests.Count -ne 7 -or @($t27Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t27Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t27Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t27Assemblies)
foreach ($t27Test in $t27Tests) { Copy-Item -LiteralPath $t27Test.assembly -Destination $t27Assemblies }
$t27Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t27Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t27Directory in @('camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t27Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t27Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin' })
}
foreach ($t27Consumer in @(
    'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'hikrobot-consumer/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll',
    'camera-conformance-consumer/bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll',
    'calibration-consumer/bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll')) {
    $t27Artifacts += Get-Item -LiteralPath (Join-Path $Run $t27Consumer)
}
$t27Hashes = @($t27Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t27Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t27Current.Count;sourceDrift=0;
    testTotal=($t27Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    artifactHashCount=$t27Hashes.Count;
    calibrationConsumerSha256=($t27Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4

