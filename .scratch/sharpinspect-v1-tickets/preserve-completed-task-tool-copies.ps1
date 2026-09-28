param([string[]]$TaskIds)
$ErrorActionPreference = 'Stop'
if (-not $TaskIds -or ($TaskIds | Where-Object { $_ -notmatch '^[0-9a-f]{32}$' })) { throw 'Explicit task ids required' }
$taskDestinationRoot = 'F:\temp\SharpInspect.NET-validation-artifacts\c-drive-preserved\completed-agent-tools'
New-Item -ItemType Directory -Path $taskDestinationRoot -Force | Out-Null
$taskRecords = @()
foreach ($taskId in $TaskIds) {
    $taskJobRoot = 'C:\Users\Hobo\.codex\deepseek-agents\jobs\' + $taskId
    $taskState = Get-Content -LiteralPath (Join-Path $taskJobRoot 'state.json') -Raw | ConvertFrom-Json
    if ($taskState.status -notin @('completed','cancelled') -or
        ($taskState.cwd -notlike 'F:\temp\SharpInspect.NET-validation-artifacts\ticket5*\worktrees\*' -and
         $taskState.cwd -ne 'D:\projects\SharpInspect.NET')) {
        throw 'Task is not a completed SharpInspect assignment'
    }
    if (Get-Process -Id $taskState.pid -ErrorAction SilentlyContinue) { throw 'Task process still alive; preserve its tools in place' }
    $taskSource = $taskJobRoot + '\codex-home\.sandbox-bin'
    $taskResolved = (Resolve-Path -LiteralPath $taskSource).Path
    if ($taskResolved -cne $taskSource -or
        ((Get-Item -LiteralPath $taskSource -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Unexpected task tool directory'
    }
    $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskDestinationRoot $taskId))
    if (-not $taskTarget.StartsWith($taskDestinationRoot + '\', [StringComparison]::Ordinal) -or
        (Test-Path -LiteralPath $taskTarget)) { throw 'Unexpected destination' }
    $taskFiles = @(Get-ChildItem -LiteralPath $taskSource -Recurse -File -Force | ForEach-Object {
        [pscustomobject]@{Relative=[IO.Path]::GetRelativePath($taskSource,$_.FullName);Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Recurse -Force
    foreach ($taskFile in $taskFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskTarget $taskFile.Relative)).Hash -cne $taskFile.Sha256 -or
            (Get-FileHash -LiteralPath (Join-Path $taskSource $taskFile.Relative)).Hash -cne $taskFile.Sha256) {
            throw 'Tool copy hash mismatch; both copies preserved'
        }
    }
    $taskFiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($taskTarget + '.sha256.json') -Encoding utf8
    if ((Resolve-Path -LiteralPath $taskSource).Path -cne $taskResolved) { throw 'Task source changed' }
    Remove-Item -LiteralPath $taskResolved -Recurse -Force
    New-Item -ItemType Junction -Path $taskResolved -Value $taskTarget | Out-Null
    $taskRecords += [pscustomobject]@{Id=$taskId;Source=$taskResolved;Target=$taskTarget;Bytes=($taskFiles | Measure-Object Length -Sum).Sum;HashesMatch=$true}
}
$taskEvidenceName = 'relocation-evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'
$taskRecords | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDestinationRoot $taskEvidenceName) -Encoding utf8
$taskRecords | ConvertTo-Json -Depth 5 -Compress
Get-PSDrive -Name C | Select-Object Name,Free | ConvertTo-Json -Compress
