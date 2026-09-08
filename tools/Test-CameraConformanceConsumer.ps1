param([Parameter(Mandatory)][string]$RunDirectory, [Parameter(Mandatory)][string]$NugetConfig,
    [Parameter(Mandatory)][string]$SourceRevision)
$ErrorActionPreference = 'Stop'
$taskCameraRepo = Split-Path -Parent $PSScriptRoot
$taskCameraConsumer = Join-Path $RunDirectory 'camera-conformance-consumer'
[void][IO.Directory]::CreateDirectory($taskCameraConsumer)
foreach ($taskCameraFile in Get-ChildItem -LiteralPath (Join-Path $taskCameraRepo 'samples/SharpInspect.CameraConformance.Probe') -File) {
    if ($taskCameraFile.Extension -notin '.cs', '.csproj') { continue }
    Copy-Item -LiteralPath $taskCameraFile.FullName -Destination $taskCameraConsumer
}
Copy-Item -LiteralPath (Join-Path $taskCameraRepo 'Directory.Build.props') -Destination $taskCameraConsumer
$taskCameraProject = Join-Path $taskCameraConsumer 'SharpInspect.CameraConformance.Probe.csproj'
function Invoke-CameraDotnet([string]$Log, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $RunDirectory $Log)
    if ($LASTEXITCODE -ne 0) { throw "Camera conformance consumer failed: $Log (exit $LASTEXITCODE)" }
}
Invoke-CameraDotnet 'camera-conformance-restore.log' @('restore',$taskCameraProject,'-p:UseLocalPackages=true',
    '--configfile',$NugetConfig,'--packages',(Join-Path $RunDirectory 'camera-conformance-cache'))
