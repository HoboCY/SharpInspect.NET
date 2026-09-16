param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed,
    [string]$DependencyFeed = 'https://api.nuget.org/v3/index.json',
    [string]$RuntimeEvidenceDocument
)
$ErrorActionPreference = 'Stop'
$performanceRepo = Split-Path -Parent $PSScriptRoot
$performanceRun = [IO.Path]::GetFullPath($Run)
$performanceFeed = [IO.Path]::GetFullPath($PackageFeed)
$performanceCopy = Join-Path $performanceRun 'performance-consumer'
$performanceEvidence = Join-Path $performanceRun 'performance-consumer-evidence'
if (Test-Path -LiteralPath $performanceCopy) { throw 'Use a fresh performance consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($performanceCopy)
[void][IO.Directory]::CreateDirectory($performanceEvidence)
foreach ($performanceFile in 'SharpInspect.Performance.Consumer.csproj', 'Program.cs') {
    Copy-Item -LiteralPath (Join-Path $performanceRepo ('samples/SharpInspect.Performance.Consumer/' + $performanceFile)) `
        -Destination (Join-Path $performanceCopy $performanceFile)
}
$performanceProject = Join-Path $performanceCopy 'SharpInspect.Performance.Consumer.csproj'
$performanceConfig = Join-Path $performanceCopy 'NuGet.Config'
$performanceXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($performanceFeed) + '"/><add key="dependencies" value="' +
    [Security.SecurityElement]::Escape($DependencyFeed) + '"/></packageSources></configuration>'
[IO.File]::WriteAllText($performanceConfig, $performanceXml, [Text.UTF8Encoding]::new($false))

function Invoke-PerformanceDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $performanceRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Performance consumer failed: $LogName ($LASTEXITCODE)" }
}

Invoke-PerformanceDotnet 'performance-consumer-restore.log' @('restore', $performanceProject,
    '-p:UseLocalPackages=true', '--configfile', $performanceConfig,
    '--packages', (Join-Path $performanceRun 'performance-consumer-cache'))
Invoke-PerformanceDotnet 'performance-consumer-build.log' @('build', $performanceProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$performanceAssets = Get-Content -LiteralPath (Join-Path $performanceCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($performanceAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Performance consumer still references source projects.'
}
foreach ($performancePackage in 'Abstractions', 'Runtime') {
    if (-not $performanceAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $performancePackage + '/0.1.0-dev.1')]) {
        throw "Performance consumer package missing: $performancePackage"
    }
}
$performanceOutput = Join-Path $performanceCopy 'bin/Release/net6.0'
$performanceDll = Join-Path $performanceOutput 'SharpInspect.Performance.Consumer.dll'
$performanceHashes = [ordered]@{ consumer=(Get-FileHash -LiteralPath $performanceDll -Algorithm SHA256).Hash }
function Get-PerformanceAssemblyIdentity([string]$Path, [string]$FileHash) {
    # Match production-loaded-assembly-v1: full name, MVID, then actual file SHA-256.
    # Read metadata without executing the packaged assembly or invoking internal APIs.
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($Path).FullName
    $assemblyInput = [IO.File]::OpenRead($Path)
    try {
        $peReader = [Reflection.PortableExecutable.PEReader]::new($assemblyInput)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            $module = $metadata.GetModuleDefinition()
            $mvid = $metadata.GetGuid($module.Mvid).ToString('D')
        } finally { $peReader.Dispose() }
    } finally { $assemblyInput.Dispose() }
    $identityStream = [IO.MemoryStream]::new()
    $identityUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $identityWriter = [IO.BinaryWriter]::new($identityStream, $identityUtf8, $true)
    try {
        $kindBytes = $identityUtf8.GetBytes('production-loaded-assembly-v1')
        $identityWriter.Write([int]$kindBytes.Length)
        $identityWriter.Write([byte[]]$kindBytes)
        $identityWriter.Write([int]3)
        foreach ($part in @($assemblyName, $mvid, $FileHash)) {
            $partBytes = $identityUtf8.GetBytes($part)
            $identityWriter.Write([int]$partBytes.Length)
            $identityWriter.Write([byte[]]$partBytes)
        }
        $identityWriter.Flush()
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identityStream.ToArray()))
    } finally { $identityWriter.Dispose(); $identityStream.Dispose() }
}
foreach ($performancePackage in 'Abstractions', 'Runtime') {
    $performanceNupkg = Join-Path $performanceFeed ('SharpInspect.NET.' + $performancePackage + '.0.1.0-dev.1.nupkg')
    if (-not (Test-Path -LiteralPath $performanceNupkg -PathType Leaf)) { throw "Performance package missing: $performancePackage" }
    $performanceEntryName = 'lib/net6.0/SharpInspect.' + $performancePackage + '.dll'
    $performanceArchive = [IO.Compression.ZipFile]::OpenRead($performanceNupkg)
    try {
        $performanceEntry = $performanceArchive.GetEntry($performanceEntryName)
        if (-not $performanceEntry) { throw "Packaged assembly missing: $performanceEntryName" }
        $performanceStream = $performanceEntry.Open()
        try { $performancePackaged = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($performanceStream)) }
        finally { $performanceStream.Dispose() }
    }
    finally { $performanceArchive.Dispose() }
    $performanceBuilt = (Get-FileHash -LiteralPath (Join-Path $performanceOutput ('SharpInspect.' + $performancePackage + '.dll')) -Algorithm SHA256).Hash
    if ($performanceBuilt -cne $performancePackaged) {
        throw "Performance consumer $performancePackage assembly does not match its declared package."
    }
    $performanceHashes[$performancePackage + 'Assembly'] = $performancePackaged
    $performanceHashes[$performancePackage + 'LoadedIdentity'] = Get-PerformanceAssemblyIdentity `
        (Join-Path $performanceOutput ('SharpInspect.' + $performancePackage + '.dll')) $performancePackaged
    $performanceHashes[$performancePackage + 'Package'] = (Get-FileHash -LiteralPath $performanceNupkg -Algorithm SHA256).Hash
}

function Invoke-PerformanceConsumer([string]$LogName, [string[]]$Arguments) {
    $performanceStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $performanceStart.UseShellExecute = $false
    $performanceStart.CreateNoWindow = $true
    $performanceStart.RedirectStandardOutput = $true
    $performanceStart.RedirectStandardError = $true
    $performanceStart.ArgumentList.Add($performanceDll)
    foreach ($performanceArgument in $Arguments) { $performanceStart.ArgumentList.Add($performanceArgument) }
    $performanceProcess = [Diagnostics.Process]::Start($performanceStart)
    try {
        $performanceStdout = $performanceProcess.StandardOutput.ReadToEndAsync()
        $performanceStderr = $performanceProcess.StandardError.ReadToEndAsync()
        if (-not $performanceProcess.WaitForExit(60000)) { $performanceProcess.Kill($true); throw 'Performance consumer deadline exceeded.' }
        $performanceOut = $performanceStdout.GetAwaiter().GetResult()
        $performanceErr = $performanceStderr.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $performanceRun ($LogName + '.stdout.log')) -Value $performanceOut -Encoding utf8
        Set-Content -LiteralPath (Join-Path $performanceRun ($LogName + '.stderr.log')) -Value $performanceErr -Encoding utf8
        if (-not [string]::IsNullOrWhiteSpace($performanceErr)) { throw "Performance consumer wrote unexpected diagnostics: $LogName" }
        return @{ exit=$performanceProcess.ExitCode; stdout=$performanceOut }
    }
    finally { $performanceProcess.Dispose() }
}

