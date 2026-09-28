$ErrorActionPreference = 'Stop'
Add-Type -Path '.scratch/t57/sqlite-path-probe.cs'
foreach ($taskTempRoot in 'F:\temp\SharpInspect-T57-Final','F:\t57') {
    $taskDirectory = Join-Path $taskTempRoot ('SharpInspect.Runtime.Tests\V155-M03-FeatureInitialized-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($taskDirectory)
    $taskBackup = Join-Path $taskDirectory ('.sharpinspect-migration-' + ('a' * 64) + '.' + [Guid]::NewGuid().ToString('N') + '.1.backup.db')
    [SqlitePathProbe]::Run($taskBackup)
}
