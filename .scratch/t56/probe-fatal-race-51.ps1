$ErrorActionPreference='Stop'
$taskRun='F:\si56race51'
if(Test-Path -LiteralPath $taskRun){throw 'Fresh evidence directory required.'}
$taskObservations=@()
foreach($taskVariant in @('negative','positive')) {
    $taskRoot=Join-Path $taskRun $taskVariant
    [void][IO.Directory]::CreateDirectory($taskRoot)
    foreach($taskName in @('Consumer.csproj','Program.cs','NuGet.Config')) {
        Copy-Item -LiteralPath (Join-Path 'F:\si56pre49\diagnostics-consumer' $taskName) -Destination (Join-Path $taskRoot $taskName)
    }
    Copy-Item -LiteralPath '.scratch/t56/DiagnosticFatalProbe-positive51.cs.snapshot' -Destination (Join-Path $taskRoot 'DiagnosticFatalProbe.cs')
    if($taskVariant -eq 'positive') {
        # Preliminary source-level control only: the final packed WPF must be tested again.
        Copy-Item -LiteralPath '.scratch/t56/ManagedFaultShutdown-positive51.cs.snapshot' -Destination (Join-Path $taskRoot 'ManagedFaultShutdown.cs')
    }
    $taskProject=Join-Path $taskRoot 'Consumer.csproj'
    & dotnet restore $taskProject --configfile (Join-Path $taskRoot 'NuGet.Config') --packages (Join-Path $taskRoot 'packages') *> (Join-Path $taskRoot 'restore.log')
    if($LASTEXITCODE -ne 0){throw 'Control restore failed.'}
    & dotnet build $taskProject -c Release --no-restore --no-incremental -m:1 *> (Join-Path $taskRoot 'build.log')
    if($LASTEXITCODE -ne 0){throw 'Control build failed.'}
    foreach($taskMode in @('dispatcher','domain','hang','domain-race','process-exit')) {
        $taskManifest=Join-Path $taskRoot ($taskMode+'.json')
        $taskStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskRoot 'bin/Release/net6.0-windows/Consumer.exe'))
        $taskStart.UseShellExecute=$false; $taskStart.CreateNoWindow=$true
        $taskStart.RedirectStandardOutput=$true; $taskStart.RedirectStandardError=$true
        $taskStart.ArgumentList.Add($taskManifest); $taskStart.ArgumentList.Add($taskMode)
        $taskProcess=[Diagnostics.Process]::Start($taskStart)
        try {
            $taskStdout=$taskProcess.StandardOutput.ReadToEndAsync(); $taskStderr=$taskProcess.StandardError.ReadToEndAsync()
            if(-not $taskProcess.WaitForExit(15000)){$taskProcess.Kill($true);throw 'Control process deadline.'}
            $taskLog=$taskStdout.GetAwaiter().GetResult()+$taskStderr.GetAwaiter().GetResult()
            Set-Content -LiteralPath (Join-Path $taskRoot ($taskMode+'.log')) -Value $taskLog -Encoding utf8
            $taskEvidence=Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
            $taskRow=[ordered]@{variant=$taskVariant;mode=$taskMode;exitCode=$taskProcess.ExitCode;secretLeaked=$taskLog.Contains('SECRET-BAIT');calls=$taskEvidence.Calls;lifecycle=$taskEvidence.Lifecycle;ready=$taskEvidence.Ready;arm=$taskEvidence.Arm;firstExitObserved=(Test-Path -LiteralPath ($taskManifest+'.exit-started'));domainRaised=(Test-Path -LiteralPath ($taskManifest+'.domain-raised'));exitCalls=$null}
            if(Test-Path -LiteralPath ($taskManifest+'.exit-calls')){$taskRow.exitCalls=[int](Get-Content -LiteralPath ($taskManifest+'.exit-calls'))}
            $taskObservations += $taskRow
            $taskRow | ConvertTo-Json -Compress | Write-Output
        } finally {$taskProcess.Dispose()}
    }
}
$taskNegative=@($taskObservations | Where-Object { $_.variant -eq 'negative' -and $_.mode -eq 'domain-race' })[0]
$taskPositive=@($taskObservations | Where-Object variant -eq 'positive')
if(-not $taskNegative.secretLeaked -or $taskNegative.exitCode -eq 1 -or -not $taskNegative.firstExitObserved -or -not $taskNegative.domainRaised){throw 'Negative race did not reproduce.'}
if(@($taskPositive | Where-Object {$_.secretLeaked -or $_.exitCode -ne 1 -or $_.calls -ne 1 -or $_.lifecycle -cne 'Stopped' -or $_.ready -ne $false -or $_.arm -cne 'Disarmed'}).Count){throw 'Positive contract failed.'}
$taskRace=@($taskPositive | Where-Object mode -eq 'domain-race')[0]
if($taskRace.exitCalls -ne 2 -or -not $taskRace.firstExitObserved -or -not $taskRace.domainRaised){throw 'Positive race coverage incomplete.'}
[ordered]@{result='Pass';scope='Preliminary source-level negative/positive control, final packages NotRun';runs=$taskObservations;runtimeHash=(Get-FileHash -LiteralPath 'F:\si56pre49\diagnostics-consumer\bin\Release\net6.0-windows\SharpInspect.Runtime.dll').Hash;positiveSourceHash=(Get-FileHash -LiteralPath '.scratch/t56/ManagedFaultShutdown-positive51.cs.snapshot').Hash} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8

