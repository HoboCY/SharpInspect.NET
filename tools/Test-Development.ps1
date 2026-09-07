param([ValidateRange(1,76)][int]$Ticket = 1)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskRun = Join-Path $taskRepo (('artifacts\ticket{0:D2}\' -f $Ticket) + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($taskRun)

function Invoke-TaskDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $taskRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}

function Test-TaskCloudRootRejection([string]$ConsumerDll, [string]$Database, [string]$Manifest) {
    $taskBefore = (Get-FileHash -LiteralPath $Database -Algorithm SHA256).Hash
    $taskStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    foreach ($taskArgument in @($ConsumerDll,'--trace-db',$Database,'--verify-trace',$Manifest)) {
        $taskStart.ArgumentList.Add($taskArgument)
    }
    # Only the child sees this synthetic sync root. The host environment and registry are unchanged.
    [void]$taskStart.Environment.Remove('OneDrive')
    $taskStart.Environment.Add('ONEDRIVE', (Split-Path -Parent $Database))
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskErr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(15000)) {
            $taskProcess.Kill($true)
            throw 'Cloud-root negative probe exceeded its process deadline.'
        }
        $taskOutput = $taskOut.GetAwaiter().GetResult() + $taskErr.GetAwaiter().GetResult()
        $taskOutput | Set-Content -LiteralPath (Join-Path $taskRun 'consumer-cloud-root.log') -Encoding utf8
        if ($taskProcess.ExitCode -ne 1 -or $taskOutput -notmatch 'StorePathCloudSynchronized') {
            throw 'A database under the child process ONEDRIVE root was not rejected.'
        }
        if ((Get-FileHash -LiteralPath $Database -Algorithm SHA256).Hash -ne $taskBefore) {
            throw 'Cloud-root rejection changed the authoritative database bytes.'
        }
        Write-Output 'V102-P03 uppercase cloud root rejection PASS database=unchanged'
    }
    finally { $taskProcess.Dispose() }
}

function Get-TaskSourceHashes {
    $taskFiles = @(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File) +
        @(Get-Item -LiteralPath 'Directory.Build.props','global.json','SharpInspect.NET.sln')
    $taskFiles | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{ path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
}

Push-Location -LiteralPath $taskRepo
try {
    $taskOs = Get-CimInstance Win32_OperatingSystem
    $taskEvidence = [ordered]@{
        kind = ('V1-{0:D2}-development-validation' -f $Ticket)
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        startingHead = (& git rev-parse HEAD)
        os = [ordered]@{ caption=$taskOs.Caption; build=$taskOs.BuildNumber; architecture=$taskOs.OSArchitecture; sku=$taskOs.OperatingSystemSKU }
        sdk = (& dotnet --version)
        runtimes = @(& dotnet --list-runtimes)
        sourceHashes = @()
        result = 'NotRun'
    }
    $taskEvidence.sourceHashes = @(Get-TaskSourceHashes)
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    Invoke-TaskDotnet 'build.log' @('build','SharpInspect.NET.sln','-c','Release','-p:RestoreLockedMode=true')
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))

    $taskFeed = Join-Path $taskRun 'packages'
    foreach ($taskName in @('Abstractions','Runtime','Wpf')) {
        Invoke-TaskDotnet ('pack-' + $taskName + '.log') @('pack',"src/SharpInspect.$taskName/SharpInspect.$taskName.csproj",
            '-c','Release','--no-build','--no-restore','--output',$taskFeed)
    }

    $taskConsumer = Join-Path $taskRun 'consumer'
    [void][IO.Directory]::CreateDirectory($taskConsumer)
    $taskSample = Join-Path $taskRepo 'samples\SharpInspect.SampleHost'
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskSample -Recurse -File) {
        $taskRelative = [IO.Path]::GetRelativePath($taskSample,$taskFile.FullName)
        if ($taskRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
        $taskDestination = Join-Path $taskConsumer $taskRelative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
    Copy-Item -LiteralPath (Join-Path $taskRepo 'Directory.Build.props') -Destination (Join-Path $taskConsumer 'Directory.Build.props')
    $taskConsumerProject = Join-Path $taskConsumer 'SharpInspect.SampleHost.csproj'
    $taskNugetConfig = Join-Path $taskConsumer 'NuGet.Config'
    $taskConfigXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
        [Security.SecurityElement]::Escape($taskFeed) +
        '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
    [IO.File]::WriteAllText($taskNugetConfig, $taskConfigXml, [Text.UTF8Encoding]::new($false))
    Invoke-TaskDotnet 'consumer-restore.log' @('restore',$taskConsumerProject,'-p:UseLocalPackages=true',
        '--configfile',$taskNugetConfig,'--packages',(Join-Path $taskRun 'consumer-cache'))
    Invoke-TaskDotnet 'consumer-build.log' @('build',$taskConsumerProject,'-c','Release','-p:UseLocalPackages=true','--no-restore')
    $taskConsumerDll = Join-Path $taskConsumer 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
    $taskDatabase = Join-Path $taskRun 'trace\station.sqlite'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDatabase))
    $taskTraceManifest = Join-Path $taskRun 'trace-manifest.json'
    $taskAuditArguments = @()
    if ($Ticket -ge 3) {
        $taskAuditKeyName = 'SharpInspect.DevelopmentValidation.' + [Guid]::NewGuid().ToString('N')
        $taskAuditKeyDirectory = Join-Path $taskRun 'private-keys'
        $taskAuditArguments = @('--audit-key',$taskAuditKeyName,'--audit-key-directory',$taskAuditKeyDirectory)
        $taskAuditKeyName | Set-Content -LiteralPath (Join-Path $taskRun 'development-key-identity.txt') -Encoding utf8
    }
    Invoke-TaskDotnet 'consumer-smoke.log' (@($taskConsumerDll,'--smoke','--screenshot',(Join-Path $taskRun 'consumer-window.png'),
        '--trace-db',$taskDatabase,'--trace-manifest',$taskTraceManifest) + $taskAuditArguments)
    Invoke-TaskDotnet 'consumer-restart.log' (@($taskConsumerDll,'--trace-db',$taskDatabase,'--verify-trace',$taskTraceManifest) + $taskAuditArguments)
    Test-TaskCloudRootRejection $taskConsumerDll $taskDatabase $taskTraceManifest
    $taskFinalHashes = @(Get-TaskSourceHashes)
    if (($taskFinalHashes | ConvertTo-Json -Depth 4 -Compress) -cne
        ($taskEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Source path set or content changed during validation; results are not bound to a single source state.'
    }
    $taskEvidence.result = 'Pass'
    $taskEvidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    Write-Output "V1 TICKET $Ticket VALIDATION PASS: $taskRun"
}
catch {
    if ($null -ne $taskEvidence) {
        $taskEvidence.result = 'Fail'
        $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    }
    throw
}
finally {
    if ($taskAuditKeyName) {
        $taskKeyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskAuditKeyName)))
        $taskOwnedKeyPath = [IO.Path]::GetFullPath((Join-Path $taskAuditKeyDirectory ($taskKeyHash + '.key')))
        if (-not $taskOwnedKeyPath.StartsWith(([IO.Path]::GetFullPath($taskRun) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Disposable key path escaped the validation directory.'
        }
        # Remove only this run's disposable protected test key; public evidence remains.
        if (Test-Path -LiteralPath $taskOwnedKeyPath) { Remove-Item -LiteralPath $taskOwnedKeyPath }
    }
    Pop-Location
}
