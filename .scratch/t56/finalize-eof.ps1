param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference='Stop'
$taskManifest=Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
if($taskManifest.result -cne 'Pass'){throw 'Complete behavioral validation before formatting.'}
$taskTargets=@('samples\SharpInspect.SampleHost\DiagnosticFatalProbe.cs','tests\SharpInspect.Runtime.Tests\ProductionStopCrashTests.cs','tools\Test-DiagnosticsConsumer.ps1')
$taskRoot='.scratch/t56/eof-finalization'
if(Test-Path -LiteralPath $taskRoot){throw 'Formatting evidence already exists.'}
[void][IO.Directory]::CreateDirectory($taskRoot)
$taskRows=@()
foreach($taskPath in $taskTargets){
    $taskExpected=@($taskManifest.sourceHashes | Where-Object path -ceq $taskPath)
    if($taskExpected.Count -ne 1 -or (Get-FileHash -LiteralPath $taskPath).Hash -cne $taskExpected[0].sha256){throw 'Formatting input differs from validated source.'}
    $taskBytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $taskPath))
    $taskText=[Text.Encoding]::UTF8.GetString($taskBytes)
    if($taskText -notmatch '(\r?\n){2,}\z'){throw 'Expected extra trailing blank line missing.'}
    $taskFinal=[Text.Encoding]::UTF8.GetBytes($taskText.TrimEnd([char[]]"`r`n")+"`n")
    $taskSnapshot=Join-Path $taskRoot ([IO.Path]::GetFileName($taskPath)+'.validated-source')
    [IO.File]::WriteAllBytes((Join-Path (Get-Location).Path $taskSnapshot),$taskBytes)
    [IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $taskPath),$taskFinal)
    $taskRows += [ordered]@{path=$taskPath;validatedSha256=$taskExpected[0].sha256;finalSha256=(Get-FileHash -LiteralPath $taskPath).Hash;validatedSource=$taskSnapshot;operation='Remove trailing empty line only; preserve every byte before final line endings'}
}
[ordered]@{kind='T56-post-validation-EOF-formatting';run=$Run;behavioralRetest='NotRequiredForEOFOnly';files=$taskRows} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'normalization.json') -Encoding utf8
