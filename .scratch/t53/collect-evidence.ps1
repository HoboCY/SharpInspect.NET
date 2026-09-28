param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
$taskRun=[IO.Path]::GetFullPath($Run)
$taskValidationPath=Join-Path $taskRun 'validation.json'
$taskValidation=Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
if($taskValidation.kind -cne 'V1-53-development-validation' -or $taskValidation.result -cne 'Pass') {
    throw 'A passing completed T53 validation is required; interrupted and failed runs are not accepted.'
}
foreach($taskSource in $taskValidation.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
$taskTrx=@(Get-ChildItem -LiteralPath $taskRun -Recurse -File -Filter '*.trx')
$taskCases=@()
$taskSuites=@()
foreach($taskFile in $taskTrx) {
    [xml]$taskXml=Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskCounters=$taskXml.TestRun.ResultSummary.Counters
    $taskResults=@($taskXml.TestRun.Results.UnitTestResult)
    if([int]$taskCounters.total -le 0 -or [int]$taskCounters.total -ne [int]$taskCounters.passed -or
        [int]$taskCounters.notExecuted -ne 0 -or $taskResults.Count -ne [int]$taskCounters.total -or
        @($taskResults | Where-Object outcome -cne 'Passed').Count) { throw ('TRX incomplete: '+$taskFile.FullName) }
    $taskRelative=[IO.Path]::GetRelativePath($taskRun,$taskFile.FullName)
    $taskSuites += [ordered]@{path=$taskRelative;sha256=(Get-FileHash $taskFile.FullName).Hash;total=$taskResults.Count}
    if($taskRelative -match '^tests[\\/]') { $taskCases += $taskResults }
}
$taskNew=@($taskCases | Where-Object testName -Match 'V153_')
if($taskNew.Count -ne 56) { throw ('T53 case count differs: '+$taskNew.Count) }
$taskMigrations=@()
$taskMigrationPaths=@('store-migration-consumer.json','image-evidence-migration-consumer.json',
    'image-finalization-migration-consumer.json') + @(32..36 | ForEach-Object {'outbox-migration-'+$_+'-consumer.json'})
$taskRuntimeHash=(Get-FileHash -LiteralPath src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll).Hash
foreach($taskName in $taskMigrationPaths) {
    $taskPath=Join-Path $taskRun $taskName
    $taskReport=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskReport.result -cne 'Pass' -or $taskReport.cases.Count -ne 14 -or
        (@($taskReport.cases.phase | Sort-Object -Unique) -join ',') -cne (@(1..14) -join ',') -or
        @($taskReport.cases | Where-Object result -cne 'Pass').Count -or
        $taskReport.current.runtimeHash -cne $taskRuntimeHash -or
        (Get-FileHash -LiteralPath $taskReport.current.runtime).Hash -cne $taskRuntimeHash) {
        throw ('Migration evidence incomplete: '+$taskName)
    }
    if($taskName -match '^outbox-migration') {
        if(-not $taskReport.fullPhaseCoverage -or @($taskReport.cases | Where-Object {
            -not $_.oldDatabaseAndWalUnchanged -or -not $_.oldWriterAuditUnchanged -or
            $_.currentPostWriteAuditSequence -le 0 -or $_.currentVerifiedThroughSequence -lt $_.currentPostWriteAuditSequence -or
            $_.seedPostWriteAuditSequence -le 0 -or $_.seedVerifiedThroughSequence -lt $_.seedPostWriteAuditSequence
        }).Count) { throw ('Outbox migration watermark or immutability evidence incomplete: '+$taskName) }
    }
    $taskMigrations += [ordered]@{path=$taskName;sha256=(Get-FileHash $taskPath).Hash;phases=14;
        oldRuntimeHash=$taskReport.old.runtimeHash;currentRuntimeHash=$taskRuntimeHash}
}
$taskPackages=@(Get-ChildItem (Join-Path $taskRun 'packages') -File -Filter '*.nupkg')
if($taskPackages.Count -ne 8) { throw 'Eight packages are required.' }
$taskAbi=Get-Content -LiteralPath D:/s53abi/abi-package-binding.json -Raw | ConvertFrom-Json
if($taskAbi.result -cne 'Pass' -or -not $taskAbi.consumerUnchanged -or $taskAbi.libraries.Count -ne 4) { throw 'ABI proof missing.' }
foreach($taskLibrary in $taskAbi.libraries) {
    $taskZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $taskRun ('packages/'+$taskLibrary.package)))
    try {
        $taskEntry=$taskZip.GetEntry($taskLibrary.entry)
        if($null -eq $taskEntry) { throw 'ABI package entry missing.' }
        $taskStream=$taskEntry.Open()
        try { $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)) }
        finally { $taskStream.Dispose() }
        if($taskHash -cne $taskLibrary.sha256) { throw 'ABI proof does not match final packages.' }
    }
    finally { $taskZip.Dispose() }
}
Copy-Item -LiteralPath D:/s53abi/abi-package-binding.json -Destination $taskRun
$taskReviewNames=@('backend-review-result.md','backend-review-disposition.md')
$taskReviews=foreach($taskName in $taskReviewNames) {
    $taskPath=Join-Path '.scratch/t53' $taskName
    Copy-Item -LiteralPath $taskPath -Destination $taskRun
    [ordered]@{path=$taskName;sha256=(Get-FileHash $taskPath).Hash}
}
$taskEvidence=[ordered]@{kind='V1-53-final-evidence';result='Pass';validationRoot=$taskRun;
    validationSha256=(Get-FileHash $taskValidationPath).Hash;solutionCases=$taskCases.Count;v153Cases=$taskNew.Count;
    failed=0;skipped=0;packages=8;suites=$taskSuites;migrations=$taskMigrations;
    legacyAbi='Pass';reviews=$taskReviews;productionQualification='NotRun';publicNugetPublication='NotRun'}
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'ticket53-final-evidence.json') -Encoding utf8
$taskEvidence | Select-Object result,solutionCases,v153Cases,packages,legacyAbi | ConvertTo-Json
