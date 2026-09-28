param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation-composite.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Final T45 gate has not passed.' }
$taskBase = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
$taskCases = Get-Content -LiteralPath (Join-Path $taskRun 'v145-case-results.json') -Raw | ConvertFrom-Json
$taskReview = Get-Content -LiteralPath (Join-Path $taskRun 'review-evidence.json') -Raw | ConvertFrom-Json
if ($taskBase.result -cne 'Pass' -or $taskCases.result -cne 'Pass' -or
    $taskCases.assemblyCaseCount -ne 31 -or $taskReview.result -cne 'Pass' -or
    (Get-FileHash -LiteralPath (Join-Path $taskRun 'validation.json')).Hash -cne $taskValidation.baseValidation.sha256) {
    throw 'Original full gate, final cases and review must pass with unchanged base evidence.'
}
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskRun 'production-recovery-demo/production-recovery-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or $taskConsumer.Result -cne 'Pass') { throw 'T45 ABI and package consumer must pass.' }
if ((git -c core.autocrlf=true -c core.safecrlf=false rev-parse HEAD).Trim() -cne 'bc8a583eab77ab98207e64ca5cd1b9de1f6c981a') { throw 'T45 starting HEAD changed.' }
& (Join-Path $PSScriptRoot 'archive-ticket45-evidence.ps1') -Run $taskRun
if ((git -c core.autocrlf=true -c core.safecrlf=false branch --show-current) -cne 'main') { throw 'Unexpected publication branch.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-staged-files.txt') | Sort-Object)
$taskActual = @(git -c core.autocrlf=true -c core.safecrlf=false diff --cached --name-only | Sort-Object)
if (($taskExpected -join "`n") -cne ($taskActual -join "`n")) { throw 'Staged scope changed.' }
if (@($taskActual | Where-Object { $_ -match '^(\.scratch/|CONTEXT\.md$|docs/adr/)' }).Count -ne 0) {
    throw 'Unrelated evidence entered the staging area.'
}
if (@(git -c core.autocrlf=true -c core.safecrlf=false diff --name-only -- @taskExpected).Count -ne 0) {
    throw 'Ticket files still contain unstaged changes.'
}
git -c core.autocrlf=true -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git -c core.autocrlf=true -c core.safecrlf=false commit -m 'feat(runtime): separate graceful stop and fault abort with bounded shutdown (T45)'
if ($LASTEXITCODE -ne 0) { throw 'T45 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git -c core.autocrlf=true -c core.safecrlf=false push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T45 push failed; do not start T46.' }
    $taskHead = (git -c core.autocrlf=true -c core.safecrlf=false rev-parse HEAD).Trim()
    $taskTracking = (git -c core.autocrlf=true -c core.safecrlf=false rev-parse origin/main).Trim()
    $taskRemote = @(git -c core.autocrlf=true -c core.safecrlf=false ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote SHA verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git -c core.autocrlf=true -c core.safecrlf=false rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Publication did not establish matching local, tracking, server HEAD and zero divergence.'
    }
    [ordered]@{ticket=45;issue=46;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git -c core.autocrlf=true -c core.safecrlf=false rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');validationRoot=$taskRun} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket45-publication.json')
}



