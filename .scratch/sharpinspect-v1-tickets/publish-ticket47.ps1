param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
$taskFinal = Get-Content -LiteralPath (Join-Path $taskRun 'ticket47-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-47-development-validation-composite' -or $taskValidation.result -cne 'Pass' -or
    $taskFinal.kind -cne 'V1-47-final-composite-development-validation' -or $taskFinal.fullSuiteSingleRunPass -ne $false -or
    $taskFinal.originalFailedTests -ne 2 -or $taskFinal.followupPassed -ne 87 -or
    $taskFinal.result -cne 'Pass' -or $taskFinal.legacyAbi -cne 'Pass' -or
    $taskFinal.reviewDisposition -cne 'Pass' -or $taskFinal.v147CaseCount -ne 84 -or
    $taskFinal.failedTests -ne 0 -or $taskFinal.packageCount -ne 8 -or
    (Get-FileHash -LiteralPath (Join-Path $taskRun 'validation.json')).Hash -cne $taskFinal.validationSha256) {
    throw 'Final T47 validation, ABI and independent review evidence required.'
}
$taskFormatting = Get-Content -LiteralPath (Join-Path $taskRun 'publication-formatting-evidence.json') -Raw | ConvertFrom-Json
if ($taskFormatting.result -cne 'Pass' -or $taskFormatting.path -cne 'tools/Test-ProductionArmingConsumer.ps1' -or
    (Get-FileHash -LiteralPath $taskFormatting.validatedSource).Hash -cne $taskFormatting.before -or
    (Get-FileHash -LiteralPath $taskFormatting.path).Hash -cne $taskFormatting.after -or
    [IO.File]::ReadAllText($taskFormatting.validatedSource).TrimEnd([char[]]"`r`n") -cne
        [IO.File]::ReadAllText((Join-Path (Get-Location) $taskFormatting.path)).TrimEnd([char[]]"`r`n")) {
    throw 'Only exact trailing-newline cleanup may follow the completed consumer gate.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256 -and
        ($taskSource.path.Replace('\','/') -cne $taskFormatting.path -or $taskSource.sha256 -cne $taskFormatting.before)) {
        throw 'Validated source changed beyond the recorded trailing-newline cleanup.'
    }
}
$taskSourceFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskSourcePaths = @($taskSourceFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    ForEach-Object { [IO.Path]::GetRelativePath((Get-Location).Path,$_.FullName) } | Sort-Object)
$taskRecordedPaths = @($taskValidation.sourceHashes.path | Sort-Object)
if (($taskSourcePaths -join "`n") -cne ($taskRecordedPaths -join "`n")) { throw 'Validated source set changed.' }
if ((git rev-parse HEAD).Trim() -cne '4333df35657ffa84f44877bfb9fd7af19e2d1d50' -or
    (git branch --show-current).Trim() -cne 'main') { throw 'Unexpected publication baseline.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket47-staged-files.txt') | Sort-Object)
$taskStaged = @(git -c core.autocrlf=true -c core.safecrlf=false diff --cached --name-only | Sort-Object)
if ($taskExpected.Count -eq 0 -or ($taskExpected -join "`n") -cne ($taskStaged -join "`n")) { throw 'Staged scope mismatch.' }
if (@($taskStaged | Where-Object { $_ -notmatch '^(src/|tests/|tools/|docs/verification/v1-47\.md$)' }).Count) {
    throw 'Unrelated files entered staging.'
}
if (@(git -c core.autocrlf=true -c core.safecrlf=false diff --name-only -- @taskExpected).Count) {
    throw 'Unstaged ticket changes remain.'
}
git -c core.autocrlf=true -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git -c core.autocrlf=true -c core.safecrlf=false commit -m 'feat(runtime): govern independent startup and post-PLC production arming (T47)'
if ($LASTEXITCODE -ne 0) { throw 'T47 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git -c core.autocrlf=true -c core.safecrlf=false push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T47 push failed; do not start T48.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTracking = (git rev-parse origin/main).Trim()
    $taskRemote = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Local, tracking and remote SHA must match with zero divergence.'
    }
    [ordered]@{ticket=47;issue=48;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;validationRoot=$taskRun;
        formattingEvidenceSha256=(Get-FileHash -LiteralPath (Join-Path $taskRun 'publication-formatting-evidence.json')).Hash;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket47-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket47-publication.json')
}
