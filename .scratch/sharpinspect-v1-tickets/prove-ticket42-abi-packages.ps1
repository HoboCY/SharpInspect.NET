param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskAbi = Get-Content -LiteralPath 'E:\temp\SharpInspect.NET-validation-artifacts\ticket42\49-legacy-abi-proof.json' -Raw | ConvertFrom-Json
if ($taskAbi.exitCode -ne 0 -or -not $taskAbi.consumerUnchanged) { throw 'Legacy ABI has not passed.' }
$taskBindings = foreach ($taskLibrary in $taskAbi.libraries) {
    $taskPackageName = $taskLibrary.name.Replace('SharpInspect.', 'SharpInspect.NET.').Replace('.dll', '')
    $taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $Run 'packages') -Filter ($taskPackageName + '.*.nupkg') -File)
    if ($taskPackages.Count -ne 1) { throw ('Exact package missing: ' + $taskPackageName) }
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskPackages[0].FullName)
    try {
        $taskEntries = @($taskZip.Entries | Where-Object { $_.FullName.StartsWith('lib/') -and $_.Name -ceq $taskLibrary.name })
        if ($taskEntries.Count -ne 1) { throw ('Exact package assembly missing: ' + $taskLibrary.name) }
        $taskStream = $taskEntries[0].Open()
        try { $taskPackageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)) }
        finally { $taskStream.Dispose() }
        if ($taskPackageHash -cne $taskLibrary.sha256) { throw ('ABI library differs from validated package: ' + $taskLibrary.name) }
        [ordered]@{library=$taskLibrary.name;sha256=$taskLibrary.sha256;package=$taskPackages[0].Name;entry=$taskEntries[0].FullName}
    }
    finally { $taskZip.Dispose() }
}
[ordered]@{result='Pass';consumerSha256=$taskAbi.consumerAfter;consumerUnchanged=$true;libraries=@($taskBindings)} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Run 'abi-package-binding.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $Run 'abi-package-binding.json')

