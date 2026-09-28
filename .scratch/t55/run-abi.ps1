param([Parameter(Mandatory)][string]$PackageFeed, [string]$ArtifactRoot='F:\si55abi')
$ErrorActionPreference='Stop'
$taskLegacy='E:\s54abi\legacy-abi-consumer'
$taskTarget=Join-Path $ArtifactRoot 'legacy-abi-consumer'
if(Test-Path -LiteralPath $taskTarget) {throw 'ABI output must be a fresh directory.'}
[void][IO.Directory]::CreateDirectory($taskTarget)
foreach($taskFile in Get-ChildItem -LiteralPath $taskLegacy -File) {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
}
$taskExpected='C7F96671764520D741E348719325755646E6C43240FC772B47ABCEEAE76037E1'
$taskConsumer=Join-Path $taskTarget 'AbiConsumer.dll'
if((Get-FileHash -LiteralPath $taskConsumer).Hash -cne $taskExpected) {throw 'Legacy consumer identity changed.'}
$taskLibraries=foreach($taskName in 'Abstractions','Runtime','Wpf','Cameras.Virtual') {
    $taskLibrary='SharpInspect.'+$taskName+'.dll'
    $taskPackage='SharpInspect.NET.'+$taskName+'.0.1.0-dev.1.nupkg'
    $taskEntryName=if($taskName -eq 'Wpf') {'lib/net6.0-windows7.0/'+$taskLibrary} else {'lib/net6.0/'+$taskLibrary}
    $taskZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $PackageFeed $taskPackage))
    try {
        $taskEntry=$taskZip.GetEntry($taskEntryName)
        if($null -eq $taskEntry) {throw 'Expected package assembly missing.'}
        $taskInput=$taskEntry.Open()
        try {
            $taskOutput=[IO.File]::Create((Join-Path $taskTarget $taskLibrary))
            try {$taskInput.CopyTo($taskOutput)} finally {$taskOutput.Dispose()}
        } finally {$taskInput.Dispose()}
    } finally {$taskZip.Dispose()}
    [ordered]@{library=$taskLibrary;sha256=(Get-FileHash -LiteralPath (Join-Path $taskTarget $taskLibrary)).Hash;
        package=$taskPackage;entry=$taskEntryName}
}
Push-Location -LiteralPath $taskTarget
try {
    & dotnet AbiConsumer.dll *> (Join-Path $ArtifactRoot 'abi.log')
    if($LASTEXITCODE -ne 0) {throw 'Legacy ABI consumer failed.'}
} finally {Pop-Location}
if((Get-Content -LiteralPath (Join-Path $ArtifactRoot 'abi.log') -Raw) -notmatch 'PASS') {throw 'ABI completion evidence missing.'}
if((Get-FileHash -LiteralPath $taskConsumer).Hash -cne $taskExpected) {throw 'Consumer was modified.'}
[ordered]@{result='Pass';consumerUnchanged=$true;consumerSha256=$taskExpected;libraries=@($taskLibraries);
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $ArtifactRoot 'abi-package-binding.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $ArtifactRoot 'abi.log')
