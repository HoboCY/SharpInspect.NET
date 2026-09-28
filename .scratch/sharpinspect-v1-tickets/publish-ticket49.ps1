param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskRun = [IO.Path]::GetFullPath($Run)
$taskValidationPath = Join-Path $taskRun 'validation.json'
$taskValidation = Get-Content -LiteralPath $taskValidationPath -Raw | ConvertFrom-Json
$taskFinal = Get-Content -LiteralPath (Join-Path $taskRun 'ticket49-final-evidence.json') -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-49-development-validation' -or $taskValidation.result -cne 'Pass' -or
    $taskFinal.result -cne 'Pass' -or $taskFinal.reviewDisposition -cne 'Pass' -or $taskFinal.legacyAbi -cne 'Pass' -or
    $taskFinal.packageCount -ne 8 -or $taskFinal.failedTests -ne 0 -or $taskFinal.v149CaseCount -lt 74 -or
    (Get-FileHash -LiteralPath $taskValidationPath).Hash -cne $taskFinal.validationSha256) {
    throw 'Complete T49 validation, package, ABI and independent review evidence required.'
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Validated source changed.' }
}
$taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskPaths = @($taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    ForEach-Object { [IO.Path]::GetRelativePath((Get-Location).Path,$_.FullName) } | Sort-Object)
if (($taskPaths -join "`n") -cne (@($taskValidation.sourceHashes.path | Sort-Object) -join "`n")) {
    throw 'Validated source set changed.'
}
if ((git rev-parse HEAD).Trim() -cne 'bf49aec015598f7a0537b45ea343966754648b27' -or
    (git branch --show-current).Trim() -cne 'main') { throw 'Unexpected T49 publication baseline.' }
$taskExpected = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-staged-files.txt') | Sort-Object)
$taskStaged = @(git -c core.autocrlf=true -c core.safecrlf=false diff --cached --name-only | Sort-Object)
if ($taskExpected.Count -eq 0 -or ($taskExpected -join "`n") -cne ($taskStaged -join "`n")) {
    throw 'Staged scope mismatch.'
}
if (@($taskStaged | Where-Object { $_ -notmatch '^(src/|tests/|tools/|docs/verification/v1-49\.md$)' }).Count) {
    throw 'Unrelated files entered staging.'
}
if (@(git -c core.autocrlf=true -c core.safecrlf=false diff --name-only -- @taskExpected).Count) {
    throw 'Unstaged ticket changes remain.'
}
git -c core.autocrlf=true -c core.safecrlf=false diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace check failed.' }
git -c core.autocrlf=true -c core.safecrlf=false commit -m 'feat(storage): migrate schema in exclusive startup maintenance (T49)'
if ($LASTEXITCODE -ne 0) { throw 'T49 commit failed.' }
. (Join-Path $PSScriptRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    git -c core.autocrlf=true -c core.safecrlf=false push origin main
    if ($LASTEXITCODE -ne 0) { throw 'T49 push failed; do not start T50.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTracking = (git rev-parse origin/main).Trim()
    $taskRemote = @(git ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $taskRemote.Count -ne 1) { throw 'Remote verification failed.' }
    $taskServer = ($taskRemote[0] -split '\s+')[0]
    $taskDivergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
    if ($taskHead -cne $taskTracking -or $taskHead -cne $taskServer -or $taskDivergence -notmatch '^0\s+0$') {
        throw 'Local, tracking and remote SHA must match with zero divergence.'
    }
    [ordered]@{ticket=49;issue=50;branch='main';head=$taskHead;tracking=$taskTracking;server=$taskServer;
        tree=(git rev-parse 'HEAD^{tree}').Trim();divergence=$taskDivergence;validationRoot=$taskRun;
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-publication.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-publication.json')
}


