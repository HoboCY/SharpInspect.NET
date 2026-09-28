$ErrorActionPreference='Stop'
. .scratch/sharpinspect-v1-tickets/github-session.ps1
Invoke-TicketGithubSession {
    param($taskGh)
    $taskDependency=& $taskGh issue view 10 --repo HoboCY/SharpInspect.NET --json number,state | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $taskDependency.state -cne 'CLOSED'){throw 'Dependency #10 is not closed.'}
    $taskIssue=& $taskGh issue view 57 --repo HoboCY/SharpInspect.NET --json number,state,title,assignees | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $taskIssue.state -cne 'OPEN' -or $taskIssue.title -notmatch '^\[V1-56\]' -or
        @($taskIssue.assignees | Where-Object login -cne 'HoboCY').Count){throw 'Ticket cannot be claimed.'}
    & $taskGh issue edit 57 --repo HoboCY/SharpInspect.NET --add-assignee '@me'
    if($LASTEXITCODE -ne 0){throw 'Claim failed.'}
    $taskClaim=& $taskGh issue view 57 --repo HoboCY/SharpInspect.NET --json number,state,title,assignees,labels
    if($LASTEXITCODE -ne 0){throw 'Claim readback failed.'}
    $taskClaim | Set-Content -LiteralPath .scratch/t56/claim.json -Encoding utf8
    $taskClaim
}
