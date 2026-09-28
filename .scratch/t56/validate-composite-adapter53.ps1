param([Parameter(Mandatory)][string]$Baseline,[Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
$taskPins=Get-Content -LiteralPath '.scratch/t56/baseline53/bindings.json' -Raw | ConvertFrom-Json
$taskBase=Get-Content -LiteralPath (Join-Path $Baseline 'validation.json') -Raw | ConvertFrom-Json
if($taskPins.baseline -cne $Baseline -or $taskBase.result -cne 'Fail' -or $taskBase.kind -cne 'V1-56-development-validation'){throw 'Completed failed baseline required.'}
$taskAllowedSource=@('src\SharpInspect.Wpf\ManagedFaultShutdown.cs','samples\SharpInspect.SampleHost\DiagnosticFatalProbe.cs','tools\Test-DiagnosticsConsumer.ps1','tests\SharpInspect.Runtime.Tests\ProductionStopCrashTests.cs','tests\SharpInspect.Runtime.Tests\PartIdentityAuditStorageTests.cs')
$taskSourceDelta=@()
foreach($taskSource in $taskBase.sourceHashes){
    $taskNow=(Get-FileHash -LiteralPath $taskSource.path).Hash
    if($taskNow -cne $taskSource.sha256){$taskSourceDelta += [ordered]@{path=$taskSource.path;before=$taskSource.sha256;after=$taskNow}}
}
if(@(Compare-Object @($taskSourceDelta.path | Sort-Object) @($taskAllowedSource | Sort-Object)).Count -ne 0){throw 'Source changes differ from exact five-file supplement scope.'}
$taskCurrentPaths=@(Get-ChildItem -LiteralPath src,tests,samples,tools -File -Recurse | Where-Object {$_.FullName -notmatch '\\(bin|obj|TestResults)\\'} | ForEach-Object {[IO.Path]::GetRelativePath((Get-Location).Path,$_.FullName)}) + @('Directory.Build.props','global.json','SharpInspect.NET.sln')
if(@(Compare-Object @($taskCurrentPaths | Sort-Object) @($taskBase.sourceHashes.path | Sort-Object)).Count){throw 'Source inventory changed.'}
$taskBinaryDelta=@(); $taskBaselineDiscovery=@{}; $taskFinalDiscovery=@{}
[void][IO.Directory]::CreateDirectory((Join-Path $Run 'discovery'))
foreach($taskSuite in $taskPins.suites){
    if((Get-FileHash -LiteralPath $taskSuite.discovery).Hash -cne $taskSuite.discoverySha256){throw 'Baseline discovery changed.'}
    foreach($taskDependency in $taskSuite.dependencies){
        $taskHash=(Get-FileHash -LiteralPath $taskDependency.path).Hash
        if($taskHash -cne $taskDependency.sha256){
            if([IO.Path]::GetFileName($taskDependency.path) -cnotin @('SharpInspect.Runtime.Tests.dll','SharpInspect.Wpf.Tests.dll','SharpInspect.Wpf.dll')){throw ('Untested dependency changed: '+$taskDependency.path)}
            $taskBinaryDelta += [ordered]@{path=$taskDependency.path;before=$taskDependency.sha256;after=$taskHash}
        }
    }
    $taskBefore=@(Get-Content -LiteralPath $taskSuite.discovery | Where-Object {$_ -cmatch '^    SharpInspect\.'} | ForEach-Object {$_.Substring(4)})
    $taskDiscovery=Join-Path $Run ('discovery/'+$taskSuite.project+'.log')
    & dotnet vstest $taskSuite.assembly /ListTests *> $taskDiscovery
    if($LASTEXITCODE -ne 0){throw 'Final discovery failed.'}
    $taskAfter=@(Get-Content -LiteralPath $taskDiscovery | Where-Object {$_ -cmatch '^    SharpInspect\.'} | ForEach-Object {$_.Substring(4)})
    if($taskBefore.Count -eq 0 -or $taskBefore.Count -ne $taskAfter.Count -or @(Compare-Object ($taskBefore | Sort-Object) ($taskAfter | Sort-Object)).Count){throw ('Final discovery differs: '+$taskSuite.project)}
    if(@($taskBefore | Group-Object | Where-Object Count -ne 1).Count){throw 'Ambiguous discovered case identities.'}
    $taskBaselineDiscovery[$taskSuite.project]=$taskBefore
    $taskFinalDiscovery[$taskSuite.project]=[ordered]@{path=$taskDiscovery;sha256=(Get-FileHash -LiteralPath $taskDiscovery).Hash;count=$taskAfter.Count;assembly=$taskSuite.assembly;assemblySha256=(Get-FileHash -LiteralPath $taskSuite.assembly).Hash;tfm=$taskSuite.tfm}
}
function Read-CompositeTrx([string]$Directory){
    if(-not (Test-Path -LiteralPath $Directory)){return @()}
    $taskRows=@()
    foreach($taskFile in @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.trx')){
        [xml]$taskXml=Get-Content -LiteralPath $taskFile.FullName -Raw
        $taskCounters=$taskXml.TestRun.ResultSummary.Counters
        $taskResults=@($taskXml.TestRun.Results.UnitTestResult)
        if($taskResults.Count -le 0 -or $taskResults.Count -ne [int]$taskCounters.total -or [int]$taskCounters.notExecuted -ne 0 -or [int]$taskCounters.executed -ne [int]$taskCounters.total){throw ('Incomplete TRX: '+$taskFile.FullName)}
        $taskDefinitions=@{}
        foreach($taskDefinition in $taskXml.TestRun.TestDefinitions.UnitTest){$taskDefinitions[$taskDefinition.id]=$taskDefinition}
        $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName).Hash
        foreach($taskResult in $taskResults){
            $taskDefinition=$taskDefinitions[$taskResult.testId]
            $taskAssembly=[IO.Path]::GetFileNameWithoutExtension($taskDefinition.storage)
            $taskMatch=@($taskPins.suites | Where-Object {('SharpInspect.'+$_.project+'.Tests') -ieq $taskAssembly})
            if($taskMatch.Count -ne 1){throw ('Unexpected test assembly: '+$taskAssembly)}
            $taskSuite=$taskMatch[0]
            $taskName=[string]$taskResult.testName
            $taskRows += [pscustomobject][ordered]@{key=$taskAssembly+'|'+$taskSuite.tfm+'|'+$taskName;project=$taskSuite.project;tfm=$taskSuite.tfm;testName=$taskName;outcome=[string]$taskResult.outcome;trx=$taskFile.FullName;trxSha256=$taskHash;testId=[string]$taskResult.testId;executionId=[string]$taskResult.executionId}
        }
    }
    return $taskRows
}
$taskOriginal=@(Read-CompositeTrx (Join-Path $Baseline 'tests'))
$taskCrashName='SharpInspect.Runtime.Tests.ProductionStopCrashTests.V145_D01_KilledHostCannotTurnAnAcceptedStopIntoNormalCompletionOnRestart'
$taskSupplementNames=@($taskCrashName,'SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests.V143_S09_IndependentRejectRechecksCentralChainWithoutAdvancing')
$taskOriginalFailures=@($taskOriginal | Where-Object outcome -cne 'Passed')
if($taskOriginalFailures.Count -ne 2 -or @($taskOriginalFailures | Where-Object outcome -cne 'Failed').Count -or
   @(Compare-Object ($taskOriginalFailures.testName | Sort-Object) ($taskSupplementNames | Sort-Object)).Count){throw 'Unexpected original failures; no selection or retry permitted.'}
$taskOriginalRuntime=@($taskOriginal | Where-Object project -ceq 'Runtime')
if($taskOriginalRuntime.Count -ne $taskBaselineDiscovery.Runtime.Count -or @(Compare-Object ($taskOriginalRuntime.testName | Sort-Object) ($taskBaselineDiscovery.Runtime | Sort-Object)).Count){throw 'Original Runtime suite incomplete.'}
$taskSupplement=@(Read-CompositeTrx (Join-Path $Run 'runtime-supplement-tests'))
if($taskSupplement.Count -ne 2 -or @($taskSupplement | Where-Object outcome -cne 'Passed').Count -or
   @(Compare-Object ($taskSupplement.testName | Sort-Object) ($taskSupplementNames | Sort-Object)).Count){throw 'Final fixture boundary results missing.'}
$taskSelected=@($taskOriginalRuntime | Where-Object testName -cnotin $taskSupplementNames) + $taskSupplement
foreach($taskSuite in @($taskPins.suites | Where-Object project -cne 'Runtime')){
    if($taskSuite.project -ceq 'Wpf'){$taskRows=@(Read-CompositeTrx (Join-Path $Run 'wpf-tests'))}
    else{
        $taskRows=@($taskOriginal | Where-Object project -ceq $taskSuite.project)
        if($taskRows.Count -eq 0){
            # A solution driver may stop after the failed Runtime project. Run every
            # undispatched suite once; this branch never replaces an observed failure.
            $taskResults=Join-Path $Run ('additional-tests/'+$taskSuite.project)
            if(Test-Path -LiteralPath $taskResults){throw 'Fresh additional test results required.'}
            [void][IO.Directory]::CreateDirectory($taskResults)
            & dotnet vstest $taskSuite.assembly /Logger:trx ('/ResultsDirectory:'+$taskResults) *> (Join-Path $taskResults 'tests.log')
            if($LASTEXITCODE -ne 0){throw ('Additional suite failed: '+$taskSuite.project)}
            $taskRows=@(Read-CompositeTrx $taskResults)
        }
    }
    if($taskRows.Count -ne $taskBaselineDiscovery[$taskSuite.project].Count -or
       @($taskRows | Where-Object outcome -cne 'Passed').Count -or
       @(Compare-Object ($taskRows.testName | Sort-Object) ($taskBaselineDiscovery[$taskSuite.project] | Sort-Object)).Count){throw ('Incomplete final suite: '+$taskSuite.project)}
    $taskSelected += $taskRows
}
if($taskSelected.Count -ne 3164 -or @($taskSelected | Where-Object outcome -cne 'Passed').Count -or
   @($taskSelected | Group-Object key | Where-Object Count -ne 1).Count -or @($taskSelected | Where-Object testName -cmatch 'V156_').Count -ne 37){throw 'Composite case inventory invalid.'}
$taskCoverage=[ordered]@{kind='V1-56-fixed-composite-test-coverage';result='Pass';originalFullResult='Fail';originalFailureCause='Unknown/Unproven';baseline=$Baseline;baselineManifestSha256=(Get-FileHash -LiteralPath (Join-Path $Baseline 'validation.json')).Hash;baselineBindingsSha256=(Get-FileHash -LiteralPath '.scratch/t56/baseline53/bindings.json').Hash;selectionRule='All unchanged baseline Runtime cases + final two exact fixture cases + full final WPF; other baseline suites retained or run once only if undispatched';sourceDelta=$taskSourceDelta;binaryDelta=$taskBinaryDelta;originalFailures=$taskOriginalFailures;caseCount=$taskSelected.Count;v156Cases=37;selectedCases=$taskSelected;finalDiscovery=$taskFinalDiscovery;fullOriginalCaseCount=$taskOriginal.Count;fullOriginalFailed=$taskOriginalFailures.Count;productionAcceptance='NotRun'}
$taskCoverage | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $Run 'coverage.json') -Encoding utf8
Write-Output ('Composite coverage: '+$taskSelected.Count+' complete identities; original full failure retained.')

