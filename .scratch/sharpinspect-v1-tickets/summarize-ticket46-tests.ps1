param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskRun 'tests') -Filter '*.trx' -File)
if ($taskFiles.Count -ne 7) { throw 'Seven completed assembly TRX files are required.' }
$taskSummary = @()
$taskCases = @()
foreach ($taskFile in $taskFiles) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskCounters = $taskTrx.TestRun.ResultSummary.Counters
    $taskHash = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    $taskRows = @($taskTrx.TestRun.Results.UnitTestResult)
    if ($taskRows.Count -ne [int]$taskCounters.total) { throw 'TRX case count does not match its counters.' }
    $taskAssembly = [IO.Path]::GetFileName(@($taskTrx.TestRun.TestDefinitions.UnitTest)[0].storage)
    $taskSummary += [ordered]@{assembly=$taskAssembly;trx=$taskFile.Name;sha256=$taskHash;
        total=[int]$taskCounters.total;passed=[int]$taskCounters.passed;failed=[int]$taskCounters.failed;
        notExecuted=[int]$taskCounters.notExecuted;
        failures=@($taskRows | Where-Object outcome -CNE 'Passed' | ForEach-Object {
            [ordered]@{test=$_.testName;outcome=$_.outcome;message=$_.Output.ErrorInfo.Message} })}
    foreach ($taskCase in $taskRows) {
        if ($taskCase.testName -match '(V146_[A-Z]+\d+)') {
            $taskCases += [ordered]@{id=$Matches[1];test=$taskCase.testName;outcome=$taskCase.outcome;
                duration=$taskCase.duration;trx=$taskFile.Name;trxSha256=$taskHash}
        }
    }
}
if (@($taskSummary.assembly | Sort-Object -Unique).Count -ne 7) { throw 'Test assemblies are duplicated.' }
$taskSummary | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRun 'test-summary.json') -Encoding utf8
$taskCaseResult = if ($taskCases.Count -gt 0 -and @($taskCases | Where-Object outcome -CNE 'Passed').Count -eq 0) { 'Pass' } else { 'Fail' }
[ordered]@{result=$taskCaseResult;assemblyCaseCount=$taskCases.Count;cases=@($taskCases | Sort-Object id,test);
    notRun=@('Real PLC and camera qualification','Physical safety and workpiece disposition',
        'Production performance','Physical power-loss qualification','Independent manual recipe-response revocation')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRun 'v146-case-results.json') -Encoding utf8
[ordered]@{assemblyCount=$taskSummary.Count;total=($taskSummary.total | Measure-Object -Sum).Sum;
    passed=($taskSummary.passed | Measure-Object -Sum).Sum;failed=($taskSummary.failed | Measure-Object -Sum).Sum;
    v146Cases=$taskCases.Count;v146Result=$taskCaseResult} | ConvertTo-Json