$performanceSmoke = Invoke-PerformanceConsumer 'performance-consumer-smoke' @()
if ($performanceSmoke.exit -ne 0) {
    throw ('Performance consumer smoke exit ' + $performanceSmoke.exit + '; expected 0. See performance-consumer-smoke.stdout.log.')
}
$performanceSmokeDetail = $performanceSmoke.stdout | ConvertFrom-Json
$performanceFailedChecks = @($performanceSmokeDetail.checks.PSObject.Properties |
    Where-Object { -not $_.Value } | ForEach-Object { $_.Name })
if ($performanceSmokeDetail.result -cne 'Pass' -or $performanceFailedChecks.Count -ne 0 -or
    $performanceSmokeDetail.scenarioKinds -ne 6 -or $performanceSmokeDetail.spanBudgets -ne 12 -or
    $performanceSmokeDetail.resourceBudgets -ne 42 -or $performanceSmokeDetail.productionQualificationAuthority -cne $false -or
    $performanceSmokeDetail.captureState -cne 'Incomplete' -or $performanceSmokeDetail.reportPassed -cne $false -or
    $performanceSmokeDetail.notRunScenarios -ne 5 -or $performanceSmokeDetail.documentBytes -le 0 -or
    $performanceSmokeDetail.rawHash -notmatch '^[0-9A-F]{64}$' -or $performanceSmokeDetail.reportHash -notmatch '^[0-9A-F]{64}$') {
    throw ('Performance consumer smoke did not satisfy its development negative gates: ' + ($performanceFailedChecks -join ','))
}

