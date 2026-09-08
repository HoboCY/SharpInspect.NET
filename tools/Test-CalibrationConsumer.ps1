param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$calibrationRepo = Split-Path -Parent $PSScriptRoot
$calibrationRun = [IO.Path]::GetFullPath($Run)
$calibrationFeed = [IO.Path]::GetFullPath($PackageFeed)
$calibrationCopy = Join-Path $calibrationRun 'calibration-consumer'
$calibrationEvidence = Join-Path $calibrationRun 'calibration-session-demo'
if (Test-Path -LiteralPath $calibrationCopy) { throw 'Use a fresh calibration consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($calibrationCopy)
[void][IO.Directory]::CreateDirectory($calibrationEvidence)
$calibrationSource = Join-Path $calibrationRepo 'samples/SharpInspect.CalibrationConsumer'
foreach ($calibrationFile in Get-ChildItem -LiteralPath $calibrationSource -Recurse -File) {
    $calibrationRelative = [IO.Path]::GetRelativePath($calibrationSource, $calibrationFile.FullName)
    if ($calibrationRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $calibrationTarget = Join-Path $calibrationCopy $calibrationRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $calibrationTarget))
    Copy-Item -LiteralPath $calibrationFile.FullName -Destination $calibrationTarget
}
Copy-Item -LiteralPath (Join-Path $calibrationRepo 'Directory.Build.props') -Destination $calibrationCopy
$calibrationProject = Join-Path $calibrationCopy 'SharpInspect.CalibrationConsumer.csproj'
$calibrationConfig = Join-Path $calibrationCopy 'NuGet.Config'
$calibrationXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($calibrationFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($calibrationConfig, $calibrationXml, [Text.UTF8Encoding]::new($false))
& dotnet restore $calibrationProject -p:UseLocalPackages=true --configfile $calibrationConfig `
    --packages (Join-Path $calibrationRun 'calibration-consumer-cache') 2>&1 |
    Tee-Object -FilePath (Join-Path $calibrationRun 'calibration-consumer-restore.log')
if ($LASTEXITCODE -ne 0) { throw 'Calibration consumer package restore failed.' }
& dotnet build $calibrationProject -c Release -p:UseLocalPackages=true --no-restore 2>&1 |
    Tee-Object -FilePath (Join-Path $calibrationRun 'calibration-consumer-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Calibration consumer package build failed.' }
$calibrationAssets = Get-Content -LiteralPath (Join-Path $calibrationCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($calibrationAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Calibration consumer still references a source project.'
}
foreach ($calibrationPackage in 'Abstractions','Runtime','Wpf','Cameras.Virtual','OpenCvSharp','Calibration.OpenCvSharp') {
    if (-not $calibrationAssets.libraries.PSObject.Properties['SharpInspect.NET.' + $calibrationPackage + '/0.1.0-dev.1']) {
        throw "Calibration consumer package is missing: $calibrationPackage"
    }
}
$calibrationDll = Join-Path $calibrationCopy 'bin/Release/net6.0-windows/SharpInspect.CalibrationConsumer.dll'
$calibrationPriorConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CALIBRATION_CONSUMER','Process')
$calibrationPriorEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT','Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_CONSUMER',$calibrationDll,'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT',$calibrationEvidence,'Process')
    & dotnet test (Join-Path $calibrationRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj') `
        -c Release --no-restore --filter 'FullyQualifiedName~CalibrationConsumerAcceptanceTests' `
        --logger trx --results-directory (Join-Path $calibrationRun 'calibration-consumer-tests') 2>&1 |
        Tee-Object -FilePath (Join-Path $calibrationRun 'calibration-consumer-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Calibration consumer acceptance test failed.' }
    $calibrationResult = Get-Content -LiteralPath (Join-Path $calibrationEvidence 'calibration-session-evidence.json') -Raw | ConvertFrom-Json
    $calibrationRestart = Get-Content -LiteralPath (Join-Path $calibrationEvidence 'calibration-session-restart.json') -Raw | ConvertFrom-Json
    if ($calibrationResult.result -cne 'Pass' -or $calibrationResult.schema -ne 14 -or
        $calibrationResult.start.accepted -cne $true -or $calibrationResult.exit.restorationVerified -cne $true -or
        $calibrationResult.global.ready -cne $false -or $calibrationResult.candidate.canPublish -cne $false -or
        $calibrationResult.candidate.canActivate -cne $false -or $calibrationResult.fixture.developmentOnly -cne $true -or
        $calibrationResult.evidenceAfterExit.FrameCount -ne 3 -or $calibrationResult.evidenceAfterExit.ObservationCount -ne 3 -or
        $calibrationResult.evidenceAfterExit.ExclusionCount -ne 1 -or
        $calibrationRestart.result -cne 'Pass' -or $calibrationRestart.schema -ne 14 -or
        $calibrationRestart.sessionId -cne $calibrationResult.sessionId -or
        $calibrationRestart.candidateHash -cne $calibrationResult.candidate.contentHash -or
        $calibrationRestart.readOnlyQueryDatabaseUnchanged -cne $true -or $calibrationRestart.openedDevices -ne 0 -or
        $calibrationRestart.ready -cne $false) {
        throw 'Calibration consumer evidence did not satisfy the retained session/restart contract.'
    }
    foreach ($calibrationScope in 'physicalHardwareQualification','providerQualification','stationAcceptance','production') {
        if ($calibrationResult.global.$calibrationScope -cne 'NotRun' -or $calibrationRestart.$calibrationScope -cne 'NotRun') {
            throw "Calibration development evidence overstates qualification: $calibrationScope"
        }
    }
    if (@($calibrationResult.screenshots).Count -lt 1) { throw 'Calibration WPF render evidence is missing.' }
    foreach ($calibrationScreenshot in $calibrationResult.screenshots) {
        $calibrationImage = [IO.Path]::GetFullPath((Join-Path $calibrationEvidence $calibrationScreenshot))
        if (-not $calibrationImage.StartsWith($calibrationEvidence + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $calibrationImage -PathType Leaf) -or
            (Get-Item -LiteralPath $calibrationImage).Length -eq 0) { throw 'Calibration WPF render artifact is invalid.' }
    }
    [ordered]@{
        result='Pass'; validationIds=@('V124-N01','V124-N02'); externalNuGetConsumer=$true; independentRestart=$true
        consumerSha256=(Get-FileHash -LiteralPath $calibrationDll -Algorithm SHA256).Hash
        sessionId=$calibrationResult.sessionId; frameCount=3; observationCount=3; exclusionCount=1
        ready=$false; canPublish=$false; canActivate=$false; physicalHardwareQualification='NotRun'; production='NotRun'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $calibrationEvidence 'acceptance.json') -Encoding utf8
    Write-Output 'V124-N01/N02 calibration NuGet consumer and independent restart PASS'
    $checkerboardDirectory = Join-Path $calibrationEvidence 'checkerboard'
    $checkerboardResult = Get-Content -LiteralPath (Join-Path $checkerboardDirectory 'calibration-session-evidence.json') -Raw | ConvertFrom-Json
    $checkerboardRestart = Get-Content -LiteralPath (Join-Path $checkerboardDirectory 'calibration-session-restart.json') -Raw | ConvertFrom-Json
    if ($checkerboardResult.result -cne 'Pass' -or $checkerboardResult.calibrationMode -cne 'checkerboard' -or
        $checkerboardResult.schema -ne 14 -or $checkerboardResult.checkerboard.imageCount -ne 20 -or
        $checkerboardResult.checkerboard.frozenManifestHash -cne '1329D4C19AD2F781E47599710A5D200831A33CEB54E30CD97D837CC3EB13CC16' -or
        $checkerboardResult.global.ready -cne $false -or $checkerboardResult.candidate.canPublish -cne $false -or
        $checkerboardResult.candidate.canActivate -cne $false -or
        $checkerboardResult.candidate.decodedViewCount -ne 19 -or $checkerboardResult.candidate.decodedPointCount -ne 1026 -or
        $checkerboardResult.extractionReceiptCount -ne 20 -or
        $checkerboardResult.store.maximumFramesPerSession -ne 24 -or
        $checkerboardRestart.result -cne 'Pass' -or $checkerboardRestart.calibrationMode -cne 'checkerboard' -or
        $checkerboardRestart.candidateHash -cne $checkerboardResult.candidate.contentHash -or
        $checkerboardRestart.candidateEvidenceHash -cne $checkerboardResult.candidate.evidenceContentHash -or
        $checkerboardRestart.validViewCount -ne 19 -or $checkerboardRestart.validPointCount -ne 1026 -or
        $checkerboardRestart.extractionReceiptCount -ne 20 -or
        $checkerboardRestart.openedDevices -ne 0 -or $checkerboardRestart.readOnlyQueryDatabaseUnchanged -cne $true -or
        $checkerboardRestart.ready -cne $false) {
        throw 'Checkerboard calibration consumer or read-only restart did not satisfy its retained evidence contract.'
    }
    if (@($checkerboardResult.screenshots).Count -lt 3) { throw 'Checkerboard candidate WPF render evidence is missing.' }
    foreach ($checkerboardScreenshot in $checkerboardResult.screenshots) {
        $checkerboardImage = [IO.Path]::GetFullPath((Join-Path $checkerboardDirectory $checkerboardScreenshot))
        if (-not $checkerboardImage.StartsWith($checkerboardDirectory + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $checkerboardImage -PathType Leaf) -or
            (Get-Item -LiteralPath $checkerboardImage).Length -eq 0) { throw 'Checkerboard WPF render artifact is invalid.' }
    }
    [ordered]@{
        result='Pass'; validationIds=@('V125-N01','V125-N02'); externalNuGetConsumer=$true; independentRestart=$true
        consumerSha256=(Get-FileHash -LiteralPath $calibrationDll -Algorithm SHA256).Hash
        sessionId=$checkerboardResult.sessionId; frameCount=20; exclusionCount=1; selectedViewCount=19
        candidateHash=$checkerboardRestart.candidateHash; evidenceHash=$checkerboardRestart.candidateEvidenceHash
        ready=$false; canPublish=$false; canActivate=$false; physicalHardwareQualification='NotRun'; production='NotRun'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checkerboardDirectory 'acceptance.json') -Encoding utf8
    Write-Output 'V125-N01/N02 checkerboard NuGet consumer and independent restart PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_CONSUMER',$calibrationPriorConsumer,'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT',$calibrationPriorEvidence,'Process')
}
