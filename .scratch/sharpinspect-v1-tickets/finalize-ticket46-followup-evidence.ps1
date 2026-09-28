$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskRoot = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46'
$taskTrxPath = Join-Path $taskRoot 'integration-20260911/post-full-followup-02.trx'
$taskPrevious = Get-Content -LiteralPath (Join-Path $taskRoot 'integration-20260911/post-full-followup-01.evidence.json') -Raw | ConvertFrom-Json
[xml]$taskTrx = Get-Content -LiteralPath $taskTrxPath -Raw
$taskCases = @($taskTrx.TestRun.Results.UnitTestResult)
if ($taskCases.Count -ne 104 -or @($taskCases | Where-Object outcome -CNE 'Passed').Count -ne 0 -or
    @($taskCases | Where-Object testName -Match 'V146_').Count -ne 95) { throw 'Follow-up is not complete.' }
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskSource = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if ($taskSource.Count -ne 1037) { throw 'Unexpected source count.' }
$taskArchive = Join-Path $taskRoot 'post-full-followup-02-binaries'
if (Test-Path -LiteralPath $taskArchive) { throw 'Archive already exists; do not overwrite.' }
[void][IO.Directory]::CreateDirectory($taskArchive)
$taskBinaries = @($taskPrevious.binaries | ForEach-Object {
    $taskDestination = Join-Path $taskArchive ([IO.Path]::GetFileName($_.source))
    $taskHash = (Get-FileHash -LiteralPath $_.source).Hash
    Copy-Item -LiteralPath $_.source -Destination $taskDestination
    if ((Get-FileHash -LiteralPath $taskDestination).Hash -cne $taskHash) { throw 'Archive copy differs.' }
    [ordered]@{source=$_.source;archive=$taskDestination;sha256=$taskHash}
})
[ordered]@{kind='V1-46-follow-up-source-and-binary-binding';result='Pass';capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    head=(& git rev-parse HEAD);trx=[ordered]@{path=$taskTrxPath;sha256=(Get-FileHash -LiteralPath $taskTrxPath).Hash;passed=104;v146Passed=95};
    sourceHashes=$taskSource;binaries=$taskBinaries;
    originalFullRun=[ordered]@{path=(Join-Path $taskRoot '20260911-223647-620/validation.json');result='Fail'};
    limitation='104 checks run on the final follow-up state. Unaffected results from the preserved original full run are reused; no single-run full-suite pass is claimed.'} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($taskTrxPath,'.evidence.json')) -Encoding utf8
$taskDelta = @('src/SharpInspect.Runtime/StationRuntime.ProductionInspection.Transport.cs',
    'tests/SharpInspect.Runtime.Tests/AuthorizationPolicyTests.cs',
    'tests/SharpInspect.Runtime.Tests/RecipeChangeRuntimeRecoveryBoundaryTests.cs',
    'tests/SharpInspect.Runtime.Tests/ModbusQualificationTestServer.RecipeChange.cs',
    'tests/SharpInspect.Runtime.Tests/PlcRecipeChangeHandshakeTests.cs')
$taskReviewPaths = @('.scratch/sharpinspect-v1-tickets/ticket46-complete-product-review-01.md',
    '.scratch/sharpinspect-v1-tickets/ticket46-final-incremental-review.md',
    '.scratch/sharpinspect-v1-tickets/ticket46-final-startup-review.md')
[ordered]@{kind='V1-46-final-follow-up-review-disposition';result='Pass';
    sourceHashes=@($taskDelta | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash} });
    independentReviews=@($taskReviewPaths | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash} });
    latestJob='5aa5dc3731a845a1b37bb1d3d6c3f702';p1p2Findings=0;
    disposition=@('Initial synchronization failure is intentionally fail-closed for RecipeChange profiles because clean state is unproved; normal shutdown and old profiles retain their guards.',
        'The dedicated response-read counter now counts only runtime address 600. The final 104 checks pass.',
        'Normal Dispose during T46 initial synchronization is not directly tested; code-reviewed cancellation guards only. V141_R06 is a different path.',
        'The continuation driver requires the exact five changed paths, all 95 expected case names, and current source plus nine binary hashes bound to the TRX.',
        'Driver provenance explicitly records the original tools directory and PSScriptRoot rebind.');
    postReviewAdjustments=@('Primary agent narrowed and renamed the test-only response read counter in two files; run 02 passes. Product transport bytes are unchanged from the independent review.',
        'Primary agent strengthened the scratch continuation provenance checks and parenthesized the added E10 name in the PowerShell array. These scratch edits were parsed and inspected locally, not independently re-reviewed.');
    followupEvidence=[IO.Path]::ChangeExtension($taskTrxPath,'.evidence.json')} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath '.scratch/sharpinspect-v1-tickets/ticket46-final-review-evidence.json' -Encoding utf8
Write-Output ('Follow-up source/binary evidence and review disposition written: 1037 sources, 9 binaries, 104 passed / 95 V146.')
