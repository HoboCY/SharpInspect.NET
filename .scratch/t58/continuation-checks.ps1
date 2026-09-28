$t58BaselineRun='F:\si58v16\ticket58\20260916-181045-441'
$t58BinaryProof='D:\projects\SharpInspect.NET\.scratch\t58\binary-bindings16.json'
function Assert-T58CandidateBinding {
    $baseline=Get-Content -LiteralPath (Join-Path $t58BaselineRun 'validation.json') -Raw | ConvertFrom-Json
    if($baseline.result -cne 'Fail' -or $baseline.kind -cne 'V1-58-development-validation' -or $baseline.startingHead -cne '8c7f95869c8f7e414332514135b1cd9e74e2527c') {throw 'Completed original full run required.'}
    $current=@(Get-TaskSourceHashes)
    if(($current | ConvertTo-Json -Depth 4 -Compress) -cne ($baseline.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) {throw 'Source changed since original full run.'}
    foreach($binary in (Get-Content -LiteralPath $t58BinaryProof -Raw | ConvertFrom-Json).binaries) {
        if((Get-FileHash -LiteralPath $binary.path).Hash -cne $binary.sha256) {throw 'Candidate binary changed before fixed recheck.'}
    }
}
function Use-T58BoundOriginalBuild {
    Assert-T58CandidateBinding
    Copy-Item -LiteralPath (Join-Path $t58BaselineRun 'build.log') -Destination (Join-Path $taskRun 'original-build.log')
    $taskEvidence.baselineFullRun=$t58BaselineRun
    $taskEvidence.baselineFullResult='Fail'
    $taskEvidence.baselineManifestSha256=(Get-FileHash -LiteralPath (Join-Path $t58BaselineRun 'validation.json')).Hash
    $taskEvidence.binaryBindingsSha256=(Get-FileHash -LiteralPath $t58BinaryProof).Hash
    $taskEvidence.validationMethod='Unchanged source and DLLs; original passing cases plus one mandatory recheck of each original failed case; all remaining driver checks executed.'
    $taskEvidence.continuationAdapterSha256=(Get-FileHash -LiteralPath 'D:\projects\SharpInspect.NET\.scratch\t58\Test-Development-continuation17.ps1').Hash
    $taskEvidence.continuationChecksSha256=(Get-FileHash -LiteralPath 'D:\projects\SharpInspect.NET\.scratch\t58\continuation-checks.ps1').Hash
}
function Read-T58Cases([string]$Directory) {
    foreach($file in Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File) {
        [xml]$xml=Get-Content -LiteralPath $file.FullName -Raw
        $rows=@($xml.TestRun.Results.UnitTestResult)
        if($rows.Count -ne [int]$xml.TestRun.ResultSummary.Counters.total -or [int]$xml.TestRun.ResultSummary.Counters.notExecuted -ne 0) {throw 'Incomplete original test suite.'}
        $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        foreach($row in $rows) {
            [pscustomobject][ordered]@{testName=$row.testName;testId=$row.testId;executionId=$row.executionId;outcome=$row.outcome;trx=$file.FullName;trxSha256=$hash}
        }
    }
}
function Invoke-T58FixedCoverage {
    Assert-T58CandidateBinding
    $original=@(Read-T58Cases (Join-Path $t58BaselineRun 'tests'))
    if($original.Count -ne 3279 -or @($original | Group-Object testName | Where-Object Count -ne 1).Count) {throw 'Original solution denominator or identities differ.'}
    $failed=@($original | Where-Object outcome -cne 'Passed')
    if($failed.Count -lt 1 -or $failed.Count -gt 10) {throw 'Original failed case set is not the bounded reviewed scope.'}
    # Exact reviewed failure identity; any additional failure requires diagnosis.
    $expectedFailures=@('SharpInspect.Runtime.Tests.ManualInspectionRuntimeTests.V147_R17_ReservedPlcRequestWinsTheOnceOnlyStartupCauseAcrossColdRestart')
    if((@($failed.testName | Sort-Object) -join '|') -cne (@($expectedFailures | Sort-Object) -join '|')) {throw 'Original failure set changed; diagnose before extending scope.'}
    $failed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRun 'original-failures.json')
    $recheckIndex=0
    foreach($failure in $failed | Sort-Object testName) {
        $recheckIndex++
        $filter='FullyQualifiedName='+$failure.testName
        Invoke-TaskDotnet ('required-recheck-'+$recheckIndex+'.log') @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj','-c','Release','--no-build','--no-restore','-m:1','--filter',$filter,'--logger',('trx;LogFileName=case-'+$recheckIndex+'.trx'),'--results-directory',(Join-Path $taskRun 'required-recheck-tests'))
    }
    Assert-T58CandidateBinding
    $recheck=@(Read-T58Cases (Join-Path $taskRun 'required-recheck-tests'))
    if(@($recheck | Where-Object outcome -cne 'Passed').Count) {throw 'Required unchanged-candidate recheck failed.'}
    $selected=@($original | Where-Object outcome -ceq 'Passed')
    foreach($failure in $failed) {
        $matching=@($recheck | Where-Object testName -ceq $failure.testName)
        if($matching.Count -ne 1) {throw 'Original failure has no unique required recheck.'}
        $selected += $matching[0]
    }
    if($selected.Count -ne $original.Count -or @($selected | Group-Object testName | Where-Object Count -ne 1).Count) {throw 'Fixed coverage lost or duplicated a case.'}
    $sourceEvidence=Join-Path $t58BaselineRun 'performance-runtime-evidence'
    $targetEvidence=Join-Path $taskRun 'performance-runtime-evidence'
    [void][IO.Directory]::CreateDirectory($targetEvidence)
    foreach($file in Get-ChildItem -LiteralPath $sourceEvidence -File) {
        $destination=Join-Path $targetEvidence $file.Name
        if(Test-Path -LiteralPath $destination) {throw 'Do not overwrite runtime capture evidence.'}
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        if((Get-FileHash -LiteralPath $destination).Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash) {throw 'Runtime evidence copy changed bytes.'}
    }
    [ordered]@{kind='V1-58-fixed-unchanged-candidate-coverage';result='PassWithRecordedDevelopmentFailures';baseline=$t58BaselineRun;
        baselineManifestSha256=(Get-FileHash -LiteralPath (Join-Path $t58BaselineRun 'validation.json')).Hash;
        binaryBindingsSha256=(Get-FileHash -LiteralPath $t58BinaryProof).Hash;originalFailures=$failed;
        selectionRule='Original passing cases are retained; each original failed identity has one required unchanged-DLL recheck. No performance baseline is replaced.';
        selectedCases=$selected;caseCount=$selected.Count;originalFailureCause='Unknown: original PLC arbitration returned RecipeActivationRuntimeBusy; no production gate or test assertion was changed';
        recheckPolicy='D:\projects\SharpInspect.NET\.scratch\t58\recheck-contract16.json'
    } | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $taskRun 'coverage.json')
}
