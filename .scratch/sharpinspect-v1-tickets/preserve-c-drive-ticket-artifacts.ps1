$ErrorActionPreference = 'Stop'
$taskArchiveRoot = 'F:\temp\SharpInspect.NET-validation-artifacts\c-drive-preserved'
$taskSources = @(
    'C:\Users\Hobo\AppData\Local\Temp\SharpInspect.NET-validation-artifacts'
)
New-Item -ItemType Directory -Path $taskArchiveRoot -Force | Out-Null
function Get-TaskManifest([string]$TaskRoot) {
    @(Get-ChildItem -LiteralPath $TaskRoot -Recurse -File -Force | ForEach-Object {
        [pscustomobject]@{
            Relative = [IO.Path]::GetRelativePath($TaskRoot, $_.FullName)
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } | Sort-Object Relative)
}
$taskResults = @()
foreach ($taskSource in $taskSources) {
    $taskResolved = (Resolve-Path -LiteralPath $taskSource).Path
    if ($taskResolved -cne $taskSource -or -not $taskResolved.StartsWith('C:\Users\Hobo\AppData\Local\Temp\SharpInspect.', [StringComparison]::Ordinal)) {
        throw 'Task source path outside explicitly named temporary artifacts'
    }
    $taskItem = Get-Item -LiteralPath $taskResolved -Force
    if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source already relocated or reparse point' }
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskArchiveRoot $taskItem.Name))
    if (-not $taskDestination.StartsWith($taskArchiveRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $taskDestination)) { throw 'Task destination invalid or already exists' }
    $taskBefore = Get-TaskManifest $taskResolved
    Copy-Item -LiteralPath $taskResolved -Destination $taskDestination -Recurse -Force
    $taskAfter = Get-TaskManifest $taskDestination
    $taskSourceAgain = Get-TaskManifest $taskResolved
    $taskBeforeJson = $taskBefore | ConvertTo-Json -Depth 5 -Compress
    if ($taskBeforeJson -cne ($taskAfter | ConvertTo-Json -Depth 5 -Compress) -or
        $taskBeforeJson -cne ($taskSourceAgain | ConvertTo-Json -Depth 5 -Compress)) { throw 'Artifact hash verification failed; both copies preserved' }
    # The resolved source was checked above against two literal task-owned temporary paths.
    if ((Resolve-Path -LiteralPath $taskResolved).Path -cne $taskSource) { throw 'Source changed before removal' }
    $taskBeforeJson | Set-Content -LiteralPath ($taskDestination + '.sha256.json') -Encoding utf8
    Remove-Item -LiteralPath $taskResolved -Recurse -Force
    New-Item -ItemType Junction -Path $taskResolved -Value $taskDestination | Out-Null
    $taskThroughOriginal = Get-TaskManifest $taskResolved
    if ($taskBeforeJson -cne ($taskThroughOriginal | ConvertTo-Json -Depth 5 -Compress)) { throw 'Original-path junction verification failed' }
    $taskResults += [pscustomobject]@{Source=$taskResolved;Destination=$taskDestination;Files=$taskBefore.Count;Bytes=($taskBefore | Measure-Object Length -Sum).Sum;HashesMatch=$true}
}
$taskResults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskArchiveRoot 'relocation-evidence.json') -Encoding utf8
$taskResults | ConvertTo-Json -Depth 5 -Compress
Get-PSDrive -Name C | Select-Object Name,Free | ConvertTo-Json -Compress
