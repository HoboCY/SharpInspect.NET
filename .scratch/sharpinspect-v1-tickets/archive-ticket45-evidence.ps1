param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t45Repo = (Get-Location).Path
$t45Validation = Get-Content -LiteralPath (Join-Path $Run 'validation-composite.json') -Raw | ConvertFrom-Json
if ($t45Validation.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$t45Sources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$t45Current = @($t45Sources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($t45Repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if (($t45Current | ConvertTo-Json -Depth 4 -Compress) -cne
    ($t45Validation.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift after gate.' }
$t45Tests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$t45Trx = Get-Content -LiteralPath $_.FullName -Raw
    $t45Counters = $t45Trx.TestRun.ResultSummary.Counters
    [ordered]@{path=$_.Name;assembly=@($t45Trx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase;
        total=[int]$t45Counters.total;passed=[int]$t45Counters.passed;failed=[int]$t45Counters.failed;
        notExecuted=[int]$t45Counters.notExecuted}
})
if ($t45Tests.Count -ne 7 -or @($t45Tests | Where-Object {
    $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed }).Count -ne 0) { throw 'Unexpected suite result.' }
$t45Tests | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$t45Assemblies = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($t45Assemblies)
$t45Bindings = Get-Content -LiteralPath (Join-Path $Run 'base-binary-bindings.json') -Raw | ConvertFrom-Json
foreach ($t45Test in $t45Tests) {
    $t45Name = [IO.Path]::GetFileName($t45Test.assembly)
    $t45Original = Join-Path (Join-Path $Run 'base-tested-assemblies') $t45Name
    $t45Binding = @($t45Bindings.tests | Where-Object assembly -CEQ $t45Name)
    if ($t45Binding.Count -ne 1 -or (Get-FileHash -LiteralPath $t45Original).Hash -cne $t45Binding[0].sha256) {
        throw 'Original full-suite binary binding changed.'
    }
    Copy-Item -LiteralPath $t45Original -Destination $t45Assemblies
}
$t45Artifacts = @(Get-ChildItem -LiteralPath $Run -File | Where-Object {
    $_.Extension -in '.json','.log' -and $_.Name -ne 'artifact-hashes.json' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $t45Assemblies -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'additional-tested-assembly') -Filter '*.dll' -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'additional-tests') -File -Recurse) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File)
foreach ($t45Directory in @('production-recovery-demo','prior-run-evidence','modbus-qualification-demo','trace-storage-policy-demo','trace-storage-policy-consumer-tests','recipe-transfer-demo','station-qualification-demo','production-admission-demo','production-admission-consumer-tests','manual-session-demo','manual-consumer-tests','calibration-import-demo','calibration-import-consumer-tests','preview-session-demo','preview-consumer-tests','recipe-activation-demo','recipe-activation-consumer-tests','plc-result-payload','plc-result-contract-demo','plc-result-contract-consumer-tests','release-demo','recipe-release-consumer-tests','draft-demo','camera-acquisition','virtual-camera','camera-recovery-demo','camera-network-demo',
    'camera-setup-demo','camera-conformance','imaging-calibration-demo','calibration-session-demo')) {
    $t45Artifacts += @(Get-ChildItem -LiteralPath (Join-Path $Run $t45Directory) -File -Recurse |
        Where-Object { $_.Extension -in '.json','.png','.log','.trx','.mono8','.bin','.html','.sqlite','.txt' })
}
foreach ($t45Consumer in @(
    'production-recovery-consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll',
    'legacy-abi-consumer/AbiConsumer.dll',
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
    $t45Artifacts += Get-Item -LiteralPath (Join-Path $Run $t45Consumer)
}
$t45Hashes = @($t45Artifacts | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$t45Hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass';sourceCount=$t45Current.Count;sourceDrift=0;
    testTotal=($t45Tests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum;
    additionalTestTotal=$t45Validation.additionalBoundaryTests.passed;
    distinctPassedCases=$t45Validation.distinctPassedCases;
    artifactHashCount=$t45Hashes.Count;
    calibrationConsumerSha256=($t45Hashes | Where-Object { $_.path -like '*SharpInspect.CalibrationConsumer.dll' }).sha256} |
    ConvertTo-Json -Depth 4








