param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'collect-evidence.ps1') -Run $Run
$taskEvidencePath=Join-Path $PSScriptRoot 'final-evidence.json'
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
if($taskEvidence.result -cne 'PassWithRecordedFailures' -or $taskEvidence.solutionCases -ne 3127 -or $taskEvidence.v155Cases -ne 92 -or
    $taskEvidence.failed -ne 0 -or $taskEvidence.skipped -ne 0 -or $taskEvidence.migrationPaths -ne 12 -or
    $taskEvidence.migrationPhases -ne 168 -or $taskEvidence.packages.Count -ne 8 -or $taskEvidence.legacyAbi -cne 'Pass') {
    throw 'T55 final evidence is incomplete.'
}
$taskDocument=Get-Content -LiteralPath docs/verification/v1-55.md -Raw
if($taskDocument.Contains('完整验证仍在执行') -or -not $taskDocument.Contains($taskEvidence.manifestSha256) -or
    -not $taskDocument.Contains($taskEvidence.runtimeSha256)) {throw 'Final verification document is incomplete.'}
function Invoke-TaskGit([string[]]$Arguments) {
    & git -c safe.directory=D:/projects/SharpInspect.NET -c core.autocrlf=true -c core.safecrlf=false @Arguments
    if($LASTEXITCODE -ne 0) {throw ('Git failed: '+($Arguments -join ' '))}
}
function Test-TaskProtectedDocuments {
    foreach($taskFile in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'protected-files.json') -Raw | ConvertFrom-Json)) {
        if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Protected document changed.'}
    }
}
if((Invoke-TaskGit @('rev-parse','HEAD')).Trim() -cne 'a4147aeffd757690a65dd8c42d96953837a2f8d4' -or
    (Invoke-TaskGit @('branch','--show-current')).Trim() -cne 'main') {throw 'T55 parent or branch changed.'}
$taskExpected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-files.json') -Raw | ConvertFrom-Json | Sort-Object)
if($taskExpected.Count -ne 150 -or @($taskExpected | Where-Object {
    $_ -notmatch '^(src/|tests/|tools/|samples/)' -and $_ -cne 'docs/verification/v1-55.md'
}).Count) {throw 'Unexpected staging manifest.'}
Test-TaskProtectedDocuments
$taskStagedBefore=@(Invoke-TaskGit @('diff','--cached','--name-only'))
if(@($taskStagedBefore | Where-Object {$_ -cnotin $taskExpected}).Count) {throw 'Unrelated staged changes.'}
Invoke-TaskGit (@('add','--')+$taskExpected)
$taskStaged=@(Invoke-TaskGit @('diff','--cached','--name-only') | Sort-Object)
if(($taskStaged -join "`n") -cne ($taskExpected -join "`n")) {throw 'Staged path set differs.'}
if(@(Invoke-TaskGit (@('diff','--name-only','--')+$taskExpected)).Count) {throw 'Ticket has unstaged edits.'}
Invoke-TaskGit @('diff','--cached','--check')
Invoke-TaskGit @('commit','-m','feat(storage): govern capacity and evidence retention (T55)')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    Invoke-TaskGit @('push','origin','main')
    $taskHead=(Invoke-TaskGit @('rev-parse','HEAD')).Trim()
    $taskTracking=(Invoke-TaskGit @('rev-parse','origin/main')).Trim()
    $taskRemote=@(Invoke-TaskGit @('ls-remote','origin','refs/heads/main'))
    if($taskRemote.Count -ne 1) {throw 'Remote verification unavailable.'}
    $taskServer=($taskRemote[0] -split '\s+')[0]
    $taskDivergence=(Invoke-TaskGit @('rev-list','--left-right','--count','HEAD...origin/main')).Trim()
    if($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {throw 'Published SHA mismatch.'}
    Test-TaskProtectedDocuments
    [ordered]@{ticket=55;issue=56;head=$taskHead;tracking=$taskTracking;server=$taskServer;divergence=$taskDivergence;
        evidence=$taskEvidencePath;evidenceSha256=(Get-FileHash -LiteralPath $taskEvidencePath).Hash;
        protectedDocumentsUnchanged=$true;verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Encoding utf8
}
