$ErrorActionPreference='Stop'
$taskRoot='F:\si56pre48'
if(Test-Path -LiteralPath $taskRoot){throw 'Fresh evidence root required.'}
[void][IO.Directory]::CreateDirectory($taskRoot)
$env:TEMP='F:\temp\SharpInspect-T56'
$env:TMP=$env:TEMP
$taskRuntimeBefore=(Get-FileHash -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll').Hash
foreach($taskName in @('Abstractions','Runtime','Wpf','OpenCvSharp','Cameras.Virtual','Cameras.Hikrobot','Cameras.Conformance','Calibration.OpenCvSharp')) {
    & dotnet pack ('src/SharpInspect.'+$taskName+'/SharpInspect.'+$taskName+'.csproj') -c Release --no-build --no-restore --output (Join-Path $taskRoot 'packages') *> (Join-Path $taskRoot ('pack-'+$taskName+'.log'))
    if($LASTEXITCODE -ne 0){throw ('Pack failed: '+$taskName)}
}
& tools/Test-DiagnosticsConsumer.ps1 -Run $taskRoot -PackageFeed (Join-Path $taskRoot 'packages') -DependencyFeed 'E:\nuget\.nuget\packages'
if((Get-FileHash -LiteralPath 'src/SharpInspect.Runtime/bin/Release/net6.0/SharpInspect.Runtime.dll').Hash -cne $taskRuntimeBefore){throw 'Runtime DLL changed.'}
Write-Output 'Preliminary packaged consumer complete; full solution validation remains independent.'
