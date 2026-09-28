param([Parameter(Mandatory)][string]$OriginalRun,
    [Parameter(Mandatory)][string]$FollowupTrx,
    [Parameter(Mandatory)][string]$ReviewEvidence)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskOriginalFile = Join-Path $OriginalRun 'validation.json'
$taskOriginal = Get-Content -LiteralPath $taskOriginalFile -Raw | ConvertFrom-Json
if ($taskOriginal.result -cne 'Fail') { throw 'Preserved original failed validation is required.' }
$taskReview = Get-Content -LiteralPath $ReviewEvidence -Raw | ConvertFrom-Json
if ($taskReview.result -cne 'Pass') { throw 'Final follow-up review is not complete.' }
foreach ($taskPin in $taskReview.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskPin.path).Hash -cne $taskPin.sha256) { throw 'Review source drift.' }
}
if (@($taskReview.sourceHashes).Count -eq 0) { throw 'Review must bind actual source hashes.' }
$taskAllowed = @('src/SharpInspect.Runtime/StationRuntime.ProductionInspection.Transport.cs',
    'tests/SharpInspect.Runtime.Tests/AuthorizationPolicyTests.cs',
    'tests/SharpInspect.Runtime.Tests/RecipeChangeRuntimeRecoveryBoundaryTests.cs',
    'tests/SharpInspect.Runtime.Tests/ModbusQualificationTestServer.RecipeChange.cs',
    'tests/SharpInspect.Runtime.Tests/PlcRecipeChangeHandshakeTests.cs')
