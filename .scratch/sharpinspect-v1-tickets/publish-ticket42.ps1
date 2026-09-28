param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.result -cne 'Pass') { throw 'Final T42 gate has not passed.' }
$taskAbi = Get-Content -LiteralPath (Join-Path $taskRun 'abi-package-binding.json') -Raw | ConvertFrom-Json
$taskConsumer = Get-Content -LiteralPath (Join-Path $taskRun 'production-inspection-consumer-evidence.json') -Raw | ConvertFrom-Json
if ($taskAbi.result -cne 'Pass' -or $taskConsumer.Result -cne 'Pass') { throw 'T42 ABI and package consumer must pass.' }
if ((git rev-parse HEAD).Trim() -cne '2d468f37243e875e6d7dd86465d664b617e1282c') { throw 'T42 starting HEAD changed.' }
& (Join-Path $PSScriptRoot 'archive-ticket42-evidence.ps1') -Run $taskRun
if ((git branch --show-current) -cne 'main') { throw 'Unexpected publication branch.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket42-staged-files.txt') | Sort-Object)
$taskActual = @(git diff --cached --name-only | Sort-Object)
if (($taskExpected -join "`n") -cne ($taskActual -join "`n")) { throw 'Staged scope changed.' }
if (@($taskActual | Where-Object { $_ -match '^(\.scratch/|CONTEXT\.md$|docs/adr/)' }).Count -ne 0) {
    throw 'Unrelated evidence entered the staging area.'
}
if (@(git diff --name-only -- README.md src tests samples tools docs/verification/v1-42.md).Count -ne 0) {
    throw 'Ticket files still contain unstaged changes.'
}
git -c core.autocrlf=true -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git -c core.autocrlf=true -c core.safecrlf=false commit -m 'feat(runtime): gate production publication on durable inspection core (T42)'
if ($LASTEXITCODE -ne 0) { throw 'T42 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git -c core.autocrlf=true -c core.safecrlf=false push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T42 push failed; do not start T43.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTracking = (git rev-parse origin/main).Trim()
    $taskRemote = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote SHA verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Publication did not establish matching local, tracking, server HEAD and zero divergence.'
    }
    [ordered]@{ticket=42;issue=43;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');validationRoot=$taskRun} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket42-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket42-publication.json')
}


