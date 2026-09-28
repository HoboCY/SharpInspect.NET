$ErrorActionPreference='Stop'
$taskRoot='F:\si58abi16'
$taskConsumer=Join-Path $taskRoot 'legacy-abi-consumer'
$taskOld='F:\si57abi\legacy-abi-consumer'
$taskExpected='C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1'
if(Test-Path -LiteralPath $taskRoot){throw 'Fresh ABI directory required.'}
if((Get-FileHash -LiteralPath (Join-Path $taskOld 'AbiConsumer.dll')).Hash -cne $taskExpected){throw 'Preserved consumer changed.'}
[void][IO.Directory]::CreateDirectory($taskRoot)
Copy-Item -LiteralPath $taskOld -Destination $taskConsumer -Recurse
$taskBindings=@()
foreach($taskLibrary in 'Abstractions','Runtime','Wpf','Cameras.Virtual'){
    $taskFramework=if($taskLibrary -eq 'Wpf'){'net6.0-windows'}else{'net6.0'}
    $taskSource=Join-Path (Get-Location) ('src/SharpInspect.'+$taskLibrary+'/bin/Release/'+$taskFramework+'/SharpInspect.'+$taskLibrary+'.dll')
    $taskDestination=Join-Path $taskConsumer ('SharpInspect.'+$taskLibrary+'.dll')
    Copy-Item -LiteralPath $taskSource -Destination $taskDestination -Force
    $taskHash=(Get-FileHash -LiteralPath $taskSource).Hash
    if((Get-FileHash -LiteralPath $taskDestination).Hash -cne $taskHash){throw 'ABI candidate copy changed.'}
    $taskBindings+=@{library=('SharpInspect.'+$taskLibrary+'.dll');name=$taskLibrary;sha256=$taskHash}
}
$env:TEMP='F:\t58';$env:TMP=$env:TEMP
& dotnet (Join-Path $taskConsumer 'AbiConsumer.dll') *> (Join-Path $taskRoot 'abi.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $taskRoot 'abi.log');throw 'Legacy ABI failed.'}
if((Get-FileHash -LiteralPath (Join-Path $taskConsumer 'AbiConsumer.dll')).Hash -cne $taskExpected){throw 'Consumer changed.'}
@{result='ExecutedPassPackageBindingPending';consumerUnchanged=$true;consumerSha256=$taskExpected;libraries=$taskBindings} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'execution.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRoot 'abi.log')
