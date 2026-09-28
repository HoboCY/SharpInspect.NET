param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskEvidence = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($taskEvidence.result -cne 'Pass') { throw 'Full gate must pass before archival.' }
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) + @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskCurrent = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
if (($taskCurrent | ConvertTo-Json -Depth 4 -Compress) -cne ($taskEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source changed since validation.' }
$taskTests = @(Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File | ForEach-Object {
    [xml]$taskTrx = Get-Content -LiteralPath $_.FullName -Raw
    $taskCounters = $taskTrx.TestRun.ResultSummary.Counters
    $taskAssembly = @($taskTrx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase
    [ordered]@{ path=$_.Name; assembly=$taskAssembly; total=[int]$taskCounters.total; passed=[int]$taskCounters.passed; failed=[int]$taskCounters.failed; notExecuted=[int]$taskCounters.notExecuted }
})
if ($taskTests.Count -ne 4 -or ($taskTests | Where-Object { $_.failed -ne 0 -or $_.notExecuted -ne 0 -or $_.total -ne $_.passed })) { throw 'Unexpected suite result.' }
$taskTests | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Run 'test-summary.json') -Encoding utf8
$taskRetained = Join-Path $Run 'tested-assemblies'
[void][IO.Directory]::CreateDirectory($taskRetained)
foreach ($taskTest in $taskTests) { Copy-Item -LiteralPath $taskTest.assembly -Destination $taskRetained }
$taskArtifacts = @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter '*.nupkg' -File) +
    @(Get-ChildItem -LiteralPath $taskRetained -Filter '*.dll' -File) +
    @(Get-Item -LiteralPath (Join-Path $Run 'consumer/bin/Release/net6.0-windows/SharpInspect.SampleHost.dll')) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'camera-acquisition') -Filter '*.json' -File -Recurse) +
    @(Get-Item -LiteralPath (Join-Path $Run 'camera-acquisition-replay-1.log'),(Join-Path $Run 'camera-acquisition-replay-2.log')) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'virtual-camera') -Filter 'replay-evidence.json' -File -Recurse) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'camera-recovery-demo') -File | Where-Object { $_.Extension -in '.json','.log' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'camera-network-demo') -File | Where-Object { $_.Extension -in '.json','.log' }) +
    @(Get-ChildItem -LiteralPath (Join-Path $Run 'camera-setup-demo') -File | Where-Object { $_.Extension -in '.json','.png','.log' })
$taskHashes = @($taskArtifacts | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($Run,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$taskHashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'artifact-hashes.json') -Encoding utf8
[ordered]@{result='Pass'; sourceCount=$taskCurrent.Count; sourceDrift=0; testTotal=($taskTests | ForEach-Object { $_.total } | Measure-Object -Sum).Sum; artifactHashCount=$taskHashes.Count; consumerSha256=($taskHashes | Where-Object { $_.path -like '*SharpInspect.SampleHost.dll' }).sha256 } | ConvertTo-Json
