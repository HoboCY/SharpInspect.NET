param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskValidation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Original full gate must finish and pass before adding tests.' }
$taskSnapshot = Join-Path $Run 'base-tested-assemblies'
if (Test-Path -LiteralPath $taskSnapshot) { throw 'Base binary snapshot already exists; do not overwrite it.' }
[void][IO.Directory]::CreateDirectory($taskSnapshot)
$taskTests = @()
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $Run 'tests') -Filter '*.trx' -File) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskCounters = $taskTrx.TestRun.ResultSummary.Counters
    if ([int]$taskCounters.failed -ne 0 -or [int]$taskCounters.notExecuted -ne 0 -or
        [int]$taskCounters.passed -ne [int]$taskCounters.total) { throw 'Base suite did not pass completely.' }
    $taskAssembly = @($taskTrx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase
    Copy-Item -LiteralPath $taskAssembly -Destination $taskSnapshot
    $taskTests += [ordered]@{assembly=[IO.Path]::GetFileName($taskAssembly);sha256=(Get-FileHash -LiteralPath $taskAssembly).Hash;
        trx=$taskFile.Name;trxSha256=(Get-FileHash -LiteralPath $taskFile.FullName).Hash;passed=[int]$taskCounters.passed}
}
if ($taskTests.Count -ne 7) { throw 'Expected seven base test assemblies.' }
$taskLibraries = @()
foreach ($taskName in 'Abstractions','Runtime','Wpf','OpenCvSharp','Cameras.Virtual','Cameras.Hikrobot','Cameras.Conformance','Calibration.OpenCvSharp') {
    $taskFramework = if ($taskName -eq 'Wpf') { 'net6.0-windows' } else { 'net6.0' }
    $taskPath = "src/SharpInspect.$taskName/bin/Release/$taskFramework/SharpInspect.$taskName.dll"
    $taskLibraries += [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash}
}
[ordered]@{baseValidationSha256=(Get-FileHash -LiteralPath (Join-Path $Run 'validation.json')).Hash;
    tests=$taskTests;libraries=$taskLibraries;capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Run 'base-binary-bindings.json') -Encoding utf8
$taskNewTest = 'tests/SharpInspect.Runtime.Tests/ProductionStopRecoveryRetryLimitsTests.cs'
if (Test-Path -LiteralPath $taskNewTest) { throw 'Additional test source already exists.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ProductionStopRecoveryRetryLimitsTests.cs') -Destination $taskNewTest
$taskFiles = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-staged-files.txt')) + $taskNewTest
$taskFiles | Sort-Object -Unique | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-staged-files.txt') -Encoding utf8
'Original full-suite binaries preserved; sole additional test source installed.'
