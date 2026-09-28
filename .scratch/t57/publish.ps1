param([Parameter(Mandatory)][string]$Run, [string]$CoveragePath, [string]$ManifestPath, [string]$PerformancePath)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'collect-evidence.ps1') -Run $Run -CoveragePath $CoveragePath -ManifestPath $ManifestPath -PerformancePath $PerformancePath
$taskEvidencePath=Join-Path $PSScriptRoot 'final-evidence.json'
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
if($taskEvidence.result -cne 'PassWithRecordedDevelopmentFailures' -or $taskEvidence.solutionCases -ne 3204 -or $taskEvidence.v157Cases -ne 39 -or
    $taskEvidence.selectedFailures -ne 0 -or $taskEvidence.skipped -ne 0 -or $taskEvidence.migrationPaths -ne 12 -or
    $taskEvidence.migrationPhases -ne 168 -or $taskEvidence.packages.Count -ne 8 -or $taskEvidence.legacyAbi -cne 'Pass') {
    throw 'T57 final evidence is incomplete.'
}
$taskDocument=Get-Content -LiteralPath docs/verification/v1-57.md -Raw
if($taskDocument.Contains('最终整仓验证与独立消费者的运行路径、哈希和计数在完成后追加。') -or -not $taskDocument.Contains($taskEvidence.manifestSha256) -or
    -not $taskDocument.Contains($taskEvidence.runtimeSha256)) {throw 'Final verification document is incomplete.'}
function Invoke-TaskGit([string[]]$Arguments) {
    & git -c safe.directory=D:/projects/SharpInspect.NET -c core.autocrlf=true -c core.safecrlf=false @Arguments
    if($LASTEXITCODE -ne 0) {throw ('Git failed: '+($Arguments -join ' '))}
}
function Test-TaskProtectedDocuments {
    $taskOwnedProtected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-paths.json') -Raw | ConvertFrom-Json)
    foreach($taskFile in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-baseline.json') -Raw | ConvertFrom-Json | Where-Object { $_.path -notin $taskOwnedProtected })) {
        if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256) {throw 'Protected document changed.'}
    }
}
if((Invoke-TaskGit @('rev-parse','HEAD')).Trim() -cne '1d15b210828659c5f4b51bc6c973e48883c64001' -or
    (Invoke-TaskGit @('branch','--show-current')).Trim() -cne 'main') {throw 'T57 parent or branch changed.'}
$taskExpected=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-paths.json') -Raw | ConvertFrom-Json | Sort-Object)
if($taskExpected.Count -ne 61 -or @($taskExpected | Where-Object {
    $_ -notmatch '^(src/|tests/|tools/|samples/)' -and $_ -cne 'docs/verification/v1-57.md'
}).Count) {throw 'Unexpected staging manifest.'}
Test-TaskProtectedDocuments
$taskStagedBefore=@(Invoke-TaskGit @('diff','--cached','--name-only'))
if(@($taskStagedBefore | Where-Object {$_ -cnotin $taskExpected}).Count) {throw 'Unrelated staged changes.'}
Invoke-TaskGit (@('add','--')+$taskExpected)
$taskStaged=@(Invoke-TaskGit @('diff','--cached','--name-only') | Sort-Object)
if(($taskStaged -join "`n") -cne ($taskExpected -join "`n")) {throw 'Staged path set differs.'}
if(@(Invoke-TaskGit (@('diff','--name-only','--')+$taskExpected)).Count) {throw 'Ticket has unstaged edits.'}
Invoke-TaskGit @('diff','--cached','--check')
Invoke-TaskGit @('commit','-m','feat(performance): capture bounded cycle evidence and reports (T57)')
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
    [ordered]@{ticket=57;issue=58;head=$taskHead;tracking=$taskTracking;server=$taskServer;divergence=$taskDivergence;
        evidence=$taskEvidencePath;evidenceSha256=(Get-FileHash -LiteralPath $taskEvidencePath).Hash;
        protectedDocumentsUnchanged=$true;verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'publication.json') -Encoding utf8
}



