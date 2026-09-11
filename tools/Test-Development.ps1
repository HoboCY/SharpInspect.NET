param([ValidateRange(1,76)][int]$Ticket = 1, [string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskArtifactBase = Join-Path $taskRepo 'artifacts'
if (-not [string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot)) { throw 'ArtifactRoot must be an absolute path.' }
    $taskArtifactBase = [IO.Path]::GetFullPath($ArtifactRoot)
}
$taskRun = Join-Path $taskArtifactBase (('ticket{0:D2}\' -f $Ticket) + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$taskOverlayDirectory = $null
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
            RoleBundles=[ordered]@{Operator=@(5,6,29); Technician=@(5,6,7,11,13,23,29,30); Administrator=@(1..30)};
            StepUpPermissions=@(1..30 | Where-Object { $_ -notin 5,6,29 }) } } |
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
    Invoke-TaskDotnet 'build.log' @('build','SharpInspect.NET.sln','-c','Release','-p:RestoreLockedMode=true','-m:1')
    Invoke-TaskDotnet 'tests.log' @('test','SharpInspect.NET.sln','-c','Release','--no-build','--no-restore','-m:1',
        '--logger','trx','--results-directory',(Join-Path $taskRun 'tests'))

    $taskFeed = Join-Path $taskRun 'packages'
    $taskPackages = @('Abstractions','Runtime','Wpf','OpenCvSharp','Cameras.Virtual')
    if ($Ticket -ge 21) { $taskPackages += 'Cameras.Hikrobot' }
    if ($Ticket -ge 22) { $taskPackages += 'Cameras.Conformance' }
    if ($Ticket -ge 25) { $taskPackages += 'Calibration.OpenCvSharp' }
    foreach ($taskName in $taskPackages) {
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
    if ($Ticket -ge 15) {
        $taskPreviousDraftConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_DRAFT_CONSUMER','Process')
        $taskPreviousDraftEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_DRAFT_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_DRAFT_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_DRAFT_EVIDENCE_ROOT',(Join-Path $taskRun 'draft-demo'),'Process')
            Invoke-TaskDotnet 'draft-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~RecipeDraftConsumerAcceptanceTests|FullyQualifiedName~CustomEditorConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'draft-consumer-tests'))
            foreach ($taskDraftFile in @('evidence.json','draft-editor.png','draft-editor-dependencies.png','draft-restart.json','process.log','restart.log')) {
                $taskDraftArtifact = Join-Path $taskRun ('draft-demo/' + $taskDraftFile)
                if (-not (Test-Path -LiteralPath $taskDraftArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskDraftArtifact).Length -eq 0) {
                    throw "Draft consumer evidence is missing or empty: $taskDraftFile"
                }
            }
            foreach ($taskDraftMigrationFile in @('evidence.json','draft-migration-editor.png','draft-migration-evidence.json','draft-migration-restart.json','process.log','restart.log')) {
                $taskDraftMigrationArtifact = Join-Path $taskRun ('draft-demo/migration/' + $taskDraftMigrationFile)
                if (-not (Test-Path -LiteralPath $taskDraftMigrationArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskDraftMigrationArtifact).Length -eq 0) {
                    throw "Draft migration consumer evidence is missing or empty: $taskDraftMigrationFile"
                }
            }
            foreach ($taskCustomEditorFile in @('evidence.json','custom-editor.png','custom-editor-evidence.json','custom-editor-restart.json','process.log','restart.log')) {
                $taskCustomEditorArtifact = Join-Path $taskRun ('draft-demo/custom-editor/' + $taskCustomEditorFile)
                if (-not (Test-Path -LiteralPath $taskCustomEditorArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskCustomEditorArtifact).Length -eq 0) {
                    throw "Custom editor consumer evidence is missing or empty: $taskCustomEditorFile"
                }
            }
            $taskCustomEditorEvidence = Get-Content -LiteralPath (Join-Path $taskRun 'draft-demo/custom-editor/evidence.json') -Raw | ConvertFrom-Json
            if ($taskCustomEditorEvidence.Result -cne 'Pass' -or
                $taskCustomEditorEvidence.ExternalNuGetConsumer -cne $true -or
                $taskCustomEditorEvidence.ConsumerSha256 -cne (Get-FileHash -LiteralPath $taskConsumerDll).Hash -or
                $taskCustomEditorEvidence.IndependentRestart -cne $true -or
                $taskCustomEditorEvidence.DatabaseUnchangedByRestart -cne $true -or
                $taskCustomEditorEvidence.Revisions -ne 3 -or
                $taskCustomEditorEvidence.Published -cne $false -or
                $taskCustomEditorEvidence.Active -cne $false -or
                $taskCustomEditorEvidence.CanRelease -cne $false) {
                throw 'Custom editor consumer evidence failed its result, scope or artifact binding.'
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_DRAFT_CONSUMER',$taskPreviousDraftConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_DRAFT_EVIDENCE_ROOT',$taskPreviousDraftEvidence,'Process')
        }
    }
    if ($Ticket -ge 30) {
        $taskPreviousReleaseConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_RELEASE_CONSUMER','Process')
        $taskPreviousReleaseEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_RELEASE_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_RELEASE_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_RELEASE_EVIDENCE_ROOT',(Join-Path $taskRun 'release-demo'),'Process')
            Invoke-TaskDotnet 'recipe-release-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~RecipeReleaseConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'recipe-release-consumer-tests'))
            foreach ($taskReleaseCase in @('single','maker-checker','concurrent','dependency')) {
                $taskReleaseDirectory = Join-Path $taskRun ('release-demo/' + $taskReleaseCase)
                foreach ($taskReleaseFile in @('evidence.json','release-editor.png','release-evidence.json','release-restart.json','process.log','restart.log')) {
                    $taskReleaseArtifact = Join-Path $taskReleaseDirectory $taskReleaseFile
                    if (-not (Test-Path -LiteralPath $taskReleaseArtifact -PathType Leaf) -or
                        (Get-Item -LiteralPath $taskReleaseArtifact).Length -eq 0) {
                        throw "Recipe release consumer evidence is missing or empty: $taskReleaseCase/$taskReleaseFile"
                    }
                }
                $taskReleaseEvidence = Get-Content -LiteralPath (Join-Path $taskReleaseDirectory 'evidence.json') -Raw | ConvertFrom-Json
                $taskReleaseExpectedCount = if ($taskReleaseCase -ceq 'single') { 1 } else { 0 }
                $taskReleaseExpectedAvailable = $taskReleaseCase -ceq 'single'
                if ($taskReleaseEvidence.Result -cne 'Pass' -or
                    $taskReleaseEvidence.ExternalNuGetConsumer -cne $true -or
                    $taskReleaseEvidence.ConsumerSha256 -cne (Get-FileHash -LiteralPath $taskConsumerDll).Hash -or
                    $taskReleaseEvidence.IndependentRestart -cne $true -or
                    $taskReleaseEvidence.DatabaseUnchangedByRestart -cne $true -or
                    $taskReleaseEvidence.ReleasedVersions -ne $taskReleaseExpectedCount -or
                    $taskReleaseEvidence.Available -cne $taskReleaseExpectedAvailable -or
                    $taskReleaseEvidence.Active -cne $false -or
                    $taskReleaseEvidence.Armed -cne $false -or
                    $taskReleaseEvidence.ProductionReady -cne $false) {
                    throw "Recipe release consumer did not prove its exact outcome and read-only restart: $taskReleaseCase"
                }
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_RELEASE_CONSUMER',$taskPreviousReleaseConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_RELEASE_EVIDENCE_ROOT',$taskPreviousReleaseEvidence,'Process')
        }
    }
    if ($Ticket -ge 17) {
        $taskPreviousCameraConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_CONSUMER','Process')
        $taskPreviousCameraEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_EVIDENCE_ROOT',(Join-Path $taskRun 'camera-setup-demo'),'Process')
            Invoke-TaskDotnet 'camera-setup-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~CameraSetupConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'camera-setup-consumer-tests'))
            foreach ($taskCameraFile in @('evidence.json','camera-setup-evidence.json','camera-setup-restart.json',
                'camera-setup-applied.png','camera-setup-failed.png',
                'camera-setup-applied-readback.png','camera-setup-failed-readback.png','process.log','restart.log')) {
                $taskCameraArtifact = Join-Path $taskRun ('camera-setup-demo/' + $taskCameraFile)
                if (-not (Test-Path -LiteralPath $taskCameraArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskCameraArtifact).Length -eq 0) {
                    throw "Camera setup consumer evidence is missing or empty: $taskCameraFile"
                }
            }
            $taskCameraEvidence = Get-Content -LiteralPath (Join-Path $taskRun 'camera-setup-demo/evidence.json') -Raw | ConvertFrom-Json
            if ($taskCameraEvidence.Result -cne 'Pass' -or $taskCameraEvidence.ProductionReady -cne $false -or
                $taskCameraEvidence.IndependentRestart -cne $true -or $taskCameraEvidence.PhysicalDevices -cne 'NotRun' -or
                $taskCameraEvidence.ProviderQualification -cne 'NotRun' -or $taskCameraEvidence.StationAcceptance -cne 'NotRun' -or
                $taskCameraEvidence.ConsumerSha256 -cne (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash) {
                throw 'Camera setup consumer evidence failed its measured result or artifact binding.'
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_CONSUMER',$taskPreviousCameraConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_SETUP_EVIDENCE_ROOT',$taskPreviousCameraEvidence,'Process')
        }
    }
    if ($Ticket -ge 19) {
        $taskPreviousRecoveryConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_CONSUMER','Process')
        $taskPreviousRecoveryEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_EVIDENCE_ROOT',(Join-Path $taskRun 'camera-recovery-demo'),'Process')
            Invoke-TaskDotnet 'camera-recovery-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~CameraRecoveryConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'camera-recovery-consumer-tests'))
            foreach ($taskRecoveryFile in @('evidence.json','camera-recovery-evidence.json','camera-recovery-restart.json',
                'summary.json','process.log','restart.log')) {
                $taskRecoveryArtifact = Join-Path $taskRun ('camera-recovery-demo/' + $taskRecoveryFile)
                if (-not (Test-Path -LiteralPath $taskRecoveryArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskRecoveryArtifact).Length -eq 0) {
                    throw "Camera recovery consumer evidence is missing or empty: $taskRecoveryFile"
                }
            }
            $taskRecoveryAcceptance = Get-Content -LiteralPath (Join-Path $taskRun 'camera-recovery-demo/evidence.json') -Raw | ConvertFrom-Json
            $taskRecoveryEvidence = Get-Content -LiteralPath (Join-Path $taskRun 'camera-recovery-demo/camera-recovery-evidence.json') -Raw | ConvertFrom-Json
            $taskRecoverySummary = Get-Content -LiteralPath (Join-Path $taskRun 'camera-recovery-demo/summary.json') -Raw | ConvertFrom-Json
            $taskRecoveryConsumerHash = (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash
            if ($taskRecoveryAcceptance.Result -cne 'Pass' -or $taskRecoveryAcceptance.ExternalNuGetConsumer -cne $true -or
                $taskRecoveryAcceptance.IndependentRestart -cne $true -or $taskRecoveryAcceptance.DatabaseReadOnlyByRestart -cne $true -or
                $taskRecoveryAcceptance.ConsumerSha256 -cne $taskRecoveryConsumerHash -or
                $taskRecoveryEvidence.Result -cne 'Pass' -or $taskRecoveryEvidence.Ready -cne $false -or
                $taskRecoveryEvidence.ConsumerSha256 -cne $taskRecoveryConsumerHash -or
                $taskRecoveryEvidence.ExhaustionMaximumAttempts -ne 20 -or $taskRecoveryEvidence.AuditBeforePhysicalStart -cne $true -or
                $taskRecoveryEvidence.CameraRecoveryFailedLatched -cne $true -or
                $taskRecoverySummary.OutstandingLeases -ne 0 -or $taskRecoverySummary.LeasesReturned -cne $true) {
                throw 'Camera recovery consumer evidence failed its measured result or artifact binding.'
            }
            foreach ($taskRecoveryNotRun in @('PhysicalHardwareQualification','StationAcceptance','Production','NativeCrashIsolation')) {
                if ($taskRecoveryAcceptance.$taskRecoveryNotRun -cne 'NotRun' -or $taskRecoveryEvidence.$taskRecoveryNotRun -cne 'NotRun') {
                    throw "Camera recovery development evidence overstates qualification: $taskRecoveryNotRun"
                }
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_CONSUMER',$taskPreviousRecoveryConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_RECOVERY_EVIDENCE_ROOT',$taskPreviousRecoveryEvidence,'Process')
        }
    }
    if ($Ticket -ge 20) {
        $taskPreviousNetworkConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_CONSUMER','Process')
        $taskPreviousNetworkEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_EVIDENCE_ROOT',(Join-Path $taskRun 'camera-network-demo'),'Process')
            Invoke-TaskDotnet 'camera-network-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~CameraNetworkConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'camera-network-consumer-tests'))
            foreach ($taskNetworkFile in @('evidence.json','camera-network-evidence.json','camera-network-restart.json','camera-network-arm-restart.json',
                'summary.json','process.log','restart.log')) {
                $taskNetworkArtifact = Join-Path $taskRun ('camera-network-demo/' + $taskNetworkFile)
                if (-not (Test-Path -LiteralPath $taskNetworkArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskNetworkArtifact).Length -eq 0) {
                    throw "Camera network consumer evidence is missing or empty: $taskNetworkFile"
                }
            }
            $taskNetworkAcceptance = Get-Content -LiteralPath (Join-Path $taskRun 'camera-network-demo/evidence.json') -Raw | ConvertFrom-Json
            $taskNetworkEvidence = Get-Content -LiteralPath (Join-Path $taskRun 'camera-network-demo/camera-network-evidence.json') -Raw | ConvertFrom-Json
            $taskNetworkSummary = Get-Content -LiteralPath (Join-Path $taskRun 'camera-network-demo/summary.json') -Raw | ConvertFrom-Json
            $taskNetworkConsumerHash = (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash
            if ($taskNetworkAcceptance.Result -cne 'Pass' -or $taskNetworkAcceptance.ExternalNuGetConsumer -cne $true -or
                $taskNetworkAcceptance.IndependentRestart -cne $true -or $taskNetworkAcceptance.DatabaseReadOnlyByRestart -cne $true -or
                $taskNetworkAcceptance.ConsumerSha256 -cne $taskNetworkConsumerHash -or
                $taskNetworkEvidence.Result -cne 'Pass' -or $taskNetworkEvidence.Ready -cne $false -or
                $taskNetworkEvidence.RequiresRecipeActivation -cne $true -or $taskNetworkEvidence.IdentityVerified -cne $true -or
                $taskNetworkEvidence.AuditBeforePhysicalChange -cne $true -or
                $taskNetworkEvidence.StartupQualificationOnly -cne $true -or
                $taskNetworkEvidence.AnonymousRejectedWithoutProjection -cne $true -or
                $taskNetworkEvidence.MissingStepUpRejectedWithoutProjection -cne $true -or
                $taskNetworkEvidence.UnsupportedProviderRejectedWithoutProjection -cne $true -or
                $taskNetworkEvidence.ArmRestartRejectedByReconciliation -cne $true -or
                $taskNetworkEvidence.ArmRestartReadyFalse -cne $true -or
                $taskNetworkEvidence.ArmRestartNoProviderRegistered -cne $true -or
                $taskNetworkEvidence.ArmRestartMalformedRejected -cne $true -or
                $taskNetworkEvidence.ArmRestartEmptyCorrelationRejected -cne $true -or
                $taskNetworkEvidence.ArmRestartAnonymousRejected -cne $true -or
                $taskNetworkEvidence.RequestedAddress -cne $taskNetworkEvidence.ObservedAddress -or
                $taskNetworkSummary.AppliedCount -ne 1 -or $taskNetworkSummary.MaintenanceLeaseHeld -cne $false) {
                throw 'Camera network consumer evidence failed its measured result or artifact binding.'
            }
            foreach ($taskNetworkNotRun in @('PhysicalHardwareQualification','StationAcceptance','Production','NativeCrashIsolation','HostNetworkMutation')) {
                if ($taskNetworkAcceptance.$taskNetworkNotRun -cne 'NotRun' -or $taskNetworkEvidence.$taskNetworkNotRun -cne 'NotRun') {
                    throw "Camera network development evidence overstates qualification: $taskNetworkNotRun"
                }
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_CONSUMER',$taskPreviousNetworkConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_CAMERA_NETWORK_EVIDENCE_ROOT',$taskPreviousNetworkEvidence,'Process')
        }
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
    if ($Ticket -ge 9) {
        $taskAlarmPolicy = Join-Path $taskRun 'development-alarm-policy.json'
        [ordered]@{ Id='development-alarms'; Version='development-2026-09';
            SourceObservationFreshness='00:01:00'; MaximumActiveInstances=32; MaximumPlcEntries=1;
            Rules=@([ordered]@{Code='StartupRecoveryRequired';Source='Runtime.StartupRecovery';Severity=1;
                ProductionImpact=1;IsLatched=$true;Notification=2;PlcCode=101;PlcPriority=100;ResetPrerequisites=15}) } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskAlarmPolicy -Encoding utf8
        $taskAuditArguments += @('--alarm-policy',$taskAlarmPolicy)
    }
    Invoke-TaskDotnet 'consumer-smoke.log' (@($taskConsumerDll,'--smoke','--screenshot',(Join-Path $taskRun 'consumer-window.png'),
        '--trace-db',$taskDatabase,'--trace-manifest',$taskTraceManifest) + $taskAuditArguments)
    if ($Ticket -ge 9) {
        $taskAlarmUiOutput = Get-Content -LiteralPath (Join-Path $taskRun 'consumer-smoke.log') -Raw
        if ($taskAlarmUiOutput -notmatch 'V109-P02 WPF alarm list/history/PLC projection PASS' -or
            -not (Test-Path -LiteralPath (Join-Path $taskRun 'consumer-alarms.png') -PathType Leaf)) {
            throw 'The actual WPF consumer did not prove its alarm page and stable selection.'
        }
    }
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
    if ($Ticket -ge 9) {
        Invoke-TaskDotnet 'alarm-consumer.log' (@($taskConsumerDll,'--alarm-check','--trace-db',$taskDatabase) + $taskAuditArguments)
        $taskAlarmOutput = Get-Content -LiteralPath (Join-Path $taskRun 'alarm-consumer.log') -Raw
        if ($taskAlarmOutput -notmatch 'V109-P01 alarm-consumer PASS' -or
            $taskAlarmOutput -notmatch 'ready=false acknowledged=false' -or
            $taskAlarmOutput -notmatch 'physicalDevices=NotRun') {
            throw 'The independent alarm consumer did not prove the configured closed path.'
        }
    }
    if ($Ticket -ge 10) {
        Invoke-TaskDotnet 'algorithm-preparation-consumer.log' @($taskConsumerDll,'--algorithm-prepare-check')
        $taskAlgorithmOutput = Get-Content -LiteralPath (Join-Path $taskRun 'algorithm-preparation-consumer.log') -Raw
        if ($taskAlgorithmOutput -notmatch 'V110-P01 algorithm-preparation PASS factories=2' -or
            $taskAlgorithmOutput -notmatch 'configurationNegatives=true preparationFailure=true ready=false' -or
            $taskAlgorithmOutput -notmatch 'frameExecution=NotRun productionAlgorithm=UserSupplied') {
            throw 'The independent algorithm consumer did not prove explicit Factory registration and preparation.'
        }
    }
    if ($Ticket -ge 11) {
        Invoke-TaskDotnet 'frame-consumer.log' @($taskConsumerDll,'--frame-consumer-check')
        $taskFrameOutput = Get-Content -LiteralPath (Join-Path $taskRun 'frame-consumer.log') -Raw
        if ($taskFrameOutput -notmatch 'V111-P01 frame-consumer PASS formats=5 padding=true strideAlignment=true' -or
            $taskFrameOutput -notmatch 'cloneRetained=true staleBorrowRejected=true' -or
            $taskFrameOutput -notmatch 'authoritativeExecution=NotRun productionReady=false') {
            throw 'The independent frame consumer did not prove the normalized pixel and native loan contracts.'
        }
    }
    if ($Ticket -ge 12) {
        Invoke-TaskDotnet 'algorithm-execution.log' @($taskConsumerDll,'--algorithm-execution-check')
        $taskExecutionOutput = Get-Content -LiteralPath (Join-Path $taskRun 'algorithm-execution.log') -Raw
        if ($taskExecutionOutput -notmatch 'V112-P01 algorithm-execution PASS decisions=3 contractNegatives=true' -or
            $taskExecutionOutput -notmatch 'exceptionSanitized=true preparedOnce=true productionReady=false') {
            throw 'The independent execution consumer did not validate whole-result semantics and instance reuse.'
        }
        if ($Ticket -ge 13 -and $taskExecutionOutput -notmatch
            'V113-N01 execution-policy-consumer PASS recipeBound=true policyBound=true monotonic=true productionReady=false') {
            throw 'The independent execution consumer did not prove its frozen timing evidence.'
        }
    }
    if ($Ticket -ge 31) {
        $taskPlcPayloadDirectory = Join-Path $taskRun 'plc-result-payload'
        Invoke-TaskDotnet 'plc-result-payload.log' @($taskConsumerDll,'--plc-result-payload-check',$taskPlcPayloadDirectory)
        $taskPlcPayloadEvidence = Get-Content -LiteralPath (Join-Path $taskPlcPayloadDirectory 'evidence.json') -Raw | ConvertFrom-Json
        if ($taskPlcPayloadEvidence.Result -cne 'Pass' -or $taskPlcPayloadEvidence.PublicAlgorithmInput -cne $true -or
            $taskPlcPayloadEvidence.PayloadPreview -cne $true -or $taskPlcPayloadEvidence.ProductionPayloadSnapshot -cne $false -or
            $taskPlcPayloadEvidence.ConsumerSha256 -cne (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash -or
            $taskPlcPayloadEvidence.WholeFault -cne $true -or $taskPlcPayloadEvidence.Ready -cne $false -or
            $taskPlcPayloadEvidence.ArmState -cne 'Disarmed' -or $taskPlcPayloadEvidence.OutstandingLeases -ne 0 -or
            $taskPlcPayloadEvidence.Payloads.Count -ne 4 -or
            @($taskPlcPayloadEvidence.Checks | Where-Object { $_.Passed -cne $true }).Count -ne 0 -or
            ($taskPlcPayloadEvidence.ActualGoldenBytes -join '|') -cne
                '10:11223344|12:55667788|14:0009|15:0001|16:0000|20:18E7|25:00FF|30:0064|40:A55A') {
            throw 'The independent PLC result consumer did not prove complete golden register bytes and the development boundary.'
        }
        foreach ($taskNotRun in @('HardwarePlc','ProductionExecutionAdmission','ResultPublicationGate','ResultValid','StationAcceptance')) {
            if ($taskPlcPayloadEvidence.$taskNotRun -cne 'NotRun') { throw "PLC result evidence overstates qualification: $taskNotRun" }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $taskPlcPayloadDirectory 'payload-preview.html') -PathType Leaf)) {
            throw 'PLC result byte preview is missing.'
        }
    }
    if ($Ticket -ge 14) {
        $taskOverlayDirectory = Join-Path $taskRun 'overlay-demo'
        Invoke-TaskDotnet 'overlay-consumer.log' @($taskConsumerDll,'--overlay-check',$taskOverlayDirectory)
        $taskOverlayOutput = Get-Content -LiteralPath (Join-Path $taskRun 'overlay-consumer.log') -Raw
        if ($taskOverlayOutput -notmatch 'V114-N01 overlay-consumer PASS primitives=10 empty=true' -or
            $taskOverlayOutput -notmatch 'maliciousRejected=true geometryUnchanged=true sourceSeparated=true productionReady=false') {
            throw 'The independent overlay consumer did not prove validated archive and display semantics.'
        }
        Invoke-TaskDotnet 'overlay-restart.log' @($taskConsumerDll,'--overlay-query',$taskOverlayDirectory)
        $taskOverlayRestart = Get-Content -LiteralPath (Join-Path $taskRun 'overlay-restart.log') -Raw
        if ($taskOverlayRestart -notmatch 'V114-N02 overlay-restart PASS records=2 originalSchema=true sourceImageRequired=false') {
            throw 'The independent overlay process did not prove original-contract history without an image source.'
        }
        foreach ($taskOverlayFile in @('overlay-evidence.json','overlay-restart.json','source-frame.png',
            'overlay-preview.png','overlay-viewer.png')) {
            $taskOverlayArtifact = Join-Path $taskOverlayDirectory $taskOverlayFile
            if (-not (Test-Path -LiteralPath $taskOverlayArtifact -PathType Leaf) -or
                (Get-Item -LiteralPath $taskOverlayArtifact).Length -eq 0) {
                throw "Overlay verification artifact is missing or empty: $taskOverlayFile"
            }
        }
    }
    if ($Ticket -ge 16) {
        $taskVirtualRuns = @()
        foreach ($taskReplayIndex in @(1,2)) {
            $taskVirtualDirectory = Join-Path $taskRun ('virtual-camera/replay-' + $taskReplayIndex)
            $taskVirtualLog = 'virtual-camera-replay-' + $taskReplayIndex + '.log'
            Invoke-TaskDotnet $taskVirtualLog @($taskConsumerDll,'--virtual-camera-check',$taskVirtualDirectory)
            $taskVirtualOutput = Get-Content -LiteralPath (Join-Path $taskRun $taskVirtualLog) -Raw
            if ($taskVirtualOutput -notmatch 'V116-N01 virtual-camera-consumer PASS formats=5 replay=true leasesReturned=true productionReady=false') {
                throw 'The independent virtual-camera consumer did not prove its closed public-interface path.'
            }
            foreach ($taskVirtualFile in @('replay-evidence.json','summary.json')) {
                $taskVirtualArtifact = Join-Path $taskVirtualDirectory $taskVirtualFile
                if (-not (Test-Path -LiteralPath $taskVirtualArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskVirtualArtifact).Length -eq 0) {
                    throw "Virtual-camera evidence is missing or empty: $taskVirtualFile"
                }
            }
            $taskVirtualSummary = Get-Content -LiteralPath (Join-Path $taskVirtualDirectory 'summary.json') -Raw | ConvertFrom-Json
            if ($taskVirtualSummary.result -cne 'Pass' -or $taskVirtualSummary.productionReady -cne $false -or
                $taskVirtualSummary.physicalDevices -cne 'NotRun' -or
                $taskVirtualSummary.realCamera -cne 'NotRun' -or
                $taskVirtualSummary.runtimeAcceptance -cne 'NotRun' -or
                $taskVirtualSummary.runtimeProductionAcceptance -cne 'NotRun' -or
                $taskVirtualSummary.providerQualification -cne 'NotRun' -or
                $taskVirtualSummary.formalQualification -cne 'NotRun' -or
                $taskVirtualSummary.leasesReturned -cne $true -or $taskVirtualSummary.replay -cne $true -or
                $taskVirtualSummary.outstandingLeases -cne 0 -or
                $taskVirtualSummary.infrastructureFailures -cne 0 -or $taskVirtualSummary.formats -cne 5) {
                throw 'Virtual-camera consumer evidence failed its measured result or applicability contract.'
            }
            $taskVirtualRuns += [ordered]@{
                run=$taskReplayIndex
                evidenceSha256=(Get-FileHash -LiteralPath (Join-Path $taskVirtualDirectory 'replay-evidence.json') -Algorithm SHA256).Hash
                summarySha256=(Get-FileHash -LiteralPath (Join-Path $taskVirtualDirectory 'summary.json') -Algorithm SHA256).Hash
            }
        }
        if ($taskVirtualRuns[0].evidenceSha256 -cne $taskVirtualRuns[1].evidenceSha256) {
            throw 'Two independent virtual-camera processes produced different deterministic replay evidence.'
        }
        [ordered]@{ verificationId='V116-N02'; result='Pass'; independentProcesses=2; runs=$taskVirtualRuns } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRun 'virtual-camera/replay-comparison.json') -Encoding utf8
        Write-Output 'V116-N02 independent-process virtual-camera replay PASS processes=2 evidenceBytesEqual=true'
    }
    if ($Ticket -ge 18) {
        $taskAcquisitionRuns = @()
        foreach ($taskReplayIndex in @(1,2)) {
            $taskAcquisitionDirectory = Join-Path $taskRun ('camera-acquisition/replay-' + $taskReplayIndex)
            $taskAcquisitionLog = 'camera-acquisition-replay-' + $taskReplayIndex + '.log'
            Invoke-TaskDotnet $taskAcquisitionLog @($taskConsumerDll,'--camera-acquisition-check',$taskAcquisitionDirectory)
            foreach ($taskAcquisitionFile in @('evidence.json','summary.json','replay-evidence.json')) {
                $taskAcquisitionArtifact = Join-Path $taskAcquisitionDirectory $taskAcquisitionFile
                if (-not (Test-Path -LiteralPath $taskAcquisitionArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskAcquisitionArtifact).Length -eq 0) {
                    throw "Controlled acquisition evidence is missing or empty: $taskAcquisitionFile"
                }
            }
            $taskAcquisitionSummary = Get-Content -LiteralPath (Join-Path $taskAcquisitionDirectory 'summary.json') -Raw | ConvertFrom-Json
            if ($taskAcquisitionSummary.Result -cne 'Pass' -or $taskAcquisitionSummary.ProductionReady -cne $false -or
                $taskAcquisitionSummary.PhysicalDevices -cne 'NotRun' -or
                $taskAcquisitionSummary.ProviderQualification -cne 'NotRun' -or
                $taskAcquisitionSummary.StationAcceptance -cne 'NotRun' -or
                $taskAcquisitionSummary.ProductionCycle -cne 'NotRun' -or
                $taskAcquisitionSummary.AlgorithmExecution -cne 'NotRun' -or
                $taskAcquisitionSummary.RuntimeComponent -cne 'Pass' -or
                $taskAcquisitionSummary.Scenarios -cne 4 -or
                $taskAcquisitionSummary.AcceptedAttempts -cne 6 -or
                $taskAcquisitionSummary.SuccessfulFrames -cne 4 -or
                $taskAcquisitionSummary.LeasesReturned -cne $true -or
                $taskAcquisitionSummary.OutstandingLeases -cne 0 -or
                $taskAcquisitionSummary.InfrastructureFailures -cne 0 -or
                $taskAcquisitionSummary.ConsumerSha256 -cne (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash) {
                throw 'Controlled acquisition consumer failed its measured result, applicability or binary binding.'
            }
            $taskAcquisitionRuns += [ordered]@{
                run=$taskReplayIndex
                replaySha256=(Get-FileHash -LiteralPath (Join-Path $taskAcquisitionDirectory 'replay-evidence.json') -Algorithm SHA256).Hash
                evidenceSha256=(Get-FileHash -LiteralPath (Join-Path $taskAcquisitionDirectory 'evidence.json') -Algorithm SHA256).Hash
                summarySha256=(Get-FileHash -LiteralPath (Join-Path $taskAcquisitionDirectory 'summary.json') -Algorithm SHA256).Hash
            }
        }
        if ($taskAcquisitionRuns[0].replaySha256 -cne $taskAcquisitionRuns[1].replaySha256) {
            throw 'Two controlled acquisition consumers disagreed on identity-normalized deterministic replay evidence.'
        }
        [ordered]@{ verificationId='V118-N02'; result='Pass'; independentProcesses=2; runs=$taskAcquisitionRuns } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRun 'camera-acquisition/replay-comparison.json') -Encoding utf8
        Write-Output 'V118-N02 independent-process controlled acquisition replay PASS processes=2 productionReady=false'
    }
    if ($Ticket -ge 21) {
        $taskHikrobotConsumer = Join-Path $taskRun 'hikrobot-consumer'
        [void][IO.Directory]::CreateDirectory($taskHikrobotConsumer)
        foreach ($taskFile in @('Program.cs','SharpInspect.Hikrobot.DeviceProbe.csproj')) {
            Copy-Item -LiteralPath (Join-Path $taskRepo ('samples/SharpInspect.Hikrobot.DeviceProbe/' + $taskFile)) -Destination $taskHikrobotConsumer
        }
        Copy-Item -LiteralPath (Join-Path $taskRepo 'Directory.Build.props') -Destination $taskHikrobotConsumer
        $taskHikrobotProject = Join-Path $taskHikrobotConsumer 'SharpInspect.Hikrobot.DeviceProbe.csproj'
        Invoke-TaskDotnet 'hikrobot-consumer-restore.log' @('restore',$taskHikrobotProject,'-p:UseLocalPackages=true',
            '--configfile',$taskNugetConfig,'--packages',(Join-Path $taskRun 'hikrobot-consumer-cache'))
        Invoke-TaskDotnet 'hikrobot-consumer-build.log' @('build',$taskHikrobotProject,'-c','Release','-p:UseLocalPackages=true','--no-restore')
        $taskHikrobotDll = Join-Path $taskHikrobotConsumer 'bin/Release/net6.0-windows/SharpInspect.Hikrobot.DeviceProbe.dll'
        Invoke-TaskDotnet 'hikrobot-dependency.json' @($taskHikrobotDll,'--diagnose')
        $taskHikrobotDiagnosis = Get-Content -LiteralPath (Join-Path $taskRun 'hikrobot-dependency.json') -Raw | ConvertFrom-Json
        if ($taskHikrobotDiagnosis.result -cne 'Pass' -or $taskHikrobotDiagnosis.nativeSdkLoaded -cne $false -or
            $taskHikrobotDiagnosis.productionReady -cne $false -or $taskHikrobotDiagnosis.productionOpenSucceeded -cne $false -or
            $taskHikrobotDiagnosis.hardwareQualification -cne 'NotRun' -or $taskHikrobotDiagnosis.deviceAccess -cne 'NotRun' -or
            $taskHikrobotDiagnosis.dependency.productionCompatible -cne $false) {
            throw 'Hikrobot isolated NuGet consumer failed its qualification boundary.'
        }
        $taskProbeFixture = Join-Path $taskRun 'hikrobot-probe-inputs'
        [void][IO.Directory]::CreateDirectory($taskProbeFixture)
        $taskOversizedRuntime = Join-Path $taskProbeFixture 'oversized-runtime.dll'
        $taskOversizedStream = [IO.File]::Create($taskOversizedRuntime)
        try { $taskOversizedStream.SetLength(64MB + 1) }
        finally { $taskOversizedStream.Dispose() }
        $taskProbeConfiguration = Join-Path $taskProbeFixture 'configuration.json'
        '{}' | Set-Content -LiteralPath $taskProbeConfiguration -Encoding utf8
        $taskOversizedConfiguration = Join-Path $taskProbeFixture 'oversized-configuration.json'
        [IO.File]::WriteAllText($taskOversizedConfiguration, (' ' * 8193))
        $taskHardwareConfiguration = Join-Path $taskProbeFixture 'hardware-configuration.json'
        [ordered]@{productionAcquisitionMode='HardwareTrigger'; exposureTimeUs=1000; gainDb=0;
            regionOfInterest=[ordered]@{offsetX=0;offsetY=0;width=640;height=480}; pixelFormat='Mono8';
            validBits=$null; acquisitionTimeoutMs=1000; triggerDelayUs=0; whiteBalanceRgb=$null} |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $taskHardwareConfiguration -Encoding utf8
        $taskProbeCases = @(
            @{name='rejection'; reason='HikrobotProbeExplicitDeviceAccessAndAllBindingsRequired'; arguments=@('--allow-device-access')},
            @{name='runtime-size'; reason='HikrobotProbeRuntimeSizeInvalid'; arguments=@('--allow-device-access',
                '--runtime',$taskOversizedRuntime,'--runtime-sha256',('0' * 64),'--device','fixture',
                '--model','fixture','--configuration',$taskProbeConfiguration,'--output',(Join-Path $taskProbeFixture 'size-output'))},
            @{name='runtime-hash'; reason='HikrobotProbeRuntimeHashMismatch'; arguments=@('--allow-device-access',
                '--runtime',$taskHikrobotDll,'--runtime-sha256',('0' * 64),'--device','fixture',
                '--model','fixture','--configuration',$taskProbeConfiguration,'--output',(Join-Path $taskProbeFixture 'hash-output'))},
            @{name='configuration-size'; reason='HikrobotProbeConfigurationSizeInvalid'; arguments=@('--allow-device-access',
                '--runtime',$taskHikrobotDll,'--runtime-sha256',(Get-FileHash -LiteralPath $taskHikrobotDll -Algorithm SHA256).Hash,
                '--device','fixture','--model','fixture','--configuration',$taskOversizedConfiguration,
                '--output',(Join-Path $taskProbeFixture 'configuration-output'))},
            @{name='hardware-trigger'; reason='HikrobotHardwareTriggerNotQualified'; arguments=@('--allow-device-access',
                '--runtime',$taskHikrobotDll,'--runtime-sha256',(Get-FileHash -LiteralPath $taskHikrobotDll -Algorithm SHA256).Hash,
                '--device','fixture','--model','fixture','--configuration',$taskHardwareConfiguration,
                '--output',(Join-Path $taskProbeFixture 'hardware-output'))}
        )
        foreach ($taskProbeCase in $taskProbeCases) {
            $taskProbeStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
            $taskProbeStart.UseShellExecute = $false
            $taskProbeStart.CreateNoWindow = $true
            $taskProbeStart.RedirectStandardOutput = $true
            $taskProbeStart.RedirectStandardError = $true
            $taskProbeStart.ArgumentList.Add($taskHikrobotDll)
            foreach ($taskProbeArgument in $taskProbeCase.arguments) { $taskProbeStart.ArgumentList.Add($taskProbeArgument) }
            $taskProbeProcess = [Diagnostics.Process]::Start($taskProbeStart)
            try {
                $taskProbeOutput = $taskProbeProcess.StandardOutput.ReadToEndAsync()
                $taskProbeError = $taskProbeProcess.StandardError.ReadToEndAsync()
                if (-not $taskProbeProcess.WaitForExit(15000)) {
                    $taskProbeProcess.Kill($true)
                    throw "Hikrobot negative probe failed to reject promptly: $($taskProbeCase.name)"
                }
                $taskProbeErrorText = $taskProbeError.GetAwaiter().GetResult()
                $taskProbeErrorText | Set-Content -LiteralPath (Join-Path $taskRun ('hikrobot-probe-' + $taskProbeCase.name + '.json')) -Encoding utf8
                $taskProbeRejection = $taskProbeErrorText | ConvertFrom-Json
                if ($taskProbeProcess.ExitCode -ne 1 -or $taskProbeOutput.GetAwaiter().GetResult().Length -ne 0 -or
                    $taskProbeRejection.reasonCode -cne $taskProbeCase.reason -or
                    $taskProbeRejection.nativeSdkLoadAttempted -cne $false -or $taskProbeRejection.productionReady -cne $false) {
                    throw "Hikrobot negative request failed its native access boundary: $($taskProbeCase.name)"
                }
            }
            finally { $taskProbeProcess.Dispose() }
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $taskHikrobotPackage = Join-Path $taskFeed 'SharpInspect.NET.Cameras.Hikrobot.0.1.0-dev.1.nupkg'
        $taskHikrobotArchive = [IO.Compression.ZipFile]::OpenRead($taskHikrobotPackage)
        try {
            $taskPayload = @($taskHikrobotArchive.Entries | Where-Object { $_.FullName -match '\.(dll|exe|sys|cti|h|lib)$' })
            if ($taskPayload.Count -ne 1 -or $taskPayload[0].FullName -cne 'lib/net6.0/SharpInspect.Cameras.Hikrobot.dll') {
                throw 'Hikrobot NuGet unexpectedly contains vendor/native payloads.'
            }
        }
        finally { $taskHikrobotArchive.Dispose() }
        [ordered]@{ verificationId='V121-N01'; result='Pass'; nativeSdkLoaded=$false; deviceAccess='NotRun';
            hardwareQualification='NotRun'; productionReady=$false; nativeVendorPayloads=0;
            consumerSha256=(Get-FileHash -LiteralPath $taskHikrobotDll -Algorithm SHA256).Hash;
            packageSha256=(Get-FileHash -LiteralPath $taskHikrobotPackage -Algorithm SHA256).Hash } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRun 'hikrobot-consumer-summary.json') -Encoding utf8
        Write-Output 'V121-N01 isolated Hikrobot NuGet consumer PASS nativeSdkLoaded=false hardwareQualification=NotRun'
    }
    if ($Ticket -ge 22) {
        & (Join-Path $PSScriptRoot 'Test-CameraConformanceConsumer.ps1') -RunDirectory $taskRun -NugetConfig $taskNugetConfig -SourceRevision $taskEvidence.startingHead
    }
    if ($Ticket -ge 23) {
        $taskImagingCalibrationConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_CONSUMER','Process')
        $taskImagingCalibrationEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_EVIDENCE_ROOT','Process')
        try {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_CONSUMER',$taskConsumerDll,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_EVIDENCE_ROOT',(Join-Path $taskRun 'imaging-calibration-demo'),'Process')
            Invoke-TaskDotnet 'imaging-calibration-consumer.log' @('test','tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
                '-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~ImagingSetupConsumerAcceptanceTests',
                '--logger','trx','--results-directory',(Join-Path $taskRun 'imaging-calibration-consumer-tests'))
            foreach ($taskImagingFile in @('evidence.json','imaging-calibration-evidence.json',
                    'imaging-calibration-restart.json','imaging-setup-panel.png',
                    'imaging-setup-panel-form.png','process.log','restart.log')) {
                $taskImagingArtifact = Join-Path $taskRun ('imaging-calibration-demo/' + $taskImagingFile)
                if (-not (Test-Path -LiteralPath $taskImagingArtifact -PathType Leaf) -or
                    (Get-Item -LiteralPath $taskImagingArtifact).Length -eq 0) {
                    throw "Imaging calibration consumer evidence is missing or empty: $taskImagingFile"
                }
            }
            $taskImagingAcceptance = Get-Content -LiteralPath (Join-Path $taskRun 'imaging-calibration-demo/evidence.json') -Raw | ConvertFrom-Json
            $taskImagingEvidence = Get-Content -LiteralPath (Join-Path $taskRun 'imaging-calibration-demo/imaging-calibration-evidence.json') -Raw | ConvertFrom-Json
            $taskImagingRestart = Get-Content -LiteralPath (Join-Path $taskRun 'imaging-calibration-demo/imaging-calibration-restart.json') -Raw | ConvertFrom-Json
            $taskImagingConsumerHash = (Get-FileHash -LiteralPath $taskConsumerDll -Algorithm SHA256).Hash
            if ($taskImagingAcceptance.Result -cne 'Pass' -or
                $taskImagingAcceptance.ExternalNuGetConsumer -cne $true -or
                $taskImagingAcceptance.IndependentRestart -cne $true -or
                $taskImagingAcceptance.DatabaseReadOnlyByRestart -cne $true -or
                $taskImagingAcceptance.ConsumerSha256 -cne $taskImagingConsumerHash -or
                $taskImagingAcceptance.ImagingHistoryRevisions -ne 2 -or
                $taskImagingAcceptance.ExactProfileSelection -cne $true -or
                $taskImagingAcceptance.OldProfileRejected -cne $true -or
                $taskImagingAcceptance.NoAutomaticPhysicalDetection -cne $true -or
                $taskImagingAcceptance.NoDefaultCoefficientContract -cne $true -or
                $taskImagingAcceptance.UiPanelScreenshot -cne 'imaging-setup-panel.png' -or
                $taskImagingAcceptance.UiPanelFormScreenshot -cne 'imaging-setup-panel-form.png' -or
                $taskImagingAcceptance.UiPanelShowsBindingAndHistory -cne $true -or
                $taskImagingAcceptance.UiPanelSubmissionFormEnabled -cne $true -or
                $taskImagingAcceptance.UiPasswordBoxEmpty -cne $true -or
                $taskImagingAcceptance.ProductionReady -cne $false -or
                $taskImagingAcceptance.CanActivate -cne $false -or
                $taskImagingAcceptance.Ready -cne $false -or
                $taskImagingEvidence.Result -cne 'Pass' -or
                $taskImagingEvidence.ImagingRevision2 -ne 2 -or
                $taskImagingEvidence.CandidateCompatible -cne $true -or
                $taskImagingEvidence.HistoricalCompatible -cne $true -or
                $taskImagingEvidence.CandidateEvidencePurpose -cne 'DevelopmentOnly' -or
                $taskImagingEvidence.HistoricalEvidencePurpose -cne 'DevelopmentOnly' -or
                $taskImagingEvidence.OldProfileRejected -cne $true -or
                $taskImagingEvidence.OldProfileReason -cne 'CalibrationImagingSetupRevisionMismatch' -or
                $taskImagingEvidence.NoAutomaticPhysicalDetection -cne $true -or
                $taskImagingEvidence.NoDefaultCoefficientContract -cne $true -or
                $taskImagingEvidence.UiPanelShowsBindingAndHistory -cne $true -or
                $taskImagingEvidence.UiPanelSubmissionFormEnabled -cne $true -or
                $taskImagingEvidence.UiPasswordBoxEmpty -cne $true -or
                $taskImagingEvidence.ProductionReady -cne $false -or
                $taskImagingEvidence.CanActivate -cne $false -or
                $taskImagingEvidence.Ready -cne $false -or
                $taskImagingEvidence.OpenCount -ne 2 -or
                $taskImagingEvidence.FramesProduced -ne 0 -or
                $taskImagingRestart.Result -cne 'Pass' -or
                $taskImagingRestart.HistoryCount -ne 2 -or
                $taskImagingRestart.HashesPersisted -cne $true -or
                $taskImagingRestart.ReadOnlyQueryDatabaseBytesUnchanged -cne $true -or
                $taskImagingRestart.NoDefaultCoefficientContract -cne $true -or
                $taskImagingRestart.OpenedDevices -ne 0 -or
                $taskImagingRestart.ProductionReady -cne $false -or
                $taskImagingRestart.CanActivate -cne $false -or
                $taskImagingRestart.Ready -cne $false) {
                throw 'Imaging calibration consumer evidence failed its measured result or artifact binding.'
            }
            foreach ($taskImagingNotRun in @('PhysicalHardwareQualification','StationAcceptance','Production','NativeCrashIsolation')) {
                if ($taskImagingAcceptance.$taskImagingNotRun -cne 'NotRun' -or
                    $taskImagingEvidence.$taskImagingNotRun -cne 'NotRun' -or
                    $taskImagingRestart.$taskImagingNotRun -cne 'NotRun') {
                    throw "Imaging calibration development evidence overstates qualification: $taskImagingNotRun"
                }
            }
        }
        finally {
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_CONSUMER',$taskImagingCalibrationConsumer,'Process')
            [Environment]::SetEnvironmentVariable('SHARPINSPECT_IMAGING_CALIBRATION_EVIDENCE_ROOT',$taskImagingCalibrationEvidence,'Process')
        }
    }
    if ($Ticket -ge 24) {
        & (Join-Path $PSScriptRoot 'Test-CalibrationConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 31) {
        & (Join-Path $PSScriptRoot 'Test-PlcResultContractConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 32) {
        & (Join-Path $PSScriptRoot 'Test-RecipeActivationConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 33) {
        & (Join-Path $PSScriptRoot 'Test-PreviewSessionConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 34) {
        & (Join-Path $PSScriptRoot 'Test-CalibrationImportConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 35) {
        & (Join-Path $PSScriptRoot 'Test-ManualInspectionConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 36) {
        & (Join-Path $PSScriptRoot 'Test-ProductionAdmissionConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 37) {
        & (Join-Path $PSScriptRoot 'Test-StationQualificationConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 38) {
        & (Join-Path $PSScriptRoot 'Test-RecipeTransferConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 39) {
        & (Join-Path $PSScriptRoot 'Test-TraceStoragePolicyConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 40) {
        & (Join-Path $PSScriptRoot 'Test-ModbusQualificationConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 41) {
        & (Join-Path $PSScriptRoot 'Test-PlcCommunicationConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 42) {
        & (Join-Path $PSScriptRoot 'Test-ProductionInspectionConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 43) {
        & (Join-Path $PSScriptRoot 'Test-PartIdentityConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
    }
    if ($Ticket -ge 44) {
        & (Join-Path $PSScriptRoot 'Test-ProductionRecoveryConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed `
            -IdentityPolicy $taskIdentityPolicy -AuditKey $taskAuditKeyName `
            -AuditKeyDirectory $taskAuditKeyDirectory
    }
    if ($Ticket -ge 46) {
        & (Join-Path $PSScriptRoot 'Test-RecipeSelectionConsumer.ps1') -Run $taskRun -PackageFeed $taskFeed
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
    if ($taskOverlayDirectory) {
        $taskOverlayKeyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes('SharpInspect.SampleOverlay')))
        $taskOverlayKey = [IO.Path]::GetFullPath((Join-Path $taskOverlayDirectory ('audit-keys\' + $taskOverlayKeyHash + '.key')))
        if (-not $taskOverlayKey.StartsWith(([IO.Path]::GetFullPath($taskRun) + [IO.Path]::DirectorySeparatorChar),
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Overlay test key escaped the validation directory.' }
        if (Test-Path -LiteralPath $taskOverlayKey) { Remove-Item -LiteralPath $taskOverlayKey }
    }
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
