$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'collect-evidence.ps1')
$taskEvidencePath=Join-Path $PSScriptRoot 'final-evidence.json'
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
if($taskEvidence.result -cne 'PassWithRecordedFailures' -or $taskEvidence.solutionCases -ne 3035 -or
    $taskEvidence.v154Cases -ne 55 -or $taskEvidence.failed -ne 0 -or $taskEvidence.skipped -ne 0 -or
    $taskEvidence.migrationPaths -ne 11 -or $taskEvidence.migrationPhases -ne 154 -or
    $taskEvidence.packages.Count -ne 8 -or $taskEvidence.legacyAbi -cne 'Pass') {throw 'T54 composite evidence is incomplete'}
function Invoke-TaskGit([string[]]$Arguments) {
    & git -c safe.directory=D:/projects/SharpInspect.NET -c core.autocrlf=true -c core.safecrlf=false @Arguments
    if($LASTEXITCODE -ne 0) {throw ('Git failed: '+($Arguments -join ' '))}
}
if((Invoke-TaskGit @('rev-parse','HEAD')).Trim() -cne 'a884db7e79330044a179ba344b25bba9de1431bc' -or
   (Invoke-TaskGit @('branch','--show-current')).Trim() -cne 'main') {throw 'T54 parent or branch changed'}
$taskExpected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'stage-paths.json') -Raw | ConvertFrom-Json | Sort-Object)
if($taskExpected.Count -ne 111 -or @($taskExpected | Where-Object {
    $_ -notmatch '^(src/|tests/|tools/|samples/|docs/verification/v1-54\.md$)' }).Count) {throw 'Unexpected staging manifest'}
$taskStagedBefore=@(Invoke-TaskGit @('diff','--cached','--name-only'))
if(@($taskStagedBefore | Where-Object {$_ -cnotin $taskExpected}).Count) {throw 'Unrelated staged changes'}
Invoke-TaskGit (@('add','--')+$taskExpected)
$taskStaged=@(Invoke-TaskGit @('diff','--cached','--name-only') | Sort-Object)
if(($taskStaged -join "`n") -cne ($taskExpected -join "`n")) {throw 'Staged path set differs'}
if(@(Invoke-TaskGit (@('diff','--name-only','--')+$taskExpected)).Count) {throw 'Ticket has unstaged edits'}
Invoke-TaskGit @('diff','--cached','--check')
Invoke-TaskGit @('commit','-m','feat(evidence): reconcile startup files and scrub immutable history (T54)')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    Invoke-TaskGit @('push','origin','main')
    $taskHead=(Invoke-TaskGit @('rev-parse','HEAD')).Trim()
    $taskTracking=(Invoke-TaskGit @('rev-parse','origin/main')).Trim()
    $taskRemote=@(Invoke-TaskGit @('ls-remote','origin','refs/heads/main'))
    if($taskRemote.Count -ne 1) {throw 'Remote verification unavailable'}
    $taskServer=($taskRemote[0] -split '\s+')[0]
    $taskDivergence=(Invoke-TaskGit @('rev-list','--left-right','--count','HEAD...origin/main')).Trim()
    if($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {throw 'Published SHA mismatch'}
    [ordered]@{ticket=54;issue=55;head=$taskHead;tracking=$taskTracking;server=$taskServer;divergence=$taskDivergence;
        evidence=$taskEvidencePath;evidenceSha256=(Get-FileHash -LiteralPath $taskEvidencePath).Hash;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Encoding utf8
}