$performanceDocument = Join-Path $performanceEvidence 'canonical-performance.json'
$performanceEmit = Invoke-PerformanceConsumer 'performance-consumer-emit' @('--emit', $performanceDocument)
if ($performanceEmit.exit -ne 0) {
    throw ('Performance consumer canonical emit exit ' + $performanceEmit.exit + '; expected 0.')
}
$performanceEmitDetail = $performanceEmit.stdout | ConvertFrom-Json
if ($performanceEmitDetail.result -cne 'Pass' -or
    $performanceEmitDetail.format -cne 'SharpInspect.PerformanceEvidence.v1' -or
    $performanceEmitDetail.productionQualificationAuthority -cne $false -or
    -not (Test-Path -LiteralPath $performanceDocument -PathType Leaf) -or
    (Get-Item -LiteralPath $performanceDocument).Length -ne $performanceEmitDetail.documentBytes) {
    throw 'Performance consumer did not publish a canonical development document.'
}

$performanceInspect = Invoke-PerformanceConsumer 'performance-consumer-inspect' @('--inspect', $performanceDocument)
if ($performanceInspect.exit -ne 3) {
    throw ('Performance consumer failed-report inspection exit ' + $performanceInspect.exit + '; expected 3.')
}
$performanceInspectDetail = $performanceInspect.stdout | ConvertFrom-Json
$performanceSpanStatistics = @($performanceInspectDetail.spans | Where-Object { $null -ne $_.p50 -or $null -ne $_.p95 -or
    $null -ne $_.p99 -or $null -ne $_.observedMax -or $null -ne $_.jitter })
if ($performanceInspectDetail.result -cne 'Failed' -or $performanceInspectDetail.passed -cne $false -or
    $performanceInspectDetail.purpose -cne 'FrameworkBaseline' -or
    $performanceInspectDetail.productionQualificationAuthority -cne $false -or
    $performanceInspectDetail.captureState -cne 'Incomplete' -or $performanceInspectDetail.spanStatisticsNull -cne $true -or
    $performanceSpanStatistics.Count -ne 0 -or @($performanceInspectDetail.spans).Count -ne 12 -or
    $performanceInspectDetail.failureCount -le 0 -or $performanceInspectDetail.resourcesUnknown.count -ne 0 -or
    $performanceInspectDetail.hashes.raw -cne $performanceEmitDetail.rawHash -or
    $performanceInspectDetail.hashes.report -cne $performanceEmitDetail.reportHash) {
    throw 'Performance canonical inspection did not report the expected failed development baseline.'
}
if ($performanceInspect.stdout -match [Regex]::Escape($performanceDocument) -or
    $performanceInspect.stdout -cmatch '"Raw"') {
    throw 'Performance inspection output must not dump raw payloads or filesystem paths.'
}

