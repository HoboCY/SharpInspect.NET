param([Parameter(Mandatory)][string]$OriginalRun,
    [Parameter(Mandatory)][string]$FollowupTrx,
    [Parameter(Mandatory)][string]$ReviewEvidence)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskOriginalFile = Join-Path $OriginalRun 'validation.json'
$taskOriginal = Get-Content -LiteralPath $taskOriginalFile -Raw | ConvertFrom-Json
if ($taskOriginal.kind -cne 'V1-47-development-validation' -or $taskOriginal.result -cne 'Fail') {
    throw 'The completed original full-run failure must remain preserved.'
}
$taskReview = Get-Content -LiteralPath $ReviewEvidence -Raw | ConvertFrom-Json
if ($taskReview.kind -cne 'V1-47-independent-review-disposition' -or $taskReview.result -cne 'Pass' -or
    [string]::IsNullOrWhiteSpace($taskReview.independentReviewer) -or [string]::IsNullOrWhiteSpace($taskReview.scope) -or
    @($taskReview.independentReports).Count -lt 2 -or @($taskReview.sourceHashes).Count -eq 0 -or
    [IO.Path]::GetFullPath($ReviewEvidence) -ieq [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($FollowupTrx), '.evidence.json')) {
    throw 'A typed independent review with a named reviewer and report provenance is required; test evidence is not a review.'
}
foreach ($taskReport in $taskReview.independentReports) {
    if ([IO.Path]::GetExtension($taskReport.path) -cne '.md' -or
        (Get-FileHash -LiteralPath $taskReport.path).Hash -cne $taskReport.sha256) { throw 'Independent report changed.' }
}
foreach ($taskPin in $taskReview.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Reviewed source changed.' }
}
$taskAllowed = @('tests/SharpInspect.Runtime.Tests/ProductionArmCauseArbitrationRuntimeTests.cs',
    'tools/Test-ProductionArmingConsumer.ps1')
