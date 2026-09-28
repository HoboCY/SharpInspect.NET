param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Final T36 gate has not passed.' }
& (Join-Path $PSScriptRoot 'archive-ticket36-evidence.ps1') -Run $taskRun
if ((git branch --show-current) -cne 'main') { throw 'Unexpected publication branch.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket36-staged-files.txt') | Sort-Object)
$taskActual = @(git diff --cached --name-only | Sort-Object)
if (($taskExpected -join "`n") -cne ($taskActual -join "`n")) { throw 'Staged scope changed.' }
if (@($taskActual | Where-Object { $_ -match '^(\.scratch/|CONTEXT\.md$|docs/adr/)' }).Count -ne 0) {
    throw 'Unrelated evidence entered the staging area.'
}
if (@(git diff --name-only -- README.md src tests samples tools docs/verification/v1-35.md docs/verification/v1-36.md).Count -ne 0) {
    throw 'Ticket files still contain unstaged changes.'
}
git -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git commit -m 'feat(admission): evaluate exact production readiness gates (T36)'
if ($LASTEXITCODE -ne 0) { throw 'T36 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T36 push failed; do not start T37.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTracking = (git rev-parse origin/main).Trim()
    $taskRemote = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote SHA verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Publication did not establish matching local, tracking, server HEAD and zero divergence.'
    }
    [ordered]@{ticket=36;issue=37;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');validationRoot=$taskRun} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket36-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket36-publication.json')
}
