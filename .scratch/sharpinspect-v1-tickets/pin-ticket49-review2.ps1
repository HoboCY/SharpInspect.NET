$ErrorActionPreference = 'Stop'
$taskRoot = (Get-Location).Path
$taskOwned = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-owned-paths.txt') | Where-Object { $_ })
$taskAll = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
    @(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskAllHashes = @($taskAll | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($taskRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
$taskScope = [ordered]@{
    kind='V1-49-review2-scope';head=(git rev-parse HEAD).Trim();createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
    owned=@($taskOwned | ForEach-Object {
        [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}
    });
    sourceHashes=$taskAllHashes
}
$taskScope | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket49-review2-scope.json') -Encoding utf8
[ordered]@{head=$taskScope.head;ownedCount=$taskOwned.Count;sourceCount=$taskAllHashes.Count} | ConvertTo-Json
