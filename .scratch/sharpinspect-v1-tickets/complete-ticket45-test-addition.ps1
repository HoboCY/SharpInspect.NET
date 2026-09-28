param([Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$AdditionalTrx)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskBaseFile = Join-Path $Run 'validation.json'
$taskBase = Get-Content -LiteralPath $taskBaseFile -Raw | ConvertFrom-Json
$taskBindings = Get-Content -LiteralPath (Join-Path $Run 'base-binary-bindings.json') -Raw | ConvertFrom-Json
if ($taskBase.result -cne 'Pass' -or (Get-FileHash -LiteralPath $taskBaseFile).Hash -cne $taskBindings.baseValidationSha256) {
    throw 'Original full validation changed or did not pass.'
}
[xml]$taskTrx = Get-Content -LiteralPath $AdditionalTrx -Raw
$taskResults = @($taskTrx.TestRun.Results.UnitTestResult)
if ($taskResults.Count -ne 2 -or @($taskResults | Where-Object outcome -CNE 'Passed').Count -ne 0 -or
    @($taskResults | Where-Object testName -NotMatch '\.V145_R0[23]_').Count -ne 0) { throw 'Both additional boundary cases must pass.' }
$taskSources = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskCurrent = @($taskSources | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskByPath = @{}
foreach ($taskSource in $taskCurrent) { $taskByPath[$taskSource.path] = $taskSource.sha256 }
foreach ($taskSource in $taskBase.sourceHashes) {
    if ($taskByPath[$taskSource.path] -cne $taskSource.sha256) { throw "Previously validated source changed: $($taskSource.path)" }
}
$taskAdded = @($taskCurrent | Where-Object { $_.path -notin @($taskBase.sourceHashes.path) })
if ($taskAdded.Count -ne 1 -or $taskAdded[0].path.Replace('\','/') -cne 'tests/SharpInspect.Runtime.Tests/ProductionStopRecoveryRetryLimitsTests.cs') {
    throw 'The final source difference must be exactly the two-case additional test file.'
}
foreach ($taskLibrary in $taskBindings.libraries) {
    if ((Get-FileHash -LiteralPath $taskLibrary.path).Hash -cne $taskLibrary.sha256) { throw "Validated library changed: $($taskLibrary.path)" }
}
$taskFinalAssembly = @($taskTrx.TestRun.TestDefinitions.UnitTest)[0].TestMethod.codeBase
$taskFinalSnapshot = Join-Path $Run 'additional-tested-assembly'
[void][IO.Directory]::CreateDirectory($taskFinalSnapshot)
Copy-Item -LiteralPath $taskFinalAssembly -Destination $taskFinalSnapshot
$taskAdditionalSummary = [ordered]@{result='Pass';total=2;passed=2;failed=0;notExecuted=0;
    trx=[IO.Path]::GetRelativePath($Run,$AdditionalTrx);trxSha256=(Get-FileHash -LiteralPath $AdditionalTrx).Hash;
    assemblySha256=(Get-FileHash -LiteralPath $taskFinalAssembly).Hash;cases=@($taskResults | ForEach-Object {
        [ordered]@{test=$_.testName;outcome=$_.outcome;duration=$_.duration}
    })}
$taskAdditionalSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Run 'additional-test-summary.json') -Encoding utf8
$taskFinal = [ordered]@{}
foreach ($taskProperty in $taskBase.PSObject.Properties) { $taskFinal[$taskProperty.Name] = $taskProperty.Value }
$taskFinal.kind = 'V1-45-composite-development-validation'
$taskFinal.sourceHashes = $taskCurrent
$taskFinal.baseValidation = [ordered]@{file='validation.json';sha256=$taskBindings.baseValidationSha256;result='Pass'}
$taskFinal.sourceAdditions = $taskAdded
$taskFinal.previouslyValidatedSourcesUnchanged = $true
$taskFinal.productionLibrariesUnchanged = $taskBindings.libraries
$taskFinal.baseFullSuitePassed = ($taskBindings.tests | Measure-Object -Property passed -Sum).Sum
$taskFinal.additionalBoundaryTests = $taskAdditionalSummary
$taskFinal.distinctPassedCases = $taskFinal.baseFullSuitePassed + 2
$taskFinal.scope = 'Original seven-assembly full suite and all package consumers, plus two newly added tests; all original source bytes and eight library binaries unchanged. Original full manifest and test binaries preserved.'
$taskFinal.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$taskFinal.result = 'Pass'
$taskFinal | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $Run 'validation-composite.json') -Encoding utf8
'Final composite evidence verifies original sources and libraries unchanged; two added cases pass.'
