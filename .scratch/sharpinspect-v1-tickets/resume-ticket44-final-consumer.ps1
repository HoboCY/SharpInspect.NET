param([Parameter(Mandatory)][string]$FullRun, [Parameter(Mandatory)][string]$PreviousRun,
    [Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRepo = (Get-Location).Path
$taskRun = [IO.Path]::GetFullPath($Run)
if (Test-Path -LiteralPath $taskRun) { throw 'Fresh final continuation directory required.' }
$taskPreviousValidation = Join-Path $PreviousRun 'validation.json'
$taskFullValidation = Join-Path $FullRun 'validation.json'
$taskEvidence = Get-Content -LiteralPath $taskPreviousValidation -Raw | ConvertFrom-Json -AsHashtable
if ($taskEvidence.result -cne 'Fail' -or $taskEvidence.resumeFailure -notmatch 'production-recovery-consumer-run.log') {
    throw 'Expected the observed final consumer failure.'
}
$taskProgramRelative = 'samples\SharpInspect.SampleHost\Program.cs'
$taskProgramPath = Join-Path $taskRepo $taskProgramRelative
$taskProgramHash = @($taskEvidence.sourceHashes | Where-Object path -CEQ $taskProgramRelative)[0]
$taskProgramText = [IO.File]::ReadAllText($taskProgramPath)
$taskRestoredText = $taskProgramText.Replace("            await WaitAsync(() => !recovery.IsBusy);`n", '')
$taskRestoredHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskRestoredText)))
if ($taskRestoredHash -cne $taskProgramHash.sha256) { throw 'Only the known SampleHost smoke wait may differ from the full test run.' }
$taskEvidence.sourceChangesAfterFullTests = @([ordered]@{
    path=$taskProgramRelative; beforeSha256=$taskProgramHash.sha256;
    afterSha256=(Get-FileHash -LiteralPath $taskProgramPath).Hash;
    reason='Wait for the already-running maintenance status refresh before asserting; no library, test or package source changed.';
    validation='Rebuilt external SampleHost consumer runs the common smoke plus V144-N01 against unchanged candidate packages.'
})
$taskProgramHash.sha256 = (Get-FileHash -LiteralPath $taskProgramPath).Hash
function Assert-TaskPinnedSource {
    $taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
        @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
    $taskHashes = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
    if (($taskHashes | ConvertTo-Json -Depth 4 -Compress) -cne
        ($taskEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) { throw 'Source drift.' }
    foreach ($taskTest in $taskEvidence.reusedTests) {
        if ((Get-FileHash -LiteralPath $taskTest.path).Hash -cne $taskTest.sha256) { throw 'Test evidence drift.' }
    }
}
Assert-TaskPinnedSource
$taskPriorHash = (Get-FileHash -LiteralPath $taskPreviousValidation).Hash
$taskFullHash = (Get-FileHash -LiteralPath $taskFullValidation).Hash
[void][IO.Directory]::CreateDirectory($taskRun)
foreach ($taskDirectory in Get-ChildItem -LiteralPath $PreviousRun -Directory | Where-Object {
    $_.Name -notin 'production-recovery-consumer','production-recovery-consumer-cache','production-recovery-demo','private-keys' }) {
    $taskTarget = if ($taskDirectory.LinkType -eq 'Junction') { $taskDirectory.Target } else { $taskDirectory.FullName }
    New-Item -ItemType Junction -Path (Join-Path $taskRun $taskDirectory.Name) -Target $taskTarget | Out-Null
}
foreach ($taskFile in Get-ChildItem -LiteralPath $PreviousRun -File | Where-Object Name -NE 'validation.json') {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRun $taskFile.Name)
}
$taskEvidence.kind = 'V1-44-development-validation-composite'
$taskEvidence.result = 'NotRun'
$taskEvidence.originalFullRun = $FullRun
$taskEvidence.resumedFrom = $FullRun
$taskEvidence.resumedFromValidationSha256 = $taskFullHash
$taskEvidence.previousContinuation = $PreviousRun
$taskEvidence.previousContinuationValidationSha256 = $taskPriorHash
$taskEvidence.resumeReason = 'Original disk-space, continuation key-directory ACL, and asynchronous smoke assertion failures retained. Correct key ACL and wait for the ongoing maintenance status read in SampleHost smoke; rerun T44 consumer with unchanged library/test/package sources.'
$taskEvidence.Remove('resumeFailure')
$taskEvidence.reusedThrough = 'V143_N01 Part identity independent NuGet consumer'
$taskEvidence.continuedConsumers = @(44)
$taskEvidence.finalContinuationStartedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
$taskAuditDirectory = Join-Path $taskRun 'private-keys'
[void][IO.Directory]::CreateDirectory($taskAuditDirectory)
$taskAcl = Get-Acl -LiteralPath (Join-Path $FullRun 'private-keys')
if (-not $taskAcl.AreAccessRulesProtected) { throw 'Reference key directory ACL is not protected.' }
Set-Acl -LiteralPath $taskAuditDirectory -AclObject $taskAcl
if (-not (Get-Acl -LiteralPath $taskAuditDirectory).AreAccessRulesProtected) { throw 'Key directory ACL was not applied.' }
$taskAuditKeyName = 'SharpInspect.DevelopmentValidation.T44.FinalContinuation.' + [Guid]::NewGuid().ToString('N')
$taskAuditKeyName | Set-Content -LiteralPath (Join-Path $taskRun 'development-key-identity.txt') -Encoding utf8
try {
    & (Join-Path $taskRepo 'tools/Test-ProductionRecoveryConsumer.ps1') -Run $taskRun -PackageFeed (Join-Path $FullRun 'packages') `
        -IdentityPolicy (Join-Path $taskRun 'development-identity-policy.json') -AuditKey $taskAuditKeyName -AuditKeyDirectory $taskAuditDirectory
    Assert-TaskPinnedSource
    if ((Get-FileHash -LiteralPath $taskPreviousValidation).Hash -cne $taskPriorHash -or
        (Get-FileHash -LiteralPath $taskFullValidation).Hash -cne $taskFullHash) { throw 'Prior failure evidence changed.' }
    $taskEvidence.result = 'Pass'
    $taskEvidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    "V1 TICKET 44 COMPOSITE DEVELOPMENT VALIDATION PASS: $taskRun"
}
catch {
    $taskEvidence.result = 'Fail'
    $taskEvidence.resumeFailure = $_.Exception.Message
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    throw
}
finally {
    $taskKeyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskAuditKeyName)))
    $taskKeyPath = [IO.Path]::GetFullPath((Join-Path $taskAuditDirectory ($taskKeyHash + '.key')))
    if (-not $taskKeyPath.StartsWith($taskRun + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Disposable key escaped the owned run.'
    }
    if (Test-Path -LiteralPath $taskKeyPath) { Remove-Item -LiteralPath $taskKeyPath }
}