$performanceInvalid = Join-Path $performanceEvidence 'invalid-format.json'
[IO.File]::WriteAllText($performanceInvalid, '{"Format":"SharpInspect.PerformanceEvidence.v0"}', [Text.UTF8Encoding]::new($false))
$performanceInvalidRun = Invoke-PerformanceConsumer 'performance-consumer-invalid' @('--inspect', $performanceInvalid)
if ($performanceInvalidRun.exit -ne 2) {
    throw ('Performance consumer invalid-format inspection exit ' + $performanceInvalidRun.exit + '; expected 2.')
}
$performanceInvalidDetail = $performanceInvalidRun.stdout | ConvertFrom-Json
if ($performanceInvalidDetail.result -cne 'Invalid' -or
    $performanceInvalidRun.stdout -match [Regex]::Escape($performanceInvalid)) {
    throw 'Invalid document inspection did not fail closed without dumping the path.'
}

$performanceRuntimeDetail = $null
$performanceRuntimeHash = $null
if (-not [string]::IsNullOrWhiteSpace($RuntimeEvidenceDocument)) {
    $performanceRuntimeHash = (Get-FileHash -LiteralPath $RuntimeEvidenceDocument -Algorithm SHA256).Hash
    $performanceRuntimeDocument = Get-Content -LiteralPath $RuntimeEvidenceDocument -Raw | ConvertFrom-Json
    if ($performanceRuntimeDocument.Raw.Header.FrameworkAssemblyHash -cne $performanceHashes.RuntimeLoadedIdentity -or
        $performanceRuntimeDocument.Raw.Header.AbstractionsAssemblyHash -cne $performanceHashes.AbstractionsLoadedIdentity) {
        throw 'Runtime evidence was captured with different framework package assemblies.'
    }
    $performanceRuntime = Invoke-PerformanceConsumer 'performance-consumer-runtime-inspect' @('--inspect', $RuntimeEvidenceDocument)
    $performanceRuntimeDetail = $performanceRuntime.stdout | ConvertFrom-Json
    if ($performanceRuntime.exit -ne 0 -or $performanceRuntimeDetail.passed -cne $true -or
        $performanceRuntimeDetail.captureState -cne 'Sealed' -or $performanceRuntimeDetail.failureCount -ne 0 -or
        $performanceRuntimeDetail.purpose -cne 'FrameworkBaseline' -or
        $performanceRuntimeDetail.productionQualificationAuthority -cne $false -or
        @($performanceRuntimeDetail.spans).Count -ne 12 -or
        (Get-FileHash -LiteralPath $RuntimeEvidenceDocument -Algorithm SHA256).Hash -cne $performanceRuntimeHash) {
        throw 'Independent NuGet consumer could not recompute the actual fixed virtual runtime evidence.'
    }
}

[ordered]@{
    result='Pass'
    scope='isolated NuGet development consumer; explicitly declared development contract; empty Incomplete negative gates; canonical encode/decode/readback; runtimeInspect when supplied binds actual captured framework assemblies; production qualification NotRun'
    externalNuGetConsumer=$true; productionQualificationAuthority=$false; consumerDll=$performanceDll; hashes=$performanceHashes
    canonicalDocument='canonical-performance.json'; smoke=$performanceSmokeDetail; emit=$performanceEmitDetail
    inspect=$performanceInspectDetail; invalidInspect=$performanceInvalidDetail
    runtimeEvidenceSha256=$performanceRuntimeHash; runtimeInspect=$performanceRuntimeDetail
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $performanceRun 'performance-consumer.json') -Encoding utf8
Write-Output 'Performance development NuGet consumer PASS (explicit development contract, empty Incomplete capture, canonical readback); production qualification NotRun.'
