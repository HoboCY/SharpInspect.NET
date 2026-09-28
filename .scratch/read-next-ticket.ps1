$ErrorActionPreference='Stop'
. .scratch/sharpinspect-v1-tickets/github-session.ps1
Invoke-TicketGithubSession {
    param($taskGh)
    $taskIssue=& $taskGh issue view 57 --repo HoboCY/SharpInspect.NET --json number,title,body,state,labels,assignees,comments
    if($LASTEXITCODE -ne 0){throw 'Next issue lookup failed.'}
    [void][IO.Directory]::CreateDirectory((Join-Path (Get-Location) '.scratch/t56'))
    $taskIssue | Set-Content -LiteralPath '.scratch/t56/issue.json' -Encoding utf8
    $taskIssue
    & $taskGh issue list --repo HoboCY/SharpInspect.NET --state open --limit 100 --json number,title,labels,assignees
    if($LASTEXITCODE -ne 0){throw 'Queue lookup failed.'}
}
