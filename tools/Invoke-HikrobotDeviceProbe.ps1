param(
    [switch]$AllowDeviceAccess,
    [Parameter(Mandatory)][string]$RuntimeLibrary,
    [Parameter(Mandatory)][string]$RuntimeSha256,
    [Parameter(Mandatory)][string]$StableDeviceIdentity,
    [Parameter(Mandatory)][string]$ExpectedModel,
    [Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$ProbeDll = (Join-Path $PSScriptRoot '../samples/SharpInspect.Hikrobot.DeviceProbe/bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll'),
    [ValidateRange(15,120)][int]$MaximumProcessSeconds = 60
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceAccess) { throw 'HikrobotProbeExplicitDeviceAccessRequired' }
foreach ($taskFile in @($RuntimeLibrary,$Configuration,$ProbeDll)) {
    if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw 'HikrobotProbeInputFileMissing' }
}
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) {
    throw 'HikrobotProbeNewAbsoluteOutputRequired'
}
[void][IO.Directory]::CreateDirectory($OutputRoot)
$taskStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
foreach ($taskArgument in @([IO.Path]::GetFullPath($ProbeDll),'--allow-device-access',
    '--runtime',[IO.Path]::GetFullPath($RuntimeLibrary),'--runtime-sha256',$RuntimeSha256,
    '--device',$StableDeviceIdentity,'--model',$ExpectedModel,
    '--configuration',[IO.Path]::GetFullPath($Configuration),'--output',(Join-Path $OutputRoot 'device'))) {
    $taskStart.ArgumentList.Add($taskArgument)
}
$taskProcess = [Diagnostics.Process]::Start($taskStart)
$taskTimedOut = $false
try {
    $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskErr = $taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit($MaximumProcessSeconds * 1000)) {
        $taskTimedOut = $true
        $taskProcess.Kill($true)
        $taskProcess.WaitForExit()
    }
    $taskOut.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputRoot 'stdout.json') -Encoding utf8
    $taskErr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputRoot 'stderr.json') -Encoding utf8
    [ordered]@{ verificationId='V121-Q02'; deviceAccessRequested=$true; processExitCode=$taskProcess.ExitCode;
        processTimedOut=$taskTimedOut; hardwareQualification='NotRun'; productionReady=$false;
        runtimeSha256=$RuntimeSha256; probeSha256=(Get-FileHash -LiteralPath $ProbeDll -Algorithm SHA256).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'process.json') -Encoding utf8
    if ($taskTimedOut -or $taskProcess.ExitCode -ne 0) { throw 'HikrobotDeviceProbeFailedSeeEvidence' }
}
finally { $taskProcess.Dispose() }
