param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskOriginal = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket33\abi-consumer\bin\Debug\net6.0-windows'
$taskCopy = Join-Path $Run 'legacy-abi-consumer'
if (Test-Path -LiteralPath $taskCopy) { throw 'Use a fresh ABI validation destination.' }
[void][IO.Directory]::CreateDirectory($taskCopy)
Get-ChildItem -LiteralPath $taskOriginal -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskCopy }
$taskConsumer = Join-Path $taskCopy 'AbiConsumer.dll'
$taskBefore = (Get-FileHash -LiteralPath $taskConsumer).Hash
if ($taskBefore -cne 'C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1') {
    throw 'Archived consumer bytes changed.'
}
$taskLibraries = @()
foreach ($taskName in 'Abstractions','Runtime','Wpf','Cameras.Virtual') {
    $taskFramework = if ($taskName -eq 'Wpf') { 'net6.0-windows' } else { 'net6.0' }
    $taskLibrary = Join-Path (Get-Location).Path ('src/SharpInspect.' + $taskName + '/bin/Release/' + $taskFramework + '/SharpInspect.' + $taskName + '.dll')
    $taskHash = (Get-FileHash -LiteralPath $taskLibrary).Hash
    $taskPackage = Join-Path $Run ('packages/SharpInspect.NET.' + $taskName + '.0.1.0-dev.1.nupkg')
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskPackage)
    try {
        $taskEntry = @($taskZip.Entries | Where-Object FullName -Like ('lib/*/SharpInspect.' + $taskName + '.dll'))
        if ($taskEntry.Count -ne 1) { throw 'Ambiguous candidate package library.' }
        $taskStream = $taskEntry[0].Open()
        try { $taskPackageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)) }
        finally { $taskStream.Dispose() }
        if ($taskPackageHash -cne $taskHash) { throw 'Candidate package does not match tested library.' }
        $taskEntryPath = $taskEntry[0].FullName
    }
    finally { $taskZip.Dispose() }
    Copy-Item -LiteralPath $taskLibrary -Destination $taskCopy
    $taskLibraries += [ordered]@{library=[IO.Path]::GetFileName($taskLibrary);sha256=$taskHash;
        package=[IO.Path]::GetFileName($taskPackage);entry=$taskEntryPath}
}
& dotnet $taskConsumer *> (Join-Path $Run 'legacy-abi.log')
$taskExit = $LASTEXITCODE
$taskAfter = (Get-FileHash -LiteralPath $taskConsumer).Hash
if ($taskExit -ne 0 -or $taskAfter -cne $taskBefore) { throw 'Legacy ABI consumer failed or changed.' }
[ordered]@{result='Pass';consumerSha256=$taskBefore;consumerUnchanged=$true;libraries=$taskLibraries} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Run 'abi-package-binding.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $Run 'legacy-abi.log')
