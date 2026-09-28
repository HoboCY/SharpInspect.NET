$ErrorActionPreference = 'Stop'
$taskRoot = 'E:\temp\SharpInspect.NET-validation-artifacts\ticket42'
$taskOriginal = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket33\abi-consumer\bin\Debug\net6.0-windows'
$taskCopy = Join-Path $taskRoot 'abi-consumer-copy'
if (Test-Path -LiteralPath $taskCopy) { throw 'ABI proof requires a fresh isolated copy.' }
[void][IO.Directory]::CreateDirectory($taskCopy)
Get-ChildItem -LiteralPath $taskOriginal -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskCopy }
$taskConsumer = Join-Path $taskCopy 'AbiConsumer.dll'
$taskBefore = (Get-FileHash -LiteralPath $taskConsumer -Algorithm SHA256).Hash
if ($taskBefore -cne 'C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1') {
    throw 'The archived T32-compiled consumer changed.'
}
$taskLibraries = @()
foreach ($taskName in 'Abstractions','Runtime','Wpf','Cameras.Virtual') {
    $taskFramework = if ($taskName -eq 'Wpf') { 'net6.0-windows' } else { 'net6.0' }
    $taskLibrary = Join-Path (Get-Location).Path ('src/SharpInspect.' + $taskName + '/bin/Release/' + $taskFramework + '/SharpInspect.' + $taskName + '.dll')
    Copy-Item -LiteralPath $taskLibrary -Destination $taskCopy
    $taskLibraries += [ordered]@{name=[IO.Path]::GetFileName($taskLibrary);sha256=(Get-FileHash -LiteralPath $taskLibrary).Hash}
}
$env:TEMP = Join-Path $taskRoot 'process-temp'
$env:TMP = $env:TEMP
& dotnet $taskConsumer *> (Join-Path $taskRoot '49-legacy-abi.log')
$taskExit = $LASTEXITCODE
$taskAfter = (Get-FileHash -LiteralPath $taskConsumer).Hash
[ordered]@{compiledAgainst='T32 archived DLLs';consumerBefore=$taskBefore;consumerAfter=$taskAfter;
    consumerUnchanged=($taskBefore -ceq $taskAfter);exitCode=$taskExit;libraries=$taskLibraries} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot '49-legacy-abi-proof.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRoot '49-legacy-abi.log')
if ($taskExit -ne 0 -or $taskBefore -cne $taskAfter) { throw 'Legacy consumer ABI proof failed.' }


