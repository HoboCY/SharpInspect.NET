$ErrorActionPreference='Stop'
$taskEvidencePath=Join-Path $PSScriptRoot 'final-evidence/ticket53-composite-evidence.json'
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
if($taskEvidence.kind -cne 'V1-53-composite-validation' -or
    $taskEvidence.result -cne 'PassWithRecordedFailures' -or
    $taskEvidence.originalFailureCount -ne 1 -or $taskEvidence.recheckFailures -ne 0 -or
    $taskEvidence.distinctSolutionCases -ne 2980 -or $taskEvidence.v153Cases -ne 56 -or
    $taskEvidence.postTestResult -cne 'CompletedByBoundedContinuation' -or $taskEvidence.migrations.Count -ne 8 -or
    $taskEvidence.packages -ne 8 -or $taskEvidence.legacyAbi -cne 'Pass') { throw 'Complete composite evidence required.' }
foreach($taskArtifact in $taskEvidence.artifacts) {
    if((Get-FileHash -LiteralPath $taskArtifact.path).Hash -cne $taskArtifact.sha256) { throw 'Evidence artifact changed.' }
}
foreach($taskSource in $taskEvidence.sourceHashes) {
    if((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
function Invoke-TaskGit([string[]]$Arguments) {
    & git -c safe.directory=D:/projects/SharpInspect.NET -c core.autocrlf=true -c core.safecrlf=false @Arguments
    if($LASTEXITCODE -ne 0) { throw ('Git failed: '+($Arguments -join ' ')) }
}
if((Invoke-TaskGit @('rev-parse','HEAD')).Trim() -cne $taskEvidence.startingHead -or
    (Invoke-TaskGit @('branch','--show-current')).Trim() -cne 'main') { throw 'T53 baseline or branch changed.' }
$taskExpected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'stage-files.txt') | Sort-Object)
if($taskExpected.Count -ne 100 -or @($taskExpected | Where-Object {
    $_ -notmatch '^(src/|tests/|tools/|samples/|docs/verification/v1-53\.md$)' }).Count) { throw 'Unexpected staging manifest.' }
$taskStagedBefore=@(Invoke-TaskGit @('diff','--cached','--name-only'))
if(@($taskStagedBefore | Where-Object { $_ -cnotin $taskExpected }).Count) { throw 'Unrelated existing staged changes.' }
Invoke-TaskGit (@('add','--')+$taskExpected)
$taskStaged=@(Invoke-TaskGit @('diff','--cached','--name-only') | Sort-Object)
if(($taskStaged -join "`n") -cne ($taskExpected -join "`n")) { throw 'Staged path set differs.' }
if(@(Invoke-TaskGit (@('diff','--name-only','--')+$taskExpected)).Count) { throw 'Ticket has unstaged changes.' }
Invoke-TaskGit @('diff','--cached','--check')
Invoke-TaskGit @('commit','-m','feat(outbox): govern recovery and corrective deliveries with immutable history (T53)')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'sharpinspect-v1-tickets/github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    Invoke-TaskGit @('push','origin','main')
    $taskHead=(Invoke-TaskGit @('rev-parse','HEAD')).Trim()
    $taskTracking=(Invoke-TaskGit @('rev-parse','origin/main')).Trim()
    $taskRemote=@(Invoke-TaskGit @('ls-remote','origin','refs/heads/main'))
    if($taskRemote.Count -ne 1) { throw 'Remote verification unavailable.' }
    $taskServer=($taskRemote[0] -split '\s+')[0]
    $taskDivergence=(Invoke-TaskGit @('rev-list','--left-right','--count','HEAD...origin/main')).Trim()
    if($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') { throw 'Published SHA mismatch.' }
    [ordered]@{ticket=53;issue=54;head=$taskHead;tracking=$taskTracking;server=$taskServer;divergence=$taskDivergence;
        evidence=$taskEvidencePath;evidenceSha256=(Get-FileHash -LiteralPath $taskEvidencePath).Hash;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Encoding utf8
}
