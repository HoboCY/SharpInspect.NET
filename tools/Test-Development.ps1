param([ValidateRange(1,76)][int]$Ticket = 1)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskRun = Join-Path $taskRepo (('artifacts\ticket{0:D2}\' -f $Ticket) + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($taskRun)

function Invoke-TaskDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $taskRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}

function Write-TaskIdentityPolicy([string]$Path) {
    $taskBlocklistId = 'development-fixture'
    $taskBlocklistVersion = 'v1'
    $taskValues = [string[]]@('passwordpassword','123456789012345')
    [Array]::Sort($taskValues, [StringComparer]::Ordinal)
    $taskBytes = [IO.MemoryStream]::new()
    function Write-TaskInt32([int]$Value) {
        $taskInteger = [BitConverter]::GetBytes($Value)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($taskInteger) }
        $taskBytes.Write($taskInteger,0,4)
    }
    function Write-TaskString([string]$Value) {
        $taskUtf8 = [Text.Encoding]::UTF8.GetBytes($Value)
        Write-TaskInt32 $taskUtf8.Length
        $taskBytes.Write($taskUtf8,0,$taskUtf8.Length)
    }
    Write-TaskString $taskBlocklistId
    Write-TaskString $taskBlocklistVersion
    Write-TaskInt32 $taskValues.Length
    foreach ($taskValue in $taskValues) { Write-TaskString $taskValue }
    $taskHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskBytes.ToArray()))
    $taskBytes.Dispose()
    [ordered]@{ PasswordPolicyVersion='development-2026-09'; BlocklistId=$taskBlocklistId; BlocklistVersion=$taskBlocklistVersion;
        BlocklistContentHash=$taskHash; BlocklistValues=$taskValues; HashBaselineVersion='development-2026-09'; WorkFactor=600000;
        AuthenticationPolicy=[ordered]@{ Id='development'; Version='development-2026-09'; AccountFailureLimit=10; StationFailureLimit=50;
            InitialDelay='00:00:01'; MaximumDelay='00:15:00'; SessionIdleTimeout='00:15:00'; StepUpFreshness='00:05:00' };
        AuthorizationPolicy=[ordered]@{Id='development'; Version='development-2026-09';
            RoleBundles=[ordered]@{Operator=@(5,6); Technician=@(5,6,7,11,13,23); Administrator=@(1..28)};
            StepUpPermissions=@(1..28 | Where-Object { $_ -notin 5,6 }) } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8
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
    if ($Ticket -ge 4) {
        $taskPreviousIdentityConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_IDENTITY_CONSUMER','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_IDENTITY_CONSUMER',$taskConsumerDll,'Process')
            Invoke-TaskDotnet 'identity-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~IdentityConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'identity-consumer-tests'))
        }
        finally { [Environment]::SetEnvironmentVariable('SHARPINSPECT_IDENTITY_CONSUMER',$taskPreviousIdentityConsumer,'Process') }
    }
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
    if ($Ticket -ge 4) {
        $taskIdentityPolicy = Join-Path $taskRun 'development-identity-policy.json'
        Write-TaskIdentityPolicy $taskIdentityPolicy
        $taskAuditArguments += @('--identity-policy',$taskIdentityPolicy)
    }
    Invoke-TaskDotnet 'consumer-smoke.log' (@($taskConsumerDll,'--smoke','--screenshot',(Join-Path $taskRun 'consumer-window.png'),
        '--trace-db',$taskDatabase,'--trace-manifest',$taskTraceManifest) + $taskAuditArguments)
    Invoke-TaskDotnet 'consumer-restart.log' (@($taskConsumerDll,'--trace-db',$taskDatabase,'--verify-trace',$taskTraceManifest) + $taskAuditArguments)
    Test-TaskCloudRootRejection $taskConsumerDll $taskDatabase $taskTraceManifest
    if ($Ticket -ge 7) {
        $taskConformanceRoot = Join-Path $taskRun 'conformance-demo'
        [void][IO.Directory]::CreateDirectory($taskConformanceRoot)
        $taskConformanceSource = (& git rev-parse HEAD).Trim()
        Invoke-TaskDotnet 'conformance-demo.log' @($taskConsumerDll,'--conformance-demo',
            $taskConformanceRoot,'--conformance-source',$taskConformanceSource)
        Invoke-TaskDotnet 'conformance-query.log' @($taskConsumerDll,'--conformance-query',$taskConformanceRoot)
        $taskConformanceDemoOutput = Get-Content -LiteralPath (Join-Path $taskRun 'conformance-demo.log') -Raw
        $taskConformanceQueryOutput = Get-Content -LiteralPath (Join-Path $taskRun 'conformance-query.log') -Raw
        if ($taskConformanceDemoOutput -notmatch 'V107-P01 conformance-demo PASS' -or
            $taskConformanceDemoOutput -notmatch 'preflightNotRun=true' -or
            $taskConformanceQueryOutput -notmatch 'V107-P02 conformance-query PASS' -or
            $taskConformanceQueryOutput -notmatch 'historicalFail=true' -or
            $taskConformanceQueryOutput -notmatch 'databaseUnchanged=true') {
            throw 'The independent consumer conformance demo/query did not prove the required markers.'
        }
        foreach ($taskConformanceFile in @('conformance.sqlite','conformance.sqlite.anchor',
                'candidate-manifest.json','conformance-manifest.json','conformance-summary.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $taskConformanceRoot $taskConformanceFile))) {
                throw "Conformance evidence file is missing: $taskConformanceFile"
            }
        }
        $taskConformanceKeys = @(Get-ChildItem -LiteralPath (Join-Path $taskConformanceRoot 'private-key') -Filter '*.key' -File)
        if ($taskConformanceKeys.Count -ne 1) { throw 'Conformance evidence signing key is missing or not bounded to one file.' }
        Write-Output "V107-P03 independent-process consumer conformance PASS: $taskConformanceRoot"
    }
    if ($Ticket -ge 8) {
        # This process reuses the isolated trace store and policy from the preceding consumer
        # smoke.  It exercises only the public, fail-closed recovery boundary; physical stop and
        # successful recovery remain Runtime fixture responsibilities.
        Invoke-TaskDotnet 'administrator-recovery-consumer.log' (@($taskConsumerDll,
                '--administrator-recovery-check','--trace-db',$taskDatabase) + $taskAuditArguments)
        $taskRecoveryOutput = Get-Content -LiteralPath (Join-Path $taskRun 'administrator-recovery-consumer.log') -Raw
        if ($taskRecoveryOutput -notmatch 'V108-P01 administrator-recovery-consumer PASS' -or
            $taskRecoveryOutput -notmatch 'ready=false' -or
            $taskRecoveryOutput -notmatch 'recoveryAvailable=false' -or
            $taskRecoveryOutput -notmatch 'kitDelivered=false' -or
            $taskRecoveryOutput -notmatch 'physicalStop=NotRun') {
            throw 'The independent consumer administrator-recovery check did not prove the closed path.'
        }
        Write-Output 'V108-P02 independent-process consumer administrator-recovery closed path PASS'
    }
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
