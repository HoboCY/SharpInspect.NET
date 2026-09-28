$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskBase = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/20260911-231805-790'
$taskFixed = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/consumer-fixed-01'
$taskRun = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/20260911-final-consumer-continuation'
$taskBaseFile = Join-Path $taskBase 'validation.json'
$taskBaseEvidence = Get-Content -LiteralPath $taskBaseFile -Raw | ConvertFrom-Json
if ($taskBaseEvidence.result -cne 'Fail') { throw 'Original failed consumer run must be preserved.' }
$taskFixedFile = Join-Path $taskFixed 'production-recovery-demo/production-recovery-consumer-evidence.json'
$taskFixedEvidence = Get-Content -LiteralPath $taskFixedFile -Raw | ConvertFrom-Json
$taskOriginalBinaryFile = 'F:/temp/SharpInspect.NET-validation-artifacts/ticket46/original-consumer-fixed-input/evidence.json'
$taskOriginalBinary = Get-Content -LiteralPath $taskOriginalBinaryFile -Raw | ConvertFrom-Json
if ($taskFixedEvidence.Result -cne 'Pass' -or -not $taskFixedEvidence.IndependentSigningKeyCreated -or
    -not $taskFixedEvidence.DisposableSigningKeyRemoved -or $taskOriginalBinary.result -cne 'Pass' -or
    -not $taskOriginalBinary.unchanged) { throw 'Fixed fixture and unchanged original consumer proof required.' }
if (Test-Path -LiteralPath $taskRun) { throw 'Fresh continuation root required.' }
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
$taskSources = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
if ($taskSources.Count -ne @($taskBaseEvidence.sourceHashes).Count) { throw 'Unexpected source path set.' }
$taskBefore = @{}
foreach ($taskSource in $taskBaseEvidence.sourceHashes) { $taskBefore[$taskSource.path] = $taskSource.sha256 }
$taskDelta = @()
foreach ($taskSource in $taskSources) {
    if (-not $taskBefore.ContainsKey($taskSource.path)) { throw 'Unexpected new source file.' }
    if ($taskSource.sha256 -cne $taskBefore[$taskSource.path]) {
        if ($taskSource.path.Replace('\','/') -cne 'tools/Test-ProductionRecoveryConsumer.ps1') {
            throw ('Unexpected product/source delta: ' + $taskSource.path)
        }
        $taskDelta += [ordered]@{path=$taskSource.path;before=$taskBefore[$taskSource.path];after=$taskSource.sha256}
    }
}
if ($taskDelta.Count -ne 1) { throw 'Exactly the consumer fixture repair is required.' }
[void][IO.Directory]::CreateDirectory($taskRun)
foreach ($taskDirectory in Get-ChildItem -LiteralPath $taskBase -Directory | Where-Object {
    $_.Name -notin 'production-recovery-consumer','production-recovery-consumer-cache','production-recovery-demo','private-keys' }) {
    New-Item -ItemType Junction -Path (Join-Path $taskRun $taskDirectory.Name) -Target $taskDirectory.FullName | Out-Null
}
foreach ($taskName in 'production-recovery-consumer','production-recovery-demo') {
    New-Item -ItemType Junction -Path (Join-Path $taskRun $taskName) -Target (Join-Path $taskFixed $taskName) | Out-Null
}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskBase -File | Where-Object {
    $_.Name -ne 'validation.json' -and $_.Name -notlike 'production-recovery-*' }) {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRun $taskFile.Name)
}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskFixed -File -Filter '*.log') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRun $taskFile.Name)
}
$taskEvidence = [ordered]@{}
foreach ($taskProperty in $taskBaseEvidence.PSObject.Properties) { $taskEvidence[$taskProperty.Name] = $taskProperty.Value }
$taskEvidence.sourceHashes = $taskSources
$taskEvidence.result = 'NotRun'
$taskEvidence.consumerContinuation = [ordered]@{
    intermediateRun=$taskBase;intermediateResult='Fail';intermediateValidationSha256=(Get-FileHash -LiteralPath $taskBaseFile).Hash;
    failure='A new independent consumer database reused the previous database signing key, causing AuditSigningKeyProvisioningConflict and the generic 8-second audit-Verified wait to expire.';
    sourceDelta=$taskDelta;reusedThrough='V143_N01; Release build, eight packages and preceding consumers actually passed in the intermediate run.';
    fixedConsumer=[ordered]@{path=$taskFixedFile;sha256=(Get-FileHash -LiteralPath $taskFixedFile).Hash;result='Pass'};
    originalConsumerBinary=[ordered]@{path=$taskOriginalBinaryFile;sha256=(Get-FileHash -LiteralPath $taskOriginalBinaryFile).Hash;result='Pass'};
    review='Pending';limitation='No timeout, product key policy or consumer assertion was relaxed. Earlier diagnostic setup errors are recorded separately.'}
$taskManifest = Join-Path $taskRun 'validation.json'
$taskEvidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskManifest -Encoding utf8
& 'tools/Test-RecipeSelectionConsumer.ps1' -Run $taskRun -PackageFeed (Join-Path $taskRun 'packages')
if (-not $?) { throw 'T46 package consumer failed.' }
& '.scratch/sharpinspect-v1-tickets/check-ticket44-legacy-abi.ps1' -Run $taskRun
if (-not $?) { throw 'Old ABI check failed.' }
$taskEvidence.consumerContinuation.remainingConsumers = 'V146_N01 and unchanged old ABI Pass; independent consumer-script review pending.'
$taskEvidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskManifest -Encoding utf8
Write-Output ('T46 remaining consumers and ABI passed; final review pending: ' + $taskRun)
