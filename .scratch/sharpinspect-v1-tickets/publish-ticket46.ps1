param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Raw | ConvertFrom-Json
$taskGatePath = Join-Path $taskRun 'validation.json'
$taskCases = Get-Content -LiteralPath (Join-Path $taskRun 'v146-case-results.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass' -or $taskValidation.distinctPassedCases -ne 2385 -or
    $taskValidation.legacyAbi -cne 'Pass' -or $taskCases.result -cne 'Pass' -or $taskCases.assemblyCaseCount -ne 95 -or
    (Get-FileHash -LiteralPath $taskGatePath).Hash -cne $taskValidation.baseValidation.sha256) { throw 'Final T46 evidence required.' }
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
if ((git rev-parse HEAD).Trim() -cne '3fea02912f46538d712a67ecbdc12550617263e6' -or
    (git branch --show-current).Trim() -cne 'main') { throw 'Unexpected publication baseline.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket46-staged-files.txt') | Sort-Object)
$taskStaged = @(git -c core.autocrlf=true -c core.safecrlf=false diff --cached --name-only | Sort-Object)
if ($taskExpected.Count -eq 0 -or ($taskExpected -join "`n") -cne ($taskStaged -join "`n")) { throw 'Staged scope mismatch.' }
if (@($taskStaged | Where-Object { $_ -notmatch '^(src/|tests/|tools/|docs/verification/v1-46\.md$|docs/production-recovery\.md$)' }).Count) {
    throw 'Unrelated files entered staging.'
}
if (@(git -c core.autocrlf=true -c core.safecrlf=false diff --name-only -- @taskExpected).Count) { throw 'Unstaged ticket changes remain.' }
git -c core.autocrlf=true -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git -c core.autocrlf=true -c core.safecrlf=false commit -m 'feat(runtime): add exact PLC recipe selection and dedicated audited handshake (T46)'
if ($LASTEXITCODE -ne 0) { throw 'T46 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git -c core.autocrlf=true -c core.safecrlf=false push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T46 push failed; do not start T47.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTracking = (git rev-parse origin/main).Trim()
    $taskRemote = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Local, tracking and remote SHA must match with zero divergence.'
    }
    [ordered]@{ticket=46;issue=47;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;validationRoot=$taskRun;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket46-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket46-publication.json')
}
