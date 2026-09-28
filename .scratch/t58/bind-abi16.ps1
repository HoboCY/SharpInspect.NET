param([Parameter(Mandatory)][string]$PackageFeed)
$ErrorActionPreference='Stop'
$taskRoot='F:\si58abi16'
$taskExecution=Get-Content -LiteralPath (Join-Path $taskRoot 'execution.json') -Raw | ConvertFrom-Json
if($taskExecution.result -cne 'ExecutedPassPackageBindingPending'){throw 'Successful original ABI execution required.'}
if((Get-FileHash -LiteralPath (Join-Path $taskRoot 'legacy-abi-consumer/AbiConsumer.dll')).Hash -cne $taskExecution.consumerSha256){throw 'ABI consumer changed.'}
$taskBindings=@()
foreach($taskLibrary in $taskExecution.libraries){
    if((Get-FileHash -LiteralPath (Join-Path $taskRoot ('legacy-abi-consumer/'+$taskLibrary.library))).Hash -cne $taskLibrary.sha256){throw 'Executed assembly changed.'}
    $taskPackage='SharpInspect.NET.'+$taskLibrary.name+'.0.1.0-dev.1.nupkg'
    $taskZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $PackageFeed $taskPackage))
    try{
        $taskEntries=@($taskZip.Entries | Where-Object {$_.FullName -like 'lib/*' -and $_.Name -ceq $taskLibrary.library})
        if($taskEntries.Count -ne 1){throw 'Ambiguous packed assembly.'}
        $taskStream=$taskEntries[0].Open()
        try{$taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream))}finally{$taskStream.Dispose()}
        if($taskHash -cne $taskLibrary.sha256){throw 'Packed assembly differs from executed ABI candidate.'}
        $taskBindings+=@{library=$taskLibrary.library;package=$taskPackage;entry=$taskEntries[0].FullName;sha256=$taskHash}
    }finally{$taskZip.Dispose()}
}
@{result='Pass';consumerUnchanged=$true;consumerSha256=$taskExecution.consumerSha256;libraries=$taskBindings} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'abi-package-binding.json') -Encoding utf8
Write-Output 'Unchanged legacy ABI execution bound to all four current package assemblies.'