$taskReviewedPaths = @($taskReview.sourceHashes | ForEach-Object { $_.path.Replace('\','/') } | Sort-Object)
if (($taskReviewedPaths -join "`n") -cne (($taskAllowed | Sort-Object) -join "`n")) {
    throw 'Review must bind exactly the five changed source files.'
}
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskCurrent = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskBefore = @{}
foreach ($taskRow in $taskOriginal.sourceHashes) { $taskBefore[$taskRow.path.Replace('\','/')] = $taskRow.sha256 }
if ($taskCurrent.Count -ne $taskBefore.Count) { throw 'Source file set changed beyond this bounded continuation.' }
$taskChanges = @()
foreach ($taskRow in $taskCurrent) {
    $taskPath = $taskRow.path.Replace('\','/')
    if (-not $taskBefore.ContainsKey($taskPath)) { throw 'Unexpected new source file.' }
    if ($taskRow.sha256 -cne $taskBefore[$taskPath]) {
        if ($taskPath -notin $taskAllowed) { throw ('Unexpected source delta: ' + $taskPath) }
        $taskChanges += [ordered]@{path=$taskPath;before=$taskBefore[$taskPath];after=$taskRow.sha256}
    }
}
if ($taskChanges.Count -ne $taskAllowed.Count) { throw 'Expected exactly the five reviewed follow-up files.' }
$taskTests = @()
$taskOriginalCases = @()
$taskTrxFiles = @(Get-ChildItem -LiteralPath (Join-Path $OriginalRun 'tests') -Filter '*.trx' -File)
if ($taskTrxFiles.Count -ne 7) { throw 'Original seven-assembly run is incomplete.' }
foreach ($taskFile in $taskTrxFiles) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskRows = @($taskTrx.TestRun.Results.UnitTestResult)
    if ($taskRows.Count -ne [int]$taskTrx.TestRun.ResultSummary.Counters.total) { throw 'Original TRX incomplete.' }
    foreach ($taskRow in $taskRows) {
        if ($taskRow.outcome -cne 'Passed' -and $taskRow.testName -notmatch
            '(V146_|V106_C01_PermissionsAndSystemPrincipalsHaveStableBoundaries)') {
            throw ('Unresolved original regression: ' + $taskRow.testName)
        }
    }
    $taskOriginalCases += $taskRows
    $taskTests += [ordered]@{path=$taskFile.FullName;sha256=(Get-FileHash -LiteralPath $taskFile.FullName).Hash;
        total=$taskRows.Count;failed=@($taskRows | Where-Object outcome -CNE 'Passed').Count}
}
[xml]$taskFollowup = Get-Content -LiteralPath $FollowupTrx -Raw
$taskFollowupCases = @($taskFollowup.TestRun.Results.UnitTestResult)
if ($taskFollowupCases.Count -ne [int]$taskFollowup.TestRun.ResultSummary.Counters.total -or
    @($taskFollowupCases | Where-Object outcome -CNE 'Passed').Count) { throw 'Follow-up checks must all pass.' }
$taskRecipeCases = @($taskFollowupCases | Where-Object testName -Match 'V146_')
if ($taskRecipeCases.Count -ne 95 -or
    @($taskFollowupCases | Where-Object testName -Match 'V106_C01_PermissionsAndSystemPrincipalsHaveStableBoundaries').Count -ne 1) {
    throw 'All 95 final T46 instances and the repaired authorization boundary are required.'
}
$taskFollowupEvidencePath = [IO.Path]::ChangeExtension($FollowupTrx, '.evidence.json')
$taskFollowupEvidence = Get-Content -LiteralPath $taskFollowupEvidencePath -Raw | ConvertFrom-Json
if ($taskFollowupEvidence.result -cne 'Pass' -or
    $taskFollowupEvidence.trx.sha256 -cne (Get-FileHash -LiteralPath $FollowupTrx).Hash -or
    ($taskFollowupEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress) -cne
        ($taskCurrent | ConvertTo-Json -Depth 4 -Compress)) {
    throw 'Follow-up TRX does not bind the current tested source state.'
}
foreach ($taskBinary in $taskFollowupEvidence.binaries) {
    if ((Get-FileHash -LiteralPath $taskBinary.source).Hash -cne $taskBinary.sha256 -or
        (Get-FileHash -LiteralPath $taskBinary.archive).Hash -cne $taskBinary.sha256) {
        throw 'Follow-up tested binary or its archive changed.'
    }
}
if (@($taskFollowupEvidence.binaries).Count -ne 9) { throw 'Follow-up test and product binary binding is incomplete.' }
$taskOldE10 = 'SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests.V146_E10_PublishedUnacknowledgedResponseSurvivesRestartWithoutAutomaticRecovery'
$taskExpectedRecipeNames = @($taskOriginalCases | Where-Object testName -Match 'V146_' | ForEach-Object {
    if ($_.testName -ceq $taskOldE10) { $taskOldE10 + '(onlyDedicatedResponseRetained: False)' } else { $_.testName }
}) + @(($taskOldE10 + '(onlyDedicatedResponseRetained: True)'),
    'SharpInspect.Runtime.Tests.PlcRecipeChangeHandshakeTests.V146_H11_RetainedRuntimeResponseNeverClearsOrReplaysAtStartup(responseValid: True)',
    'SharpInspect.Runtime.Tests.PlcRecipeChangeHandshakeTests.V146_H11_RetainedRuntimeResponseNeverClearsOrReplaysAtStartup(responseValid: False)')
$taskExpectedRecipeNames = @($taskExpectedRecipeNames | Sort-Object -Unique)
$taskRecipeNames = @($taskRecipeCases.testName | Sort-Object -Unique)
if ($taskRecipeNames.Count -ne 95 -or ($taskRecipeNames -join "`n") -cne ($taskExpectedRecipeNames -join "`n")) {
    throw 'Follow-up case names do not match the original cases plus the three documented additions.'
}
$taskFinalNames = @(@($taskOriginalCases | Where-Object testName -NotMatch 'V146_' | Select-Object -ExpandProperty testName) +
    $taskRecipeNames | Sort-Object -Unique)
$taskEvidence = [ordered]@{kind='V1-46-follow-up-test-and-consumer-continuation';
    original=[ordered]@{path=$OriginalRun;result='Fail';validationSha256=(Get-FileHash -LiteralPath $taskOriginalFile).Hash;
        tests=$taskTests;total=$taskOriginalCases.Count};
    reviewedChanges=$taskChanges;review=[ordered]@{path=$ReviewEvidence;sha256=(Get-FileHash -LiteralPath $ReviewEvidence).Hash};
    followup=[ordered]@{path=$FollowupTrx;sha256=(Get-FileHash -LiteralPath $FollowupTrx).Hash;passed=$taskFollowupCases.Count;
        v146Passed=$taskRecipeCases.Count;sourceAndBinaryEvidence=$taskFollowupEvidencePath;
        sourceAndBinaryEvidenceSha256=(Get-FileHash -LiteralPath $taskFollowupEvidencePath).Hash;
        v146Names=$taskRecipeNames;legacyE10MappedToFalse=$taskOldE10};
    distinctCaseCount=$taskFinalNames.Count;
    limitation='Original full run remains Fail. Unaffected original tests are reused; all changed T46 behavior and the old authorization boundary are checked separately. No single-run full-suite pass is claimed.'}
$taskDriverRoot = Join-Path (Split-Path -Parent $OriginalRun) ('continuation-driver-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($taskDriverRoot)
$taskContext = Join-Path $taskDriverRoot 'continuation-context.json'
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskContext -Encoding utf8
$taskOriginalScript = Join-Path $taskRepo 'tools/Test-Development.ps1'
$taskScript = Get-Content -LiteralPath $taskOriginalScript -Raw
$taskTestCall = @'
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))
'@
$taskScript = $taskScript.Replace("`r`n", "`n")
$taskTestCall = $taskTestCall.Replace("`r`n", "`n")
if ($taskScript.Split($taskTestCall, [StringSplitOptions]::None).Count -ne 2) { throw 'Development test-call shape changed.' }
$taskQuotedContext = "'" + $taskContext.Replace("'", "''") + "'"
$taskReplacement = '    $taskEvidence.kind = ''V1-46-development-validation-composite''' + "`n" +
    '    $taskEvidence.continuation = Get-Content -LiteralPath ' + $taskQuotedContext + ' -Raw | ConvertFrom-Json' + "`n" +
    '    $taskEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskRun ''validation.json'') -Encoding utf8'
$taskScript = $taskScript.Replace($taskTestCall, $taskReplacement)
$taskTools = "('" + (Join-Path $taskRepo 'tools').Replace("'", "''") + "')"
$taskScript = $taskScript.Replace('$PSScriptRoot', $taskTools)
$taskDriver = Join-Path $taskDriverRoot 'Test-Development-Continuation.ps1'
[IO.File]::WriteAllText($taskDriver, $taskScript, [Text.UTF8Encoding]::new($false))
$taskParseErrors = $null
$taskParseTokens = $null
[void][Management.Automation.Language.Parser]::ParseFile($taskDriver, [ref]$taskParseTokens, [ref]$taskParseErrors)
if ($taskParseErrors.Count) { throw ($taskParseErrors | Out-String) }
[ordered]@{originalScriptSha256=(Get-FileHash -LiteralPath $taskOriginalScript).Hash;
    generatedScriptSha256=(Get-FileHash -LiteralPath $taskDriver).Hash;
    replaced='Solution test invocation is replaced with composite references; PSScriptRoot is rebound to the original tools directory because the driver is stored outside the repository. Build, packages and every consumer still execute.';
    originalToolsDirectory=(Join-Path $taskRepo 'tools')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDriverRoot 'driver-provenance.json') -Encoding utf8
& $taskDriver -Ticket 46 -ArtifactRoot (Split-Path -Parent (Split-Path -Parent $OriginalRun))
