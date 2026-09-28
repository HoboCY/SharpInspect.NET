$ErrorActionPreference = 'Stop'
$taskRoot = 'F:\temp\SharpInspect.NET-t53-backend-review-20260914'
if (Test-Path -LiteralPath $taskRoot) { throw 'Review snapshot already exists' }
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$taskZip = Join-Path $taskRoot 'baseline.zip'
git -c safe.directory=D:/projects/SharpInspect.NET archive 4302f5c -o $taskZip
if ($LASTEXITCODE -ne 0) { throw 'Baseline archive failed' }
$taskCandidate = Join-Path $taskRoot 'candidate'
Expand-Archive -LiteralPath $taskZip -DestinationPath $taskCandidate
$taskPaths = @(git -c safe.directory=D:/projects/SharpInspect.NET diff --name-only 4302f5c -- src tests tools samples)
$taskPaths += @(git -c safe.directory=D:/projects/SharpInspect.NET ls-files --others --exclude-standard -- src tests tools)
foreach ($taskRelative in $taskPaths) {
    $taskSource = Join-Path 'D:\projects\SharpInspect.NET' $taskRelative
    $taskTarget = Join-Path $taskCandidate $taskRelative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskTarget) | Out-Null
    Copy-Item -LiteralPath $taskSource -Destination $taskTarget
}
git -c safe.directory=D:/projects/SharpInspect.NET diff 4302f5c -- src tests tools samples | Set-Content -LiteralPath (Join-Path $taskRoot 'tracked.diff') -Encoding utf8
$taskPaths | Set-Content -LiteralPath (Join-Path $taskRoot 'changed-files.txt') -Encoding utf8
Copy-Item -LiteralPath '.scratch/sharpinspect-v1-tickets/issues/53-outbox-failures.md' -Destination (Join-Path $taskRoot 'ticket-53.md')
foreach ($taskAdr in '0072','0073','0074','0079','0103') {
    Get-ChildItem -LiteralPath 'docs/adr' -Filter "$taskAdr*" -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $taskRoot
    }
}
$taskRoot
