param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$t41BindingPath = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket41\31-passing-suite-binding.json'
$t41Binding = Get-Content -LiteralPath $t41BindingPath -Raw | ConvertFrom-Json
$t41Old = Get-Content -LiteralPath (Join-Path $t41Binding.originalRun 'validation.json') -Raw | ConvertFrom-Json
$t41New = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
$t41OldMap = @{}
foreach ($t41Source in $t41Old.sourceHashes) { $t41OldMap[$t41Source.path] = $t41Source.sha256 }
$t41Added = @()
$t41ValidationOnlyChanges = @()
foreach ($t41Source in $t41New.sourceHashes) {
    if ($t41OldMap.ContainsKey($t41Source.path)) {
        if ($t41OldMap[$t41Source.path] -cne $t41Source.sha256) {
            # The failed original run never reached the new consumer script. It is
            # not compiled into any reused suite and will execute in this new gate.
            if ($t41Source.path -cne 'tools\Test-PlcCommunicationConsumer.ps1') {
                throw "Reused suite source changed: $($t41Source.path)"
            }
            $t41ValidationOnlyChanges += [ordered]@{path=$t41Source.path;oldSha256=$t41OldMap[$t41Source.path];newSha256=$t41Source.sha256}
        }
        $t41OldMap.Remove($t41Source.path)
    } else { $t41Added += $t41Source.path }
}
if ($t41OldMap.Count -ne 0 -or $t41Added.Count -ne 1 -or
    $t41Added[0] -cne 'tests\SharpInspect.Wpf.Tests\AssemblyInfo.cs') { throw 'Only the WPF test scheduling file may differ from the original run.' }
$t41Proof = @()
[void][IO.Directory]::CreateDirectory((Join-Path $Run 'tests'))
foreach ($t41Suite in $t41Binding.suites) {
    if ((Get-FileHash -LiteralPath $t41Suite.trx).Hash -cne $t41Suite.trxSha256) { throw 'Original TRX changed.' }
    [xml]$t41Trx = Get-Content -LiteralPath $t41Suite.trx -Raw
    $t41Counters = $t41Trx.TestRun.ResultSummary.Counters
    if ([int]$t41Counters.failed -ne 0 -or [int]$t41Counters.notExecuted -ne 0 -or
        [int]$t41Counters.total -ne [int]$t41Counters.passed) { throw 'Cannot reuse a failed or skipped suite.' }
    foreach ($t41Binary in $t41Suite.binaryHashes) {
        if ((Get-FileHash -LiteralPath $t41Binary.path).Hash -cne $t41Binary.sha256) {
            throw "Reused suite binary changed: $($t41Binary.path)"
        }
    }
    Copy-Item -LiteralPath $t41Suite.trx -Destination (Join-Path $Run 'tests')
    $t41Proof += [ordered]@{assembly=$t41Suite.assembly;total=$t41Suite.total;originalTrx=$t41Suite.trx;
        trxSha256=$t41Suite.trxSha256;verifiedBinaryCount=$t41Suite.binaryHashes.Count;sourceAndBinaryDrift=0}
}
if ($t41Proof.Count -ne 6) { throw 'Expected six passing unchanged suites.' }
[ordered]@{result='Pass';method='Reuse six passing unchanged suites; rerun all WPF tests after scheduling fix';
    originalRun=$t41Binding.originalRun;bindingSha256=(Get-FileHash -LiteralPath $t41BindingPath).Hash;
    onlySourceAddition=$t41Added[0];validationOnlyChanges=$t41ValidationOnlyChanges;suites=$t41Proof} | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path $Run 'reused-suite-evidence.json') -Encoding utf8
Write-Output 'REUSED_SUITE_BINDING_PASS: six suites, source and binary drift zero'
