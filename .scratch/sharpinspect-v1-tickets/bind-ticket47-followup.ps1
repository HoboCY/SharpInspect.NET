param([Parameter(Mandatory)][string]$Trx)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskOriginalRun = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket47\20260912-021453-387'
[xml]$taskXml = Get-Content -LiteralPath $Trx -Raw
$taskRows = @($taskXml.SelectNodes('//*[local-name()="UnitTestResult"]'))
if ($taskRows.Count -ne 87 -or @($taskRows | Where-Object outcome -CNE 'Passed').Count) { throw 'All 87 follow-up cases must pass.' }
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskCurrent = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskPins = @(Get-Content -LiteralPath (Join-Path $taskOriginalRun 'original-product-binary-hashes.json') -Raw | ConvertFrom-Json)
$taskArchive = Join-Path (Split-Path -Parent $Trx) 'focused45-binaries'
if (Test-Path -LiteralPath $taskArchive) { throw 'Do not overwrite an existing binary archive.' }
[void][IO.Directory]::CreateDirectory($taskArchive)
$taskBinaries = @()
foreach ($taskPin in $taskPins) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Product binary changed from complete Gate40.' }
    $taskDestination = Join-Path $taskArchive ([IO.Path]::GetFileName($taskPin.path))
    Copy-Item -LiteralPath $taskPin.path -Destination $taskDestination
    $taskBinaries += [ordered]@{source=$taskPin.path;archive=$taskDestination;sha256=$taskPin.sha256}
}
$taskTestDll = Join-Path $taskRepo 'tests/SharpInspect.Runtime.Tests/bin/Release/net6.0/SharpInspect.Runtime.Tests.dll'
$taskDestination = Join-Path $taskArchive 'SharpInspect.Runtime.Tests.dll'
Copy-Item -LiteralPath $taskTestDll -Destination $taskDestination
$taskBinaries += [ordered]@{source=$taskTestDll;archive=$taskDestination;sha256=(Get-FileHash -LiteralPath $taskTestDll).Hash}
foreach ($taskBinary in $taskBinaries) {
    if ((Get-FileHash -LiteralPath $taskBinary.archive).Hash -cne $taskBinary.sha256) { throw 'Binary archive changed.' }
}
[ordered]@{result='Pass';trx=[ordered]@{path=[IO.Path]::GetFullPath($Trx);sha256=(Get-FileHash -LiteralPath $Trx).Hash};
    sourceHashes=$taskCurrent;binaries=$taskBinaries;passed=$taskRows.Count;
    recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Trx, '.evidence.json')) -Encoding utf8