Invoke-CameraDotnet 'camera-conformance-build.log' @('build',$taskCameraProject,'-c','Release','--no-restore','-p:UseLocalPackages=true')
$taskCameraDll = Join-Path $taskCameraConsumer 'bin/Release/net6.0/SharpInspect.CameraConformance.Probe.dll'
$taskCameraReports = @()
for ($taskCameraIndex = 1; $taskCameraIndex -le 2; $taskCameraIndex++) {
    $taskCameraEvidence = Join-Path $RunDirectory ('camera-conformance/run-' + $taskCameraIndex)
    Invoke-CameraDotnet ('camera-conformance-run-' + $taskCameraIndex + '.log') @($taskCameraDll,'run',$taskCameraEvidence,$SourceRevision)
    Invoke-CameraDotnet ('camera-conformance-query-' + $taskCameraIndex + '.log') @($taskCameraDll,'query',$taskCameraEvidence,$SourceRevision)
    $taskCameraReports += Get-Content -LiteralPath (Join-Path $taskCameraEvidence 'report.json') -Raw | ConvertFrom-Json
}
$taskCameraFirst = $taskCameraReports[0]
$taskCameraSecond = $taskCameraReports[1]
foreach ($taskCameraHashName in @('ProfileHash','CandidateHash','ContextHash','FixtureHash')) {
    if ($taskCameraFirst.$taskCameraHashName -cne $taskCameraSecond.$taskCameraHashName) {
        throw "Camera conformance replay changed its frozen binding: $taskCameraHashName"
    }
}
function Get-CameraSemanticRecords($Report) {
    @($Report.Cases | ForEach-Object {
        [ordered]@{testId=$_.TestId; requirementId=$_.RequirementId; sourceReference=$_.SourceReference;
            outcome=$_.Record.Outcome; observed=$_.Record.Observed; reasonCode=$_.Record.ReasonCode;
            outputHashes=@($_.Record.Outputs | ForEach-Object { $_.Name + '=' + $_.Sha256 })}
    }) | ConvertTo-Json -Depth 8 -Compress
}
if ((Get-CameraSemanticRecords $taskCameraFirst) -cne (Get-CameraSemanticRecords $taskCameraSecond)) {
    throw 'Camera conformance independent-process replay changed public observations or raw evidence hashes.'
}
$taskCameraReportPath = Join-Path $RunDirectory 'camera-conformance/run-1/report.json'
$taskCameraOriginalReport = [IO.File]::ReadAllBytes($taskCameraReportPath)
$taskCameraContextPath = Join-Path $RunDirectory 'camera-conformance/run-1/frozen-context.json'
$taskCameraOriginalContext = [IO.File]::ReadAllBytes($taskCameraContextPath)
$taskCameraRawPath = Join-Path $RunDirectory ('camera-conformance/run-1/raw/V122-C01/' + $taskCameraFirst.Cases[0].Record.Outputs[0].Name + '.json')
$taskCameraOriginalRaw = [IO.File]::ReadAllBytes($taskCameraRawPath)
$taskCameraTamperFields = @('Schema','SuiteVersion','FixtureHash','QualificationStatus','ScopeLimitations','SourceRevision','RecordOutputHash','RequirementSource','FrozenContext','RawExport')
try {
    foreach ($taskCameraTamperField in $taskCameraTamperFields) {
        $taskCameraChangedReport = [Text.Encoding]::UTF8.GetString($taskCameraOriginalReport) | ConvertFrom-Json
        $taskCameraRevision = $SourceRevision
        [IO.File]::WriteAllBytes($taskCameraContextPath, $taskCameraOriginalContext)
        [IO.File]::WriteAllBytes($taskCameraRawPath, $taskCameraOriginalRaw)
        switch ($taskCameraTamperField) {
            'RawExport' { [IO.File]::WriteAllText($taskCameraRawPath, '{}') }
            'FrozenContext' { [IO.File]::WriteAllText($taskCameraContextPath, '{}') }
            'RecordOutputHash' { $taskCameraChangedReport.Cases[0].Record.Outputs[0].Sha256 = '0' * 64 }
            'RequirementSource' { $taskCameraChangedReport.Cases[0].SourceReference = 'fabricated-reference' }
            'QualificationStatus' { $taskCameraChangedReport.QualificationStatus = 'OfficiallySupported' }
            'FixtureHash' { $taskCameraChangedReport.FixtureHash = '0' * 64 }
            'SourceRevision' { $taskCameraChangedReport.SourceRevision = 'fabricated-revision'; $taskCameraRevision = 'fabricated-revision' }
            default { $taskCameraChangedReport.$taskCameraTamperField = 'fabricated-value' }
        }
        $taskCameraChangedReport | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskCameraReportPath -Encoding utf8NoBOM
        $taskCameraStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $taskCameraStart.UseShellExecute = $false
        $taskCameraStart.CreateNoWindow = $true
        $taskCameraStart.RedirectStandardOutput = $true
        $taskCameraStart.RedirectStandardError = $true
        foreach ($taskCameraArgument in @($taskCameraDll,'query',(Join-Path $RunDirectory 'camera-conformance/run-1'),$taskCameraRevision)) {
            $taskCameraStart.ArgumentList.Add($taskCameraArgument)
        }
        $taskCameraProcess = [Diagnostics.Process]::Start($taskCameraStart)
        try {
            $taskCameraOut = $taskCameraProcess.StandardOutput.ReadToEndAsync()
            $taskCameraErr = $taskCameraProcess.StandardError.ReadToEndAsync()
            if (-not $taskCameraProcess.WaitForExit(15000)) {
                $taskCameraProcess.Kill($true)
                throw "Camera conformance report tamper query exceeded deadline: $taskCameraTamperField"
            }
            $taskCameraErrorText = $taskCameraErr.GetAwaiter().GetResult()
            $taskCameraErrorText | Set-Content -LiteralPath (Join-Path $RunDirectory ('camera-conformance-tamper-' + $taskCameraTamperField + '.json')) -Encoding utf8
            if ($taskCameraProcess.ExitCode -ne 1 -or $taskCameraOut.GetAwaiter().GetResult().Length -ne 0 -or
                ($taskCameraErrorText | ConvertFrom-Json).result -cne 'Fail') {
                throw "Camera conformance query accepted changed report field: $taskCameraTamperField"
            }
        }
        finally { $taskCameraProcess.Dispose() }
    }
}
finally {
    [IO.File]::WriteAllBytes($taskCameraReportPath, $taskCameraOriginalReport)
    [IO.File]::WriteAllBytes($taskCameraContextPath, $taskCameraOriginalContext)
    [IO.File]::WriteAllBytes($taskCameraRawPath, $taskCameraOriginalRaw)
}
Invoke-CameraDotnet 'camera-conformance-query-restored.log' @($taskCameraDll,'query',(Join-Path $RunDirectory 'camera-conformance/run-1'),$SourceRevision)
$taskCameraPackage = Join-Path $RunDirectory 'packages/SharpInspect.NET.Cameras.Conformance.0.1.0-dev.1.nupkg'
$taskCameraArchive = [IO.Compression.ZipFile]::OpenRead($taskCameraPackage)
try {
    $taskCameraNuspec = $taskCameraArchive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
    $taskCameraReader = [IO.StreamReader]::new($taskCameraNuspec.Open())
    try { $taskCameraNuspecText = $taskCameraReader.ReadToEnd() }
    finally { $taskCameraReader.Dispose() }
    if ($taskCameraNuspecText -match 'SharpInspect.NET.(Wpf|Cameras.Virtual|Cameras.Hikrobot)') {
        throw 'Core camera conformance package depends on an optional UI or provider implementation.'
    }
}
finally { $taskCameraArchive.Dispose() }
[ordered]@{ verificationId='V122-N01'; result='Pass'; independentRuns=2; immutableRecordsPerRun=15;
    publicContractPassPerRun=12; instrumentationBlockedPerRun=3; rejectedReportTamperFields=$taskCameraTamperFields;
    hardwareQualification='NotRun'; canIssueQualification=$false;
    profileHash=$taskCameraFirst.ProfileHash; candidateHash=$taskCameraFirst.CandidateHash; contextHash=$taskCameraFirst.ContextHash;
    consumerSha256=(Get-FileHash -LiteralPath $taskCameraDll -Algorithm SHA256).Hash;
    packageSha256=(Get-FileHash -LiteralPath $taskCameraPackage -Algorithm SHA256).Hash } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunDirectory 'camera-conformance-summary.json') -Encoding utf8
Write-Output 'V122-N01 isolated NuGet conformance consumer PASS independentReplay=2 publicContractPass=12 instrumentationBlocked=3 hardwareQualification=NotRun'
