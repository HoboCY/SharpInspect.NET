param([Parameter(Mandatory)][string]$OriginalRun, [Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskOriginal = [IO.Path]::GetFullPath($OriginalRun)
$taskRun = [IO.Path]::GetFullPath($Run)
if (Test-Path -LiteralPath $taskRun) { throw 'Consumer continuation requires a fresh directory.' }
$taskOriginalValidationPath = Join-Path $taskOriginal 'validation.json'
$taskEvidence = Get-Content -LiteralPath $taskOriginalValidationPath -Raw | ConvertFrom-Json -AsHashtable
$taskRestoreFailure = Get-Content -LiteralPath (Join-Path $taskOriginal 'calibration-import-consumer-restore.log') -Raw
if ($taskEvidence.result -cne 'Fail' -or $taskRestoreFailure -notmatch '磁盘空间不足') {
    throw 'Only the observed calibration-import restore disk-space failure may be resumed.'
}
function Get-TaskContinuationSourceHashes {
    $taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
        @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
    @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
}
function Assert-TaskContinuationSource {
    if ((Get-TaskContinuationSourceHashes | ConvertTo-Json -Depth 4 -Compress) -cne
        ($taskEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source changed since the passing full test run.' }
}
Assert-TaskContinuationSource
$taskTestFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskOriginal 'tests') -File -Filter '*.trx')
if ($taskTestFiles.Count -ne 7) { throw 'Seven complete test assemblies required.' }
$taskTotal = 0
foreach ($taskFile in $taskTestFiles) {
    [xml]$taskTrx = Get-Content -LiteralPath $taskFile.FullName -Raw
    $taskCounts = $taskTrx.TestRun.ResultSummary.Counters
    if ([int]$taskCounts.failed -ne 0 -or [int]$taskCounts.notExecuted -ne 0 -or
        [int]$taskCounts.total -ne [int]$taskCounts.passed) { throw 'A reused test suite did not pass.' }
    $taskTotal += [int]$taskCounts.passed
}
if ($taskTotal -ne 2259 -or (Get-Content -LiteralPath (Join-Path $taskOriginal 'preview-consumer-tests.log') -Raw) -notmatch '通过:\s+1') {
    throw 'Expected full tests and the last completed consumer checkpoint.'
}
[void][IO.Directory]::CreateDirectory($taskRun)
& compact.exe /C /Q $taskRun | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable compression for continuation artifacts.' }
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskOriginal -Directory | Where-Object {
    $_.Name -notin 'calibration-import-consumer','calibration-import-consumer-cache','calibration-import-demo','private-keys' }) {
    New-Item -ItemType Junction -Path (Join-Path $taskRun $taskDirectory.Name) -Target $taskDirectory.FullName | Out-Null
}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskOriginal -File | Where-Object Name -NE 'validation.json') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRun $taskFile.Name)
}
$taskEvidence.kind = 'V1-44-development-validation-resumed'
$taskEvidence.result = 'NotRun'
$taskEvidence.resumedFrom = $taskOriginal
$taskEvidence.resumedFromValidationSha256 = (Get-FileHash -LiteralPath $taskOriginalValidationPath).Hash
$taskEvidence.resumeReason = 'NuGet restore disk space exhausted; original failure preserved.'
$taskEvidence.reusedTestTotal = $taskTotal
$taskEvidence.reusedThrough = 'V133_N01 Preview independent NuGet consumer'
$taskEvidence.continuedConsumers = @(34..44)
$taskEvidence.resumeStartedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$taskEvidence.reusedTests = @($taskTestFiles | ForEach-Object {
    [ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
$taskFeed = Join-Path $taskOriginal 'packages'
$taskIdentityPolicy = Join-Path $taskRun 'development-identity-policy.json'
$taskAuditKeyDirectory = Join-Path $taskRun 'private-keys'
[void][IO.Directory]::CreateDirectory($taskAuditKeyDirectory)
$taskAuditKeyName = 'SharpInspect.DevelopmentValidation.T44.Continuation.' + [Guid]::NewGuid().ToString('N')
$taskAuditKeyName | Set-Content -LiteralPath (Join-Path $taskRun 'development-key-identity.txt') -Encoding utf8
try {
    foreach ($taskScript in @('Test-CalibrationImportConsumer.ps1','Test-ManualInspectionConsumer.ps1',
        'Test-ProductionAdmissionConsumer.ps1','Test-StationQualificationConsumer.ps1',
        'Test-RecipeTransferConsumer.ps1','Test-TraceStoragePolicyConsumer.ps1',
        'Test-ModbusQualificationConsumer.ps1','Test-PlcCommunicationConsumer.ps1',
        'Test-ProductionInspectionConsumer.ps1','Test-PartIdentityConsumer.ps1')) {
        & (Join-Path $taskRepo ('tools/' + $taskScript)) -Run $taskRun -PackageFeed $taskFeed
    }
    & (Join-Path $taskRepo 'tools/Test-ProductionRecoveryConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed `
        -IdentityPolicy $taskIdentityPolicy -AuditKey $taskAuditKeyName -AuditKeyDirectory $taskAuditKeyDirectory
    Assert-TaskContinuationSource
    foreach ($taskTest in $taskEvidence.reusedTests) {
        if ((Get-FileHash -LiteralPath $taskTest.path).Hash -cne $taskTest.sha256) { throw 'Reused test evidence changed.' }
    }
    if ((Get-FileHash -LiteralPath $taskOriginalValidationPath).Hash -cne $taskEvidence.resumedFromValidationSha256) {
        throw 'Original failed validation evidence changed.'
    }
    $taskEvidence.result = 'Pass'
    $taskEvidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    "V1 TICKET 44 RESUMED VALIDATION PASS: $taskRun"
}
catch {
    $taskEvidence.result = 'Fail'
    $taskEvidence.resumeFailure = $_.Exception.Message
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    throw
}
finally {
    $taskKeyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskAuditKeyName)))
    $taskKeyPath = [IO.Path]::GetFullPath((Join-Path $taskAuditKeyDirectory ($taskKeyHash + '.key')))
    if (-not $taskKeyPath.StartsWith($taskRun + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Disposable continuation key escaped the run directory.'
    }
    if (Test-Path -LiteralPath $taskKeyPath) { Remove-Item -LiteralPath $taskKeyPath }
}