$taskConsumerRepair = $taskReview.consumerScriptRepair
if ($taskConsumerRepair.path -cne $taskAllowed[1] -or $taskConsumerRepair.result -cne 'Pass' -or
    (Get-FileHash -LiteralPath $taskConsumerRepair.failedValidation.path).Hash -cne $taskConsumerRepair.failedValidation.sha256 -or
    (Get-Content -LiteralPath $taskConsumerRepair.failedValidation.path -Raw | ConvertFrom-Json).result -cne 'Fail' -or
    (Get-FileHash -LiteralPath $taskConsumerRepair.fixedConsumerEvidence.path).Hash -cne $taskConsumerRepair.fixedConsumerEvidence.sha256 -or
    (Get-Content -LiteralPath $taskConsumerRepair.fixedConsumerEvidence.path -Raw | ConvertFrom-Json).result -cne 'Pass' -or
    (Get-Content -LiteralPath $taskConsumerRepair.fixedConsumerEvidence.path -Raw | ConvertFrom-Json).id -cne 'V147_N01') {
    throw 'Preserved consumer compilation failure and actual fixed package-consumer pass required.'
}
$taskOldConsumerPath = Join-Path (Join-Path $OriginalRun 'source-before-followup') $taskConsumerRepair.path
$taskOldConsumer = (Get-Content -LiteralPath $taskOldConsumerPath -Raw).Replace("`r`n", "`n")
$taskNewConsumer = (Get-Content -LiteralPath $taskConsumerRepair.path -Raw).Replace("`r`n", "`n")
if ($taskNewConsumer -cne $taskOldConsumer.Replace("using SharpInspect.Runtime.Production;`n",
    "using SharpInspect.Runtime.Production;`nusing SharpInspect.Runtime.Qualification;`n")) {
    throw 'The only consumer-script repair permitted is its missing Qualification namespace import.'
}
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskCurrent = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskBefore = @{}
foreach ($taskSource in $taskOriginal.sourceHashes) { $taskBefore[$taskSource.path.Replace('\','/')] = $taskSource.sha256 }
if ($taskCurrent.Count -ne $taskBefore.Count) { throw 'Source path set changed.' }
$taskChanges = @()
foreach ($taskSource in $taskCurrent) {
    $taskPath = $taskSource.path.Replace('\','/')
    if (-not $taskBefore.ContainsKey($taskPath)) { throw 'Unexpected new source file.' }
    if ($taskSource.sha256 -cne $taskBefore[$taskPath]) {
        if ($taskPath -cnotin $taskAllowed) { throw "Unexpected source delta: $taskPath" }
        $taskChanges += [ordered]@{path=$taskPath;before=$taskBefore[$taskPath];after=$taskSource.sha256}
    }
}
if ($taskChanges.Count -ne 2) { throw 'Only the reviewed R17 correction and consumer namespace import are allowed.' }
$taskRootBuildFiles = @(Get-ChildItem -LiteralPath $taskRepo -File | Where-Object {
    $_.Extension -in '.props','.targets' -or $_.Name -in 'NuGet.Config','global.json'
})
foreach ($taskFile in $taskRootBuildFiles) {
    if ($taskBefore.ContainsKey($taskFile.Name)) { continue }
    git ls-files --error-unmatch -- $taskFile.Name *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Uncaptured new root build metadata.' }
    git diff --quiet HEAD -- $taskFile.Name
    if ($LASTEXITCODE -ne 0) { throw 'Uncaptured changed root build metadata.' }
}
$taskSummaryPath = Join-Path $OriginalRun 'completed-full-run-test-summary.json'
if ((Get-FileHash -LiteralPath $taskSummaryPath).Hash -cne 'CBC2448C9BAE2DE413CF391B7175A22619E11283C77FE57B02CB2D4F5A8D45F9') {
    throw 'The recorded Gate40 completion summary changed.'
}
$taskCompletedPins = @(Get-Content -LiteralPath $taskSummaryPath -Raw | ConvertFrom-Json)
$taskProductPins = @(Get-Content -LiteralPath (Join-Path $OriginalRun 'original-product-binary-hashes.json') -Raw | ConvertFrom-Json)
if ($taskProductPins.Count -ne 8) { throw 'Eight original product binaries must be pinned.' }
foreach ($taskPin in $taskProductPins) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'A product binary changed; this continuation is not permitted.' }
}
$taskTrxFiles = @(Get-ChildItem -LiteralPath (Join-Path $OriginalRun 'tests') -Filter '*.trx' -File)
if ($taskTrxFiles.Count -ne 7) { throw 'Original seven-assembly execution is incomplete.' }
$taskOriginalRows = @()
$taskTrxPins = @()
foreach ($taskFile in $taskTrxFiles) {
    $taskCompletionPin = @($taskCompletedPins | Where-Object path -CEQ $taskFile.FullName)
    if ($taskCompletionPin.Count -ne 1 -or
        (Get-FileHash -LiteralPath $taskFile.FullName).Hash -cne $taskCompletionPin[0].sha256) { throw 'Gate40 TRX changed after completion.' }
    [xml]$taskXml = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskRows = @($taskXml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($taskRows.Count -ne [int]$taskXml.SelectSingleNode('//*[local-name()="Counters"]').total) { throw 'Original TRX incomplete.' }
    foreach ($taskRow in $taskRows) {
        if ($taskRow.outcome -cne 'Passed' -and $taskRow.testName -notmatch '(V147_R17_|V143_R02_)') {
            throw "Unresolved original failure: $($taskRow.testName)"
        }
    }
    $taskOriginalRows += $taskRows
    $taskTrxPins += [ordered]@{path=$taskFile.FullName;sha256=(Get-FileHash -LiteralPath $taskFile.FullName).Hash;
        total=$taskRows.Count;failed=@($taskRows | Where-Object outcome -CNE 'Passed').Count}
}
[xml]$taskFollowup = Get-Content -LiteralPath $FollowupTrx -Raw
$taskFollowupRows = @($taskFollowup.SelectNodes('//*[local-name()="UnitTestResult"]'))
if ($taskFollowupRows.Count -ne [int]$taskFollowup.SelectSingleNode('//*[local-name()="Counters"]').total -or
    @($taskFollowupRows | Where-Object outcome -CNE 'Passed').Count) { throw 'Follow-up must be complete and all passed.' }
$taskExpected = @($taskOriginalRows | Where-Object testName -Match '(V147_|V143_R02_)' | Select-Object -ExpandProperty testName | Sort-Object)
$taskActual = @($taskFollowupRows.testName | Sort-Object)
if ($taskActual.Count -ne 87 -or ($taskExpected -join "`n") -cne ($taskActual -join "`n")) {
    throw 'Exactly all 84 T47 cases and three existing V143_R02 cases must pass.'
}
$taskFollowupBindingPath = [IO.Path]::ChangeExtension($FollowupTrx, '.evidence.json')
$taskFollowupBinding = Get-Content -LiteralPath $taskFollowupBindingPath -Raw | ConvertFrom-Json
if ($taskFollowupBinding.result -cne 'Pass' -or $taskFollowupBinding.trx.sha256 -cne (Get-FileHash -LiteralPath $FollowupTrx).Hash -or
    @($taskFollowupBinding.sourceHashes).Count -ne $taskCurrent.Count) {
    throw 'Follow-up source and binary binding is invalid.'
}
$taskFollowupSources = @{}
foreach ($taskPin in $taskFollowupBinding.sourceHashes) { $taskFollowupSources[$taskPin.path.Replace('\','/')] = $taskPin.sha256 }
foreach ($taskPin in $taskCurrent) {
    $taskPath = $taskPin.path.Replace('\','/')
    if (-not $taskFollowupSources.ContainsKey($taskPath)) { throw 'Follow-up source path set changed.' }
    if ($taskPin.sha256 -cne $taskFollowupSources[$taskPath] -and
        ($taskPath -cne $taskConsumerRepair.path -or $taskPin.sha256 -cne $taskConsumerRepair.after -or
            $taskFollowupSources[$taskPath] -cne $taskConsumerRepair.before)) { throw 'Unvalidated follow-up source change.' }
}
foreach ($taskBinary in $taskFollowupBinding.binaries) {
    if ((Get-FileHash -LiteralPath $taskBinary.source).Hash -cne $taskBinary.sha256 -or
        (Get-FileHash -LiteralPath $taskBinary.archive).Hash -cne $taskBinary.sha256) { throw 'Follow-up binary binding changed.' }
}
if (@($taskFollowupBinding.binaries).Count -ne 9) { throw 'Eight product DLLs and the Runtime test assembly are required.' }
$taskFinal = @{}
foreach ($taskRow in $taskOriginalRows) { $taskFinal[$taskRow.testName] = $taskRow.outcome }
foreach ($taskRow in $taskFollowupRows) { $taskFinal[$taskRow.testName] = $taskRow.outcome }
if ($taskFinal.Count -ne $taskOriginalRows.Count -or @($taskFinal.Values | Where-Object { $_ -cne 'Passed' }).Count) {
    throw 'Combined distinct cases are incomplete.'
}
$taskContext = [ordered]@{kind='V1-47-bounded-test-and-consumer-continuation';
    consumerScriptRepair=$taskConsumerRepair;
    completedOriginalSummary=[ordered]@{path=$taskSummaryPath;sha256=(Get-FileHash -LiteralPath $taskSummaryPath).Hash};
    original=[ordered]@{path=$OriginalRun;result='Fail';validationSha256=(Get-FileHash -LiteralPath $taskOriginalFile).Hash;
        tests=$taskTrxPins;total=$taskOriginalRows.Count;failed=@($taskOriginalRows | Where-Object outcome -CNE 'Passed').Count};
    reviewedChanges=$taskChanges;productBinaryPins=$taskProductPins;
    review=[ordered]@{path=$ReviewEvidence;sha256=(Get-FileHash -LiteralPath $ReviewEvidence).Hash};
    followup=[ordered]@{path=$FollowupTrx;sha256=(Get-FileHash -LiteralPath $FollowupTrx).Hash;passed=$taskFollowupRows.Count;
        sourceAndBinaryEvidence=$taskFollowupBindingPath;sourceAndBinaryEvidenceSha256=(Get-FileHash -LiteralPath $taskFollowupBindingPath).Hash};
    distinctCaseCount=$taskFinal.Count;fullSuiteSingleRunPass=$false;
    limitation='Original full run remains Fail. R17 test assertions changed, and the new package consumer gained its missing Qualification namespace import after a separately preserved consumer-gate failure. All eight product binaries are identical. All 84 T47 cases and three V143_R02 cases passed separately; that test binding predates only the consumer-script import, which is checked by actual package-consumer execution. Unaffected complete original results are reused. No single-run full-suite pass is claimed.'}
$taskDriverRoot = Join-Path (Split-Path -Parent $OriginalRun) ('continuation-driver-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($taskDriverRoot)
$taskContextPath = Join-Path $taskDriverRoot 'continuation-context.json'
$taskContext | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $taskContextPath -Encoding utf8
$taskOriginalScript = Join-Path $taskRepo 'tools/Test-Development.ps1'
$taskScript = (Get-Content -LiteralPath $taskOriginalScript -Raw).Replace("`r`n", "`n")
$taskTestCall = @'
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))
'@
$taskTestCall = $taskTestCall.Replace("`r`n", "`n")
if ($taskScript.Split($taskTestCall, [StringSplitOptions]::None).Count -ne 2) { throw 'Canonical test-call shape changed.' }
$taskQuotedContext = "'" + $taskContextPath.Replace("'", "''") + "'"
$taskReplacement = '    $taskEvidence.kind = ''V1-47-development-validation-composite''' + "`n" +
    '    $taskEvidence.continuation = Get-Content -LiteralPath ' + $taskQuotedContext + ' -Raw | ConvertFrom-Json' + "`n" +
    '    $taskEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskRun ''validation.json'') -Encoding utf8'
$taskScript = $taskScript.Replace($taskTestCall, $taskReplacement)
$taskScript = $taskScript.Replace('$PSScriptRoot', "('" + (Join-Path $taskRepo 'tools').Replace("'", "''") + "')")
$taskDriver = Join-Path $taskDriverRoot 'Test-Development-Continuation.ps1'
[IO.File]::WriteAllText($taskDriver, $taskScript, [Text.UTF8Encoding]::new($false))
$taskParseErrors = $null
$taskParseTokens = $null
[void][Management.Automation.Language.Parser]::ParseFile($taskDriver, [ref]$taskParseTokens, [ref]$taskParseErrors)
if ($taskParseErrors.Count) { throw ($taskParseErrors | Out-String) }
[ordered]@{originalScriptSha256=(Get-FileHash -LiteralPath $taskOriginalScript).Hash;
    generatedScriptSha256=(Get-FileHash -LiteralPath $taskDriver).Hash;
    replaced='Only the full solution test invocation is replaced by verified composite evidence. Build, eight packages and every consumer still execute.';
    originalToolsDirectory=(Join-Path $taskRepo 'tools')} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $taskDriverRoot 'driver-provenance.json') -Encoding utf8
& $taskDriver -Ticket 47 -ArtifactRoot (Split-Path -Parent (Split-Path -Parent $OriginalRun))
