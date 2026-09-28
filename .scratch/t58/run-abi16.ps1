param([Parameter(Mandatory)][string]$PackageFeed)
$ErrorActionPreference = 'Stop'
$taskSource = 'F:\si57abi\legacy-abi-consumer'
$taskRoot = 'F:\si58abi16'
$taskConsumer = Join-Path $taskRoot 'legacy-abi-consumer'
$taskExpected = 'C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1'
if (Test-Path -LiteralPath $taskRoot) { throw 'Use a fresh ABI evidence directory.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskSource 'AbiConsumer.dll')).Hash -cne $taskExpected) {
    throw 'The preserved consumer binary changed.'
}
[void][IO.Directory]::CreateDirectory($taskRoot)
Copy-Item -LiteralPath $taskSource -Destination $taskConsumer -Recurse
$taskBindings = @()
foreach ($taskLibrary in 'Abstractions','Runtime','Wpf','Cameras.Virtual') {
    $taskPackage = Join-Path $PackageFeed ('SharpInspect.NET.' + $taskLibrary + '.0.1.0-dev.1.nupkg')
    $taskDll = 'SharpInspect.' + $taskLibrary + '.dll'
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskPackage)
    try {
        $taskEntries = @($taskZip.Entries | Where-Object { $_.FullName -like 'lib/*' -and $_.Name -ceq $taskDll })
        if ($taskEntries.Count -ne 1) { throw 'Ambiguous package assembly.' }
        $taskEntry = $taskEntries[0]
        $taskInput = $taskEntry.Open()
        $taskOutput = [IO.File]::Create((Join-Path $taskConsumer $taskDll))
        try { $taskInput.CopyTo($taskOutput) } finally { $taskOutput.Dispose(); $taskInput.Dispose() }
        $taskBindings += @{library=$taskDll;package=$taskPackage;entry=$taskEntry.FullName;
            sha256=(Get-FileHash -LiteralPath (Join-Path $taskConsumer $taskDll)).Hash}
    } finally { $taskZip.Dispose() }
}
$env:TEMP='F:\t58'; $env:TMP=$env:TEMP
& dotnet (Join-Path $taskConsumer 'AbiConsumer.dll') *> (Join-Path $taskRoot 'abi.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskRoot 'abi.log'); throw 'Legacy ABI consumer failed.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskConsumer 'AbiConsumer.dll')).Hash -cne $taskExpected) {
    throw 'ABI consumer was changed during validation.'
}
@{result='Pass';consumerUnchanged=$true;consumerSha256=$taskExpected;libraries=$taskBindings} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'abi-package-binding.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRoot 'abi.log')
