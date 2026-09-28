#Requires -Version 7.0
<#
Capture-Baseline.ps1 -- ticket 56 composite validation baseline snapshot (read-only over the repo).

Captures, into this directory:
  baseline\listtests-<assembly>.txt  raw `dotnet vstest <Release test dll> /ListTests` output
  baseline-discovery.json           per-assembly case identity inventory + test DLL SHA-256 + counts
  baseline-binaries.json            SHA-256 of every file under src\*\bin\Release and tests\*\bin\Release
  baseline-source-hashes.json       sourceHashes copied out of the run-47 validation.json (immutable copy)
  baseline-run47-validation.json    byte copy of the run-47 validation.json + its SHA-256
  baseline-constants.json           frozen expectations (3164 cases / 37 V156 / crash identity / allowlists)

The capture NEVER writes into the baseline run directory; it only reads it.
It fails if the source-hash engine does not reproduce the driver's recorded sourceHashes,
or if the discovery totals do not match the frozen expectations.
#>
[CmdletBinding()]
param(
    [string]$Baseline = 'F:\si56v1\ticket56\20260916-122627-391',
    [string]$OutDirectory = $PSScriptRoot,
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'SharpInspect.NET.sln') -PathType Leaf)) {
    throw "Repository root does not contain SharpInspect.NET.sln: $RepositoryRoot"
}
[void][IO.Directory]::CreateDirectory((Join-Path $OutDirectory 'baseline'))
$rawDirectory = Join-Path $OutDirectory 'baseline'

$expectedSolutionCases = 3164
$expectedV156Cases = 37
$actualRuntimeDllSha256 = 'CB4ACDF836E4CFB141A6D37411A20AE2BD52F740D0D8156854CC69AD45B480F6'
$crashFqn = 'SharpInspect.Runtime.Tests.ProductionStopCrashTests.V145_D01_KilledHostCannotTurnAnAcceptedStopIntoNormalCompletionOnRestart'
$allowedSourceChanges = @(
    'samples/SharpInspect.SampleHost/DiagnosticFatalProbe.cs',
    'src/SharpInspect.Wpf/ManagedFaultShutdown.cs',
    'tests/SharpInspect.Runtime.Tests/ProductionStopCrashTests.cs',
    'tools/Test-DiagnosticsConsumer.ps1'
)

# The seven solution test projects and their Release TFMs, exactly as `dotnet test SharpInspect.NET.sln -c Release` resolves them.
$assemblies = @(
    [ordered]@{ name = 'SharpInspect.Runtime.Tests';                 tfm = 'net6.0' }
    [ordered]@{ name = 'SharpInspect.Wpf.Tests';                     tfm = 'net6.0-windows' }
    [ordered]@{ name = 'SharpInspect.OpenCvSharp.Tests';             tfm = 'net6.0' }
    [ordered]@{ name = 'SharpInspect.Cameras.Virtual.Tests';         tfm = 'net6.0' }
    [ordered]@{ name = 'SharpInspect.Cameras.Hikrobot.Tests';        tfm = 'net6.0' }
    [ordered]@{ name = 'SharpInspect.Cameras.Conformance.Tests';     tfm = 'net6.0' }
    [ordered]@{ name = 'SharpInspect.Calibration.OpenCvSharp.Tests'; tfm = 'net6.0' }
)

function Get-Sha256Hex([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-JsonFile([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-SourceHashes([string]$Root) {
    # Exact replica of Get-TaskSourceHashes in tools/Test-Development.ps1 (validation.json schema).
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src'), (Join-Path $Root 'tests'),
            (Join-Path $Root 'samples'), (Join-Path $Root 'tools') -Recurse -File) +
        @(Get-Item -LiteralPath (Join-Path $Root 'Directory.Build.props'), (Join-Path $Root 'global.json'),
            (Join-Path $Root 'SharpInspect.NET.sln'))
    $result = @($files | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults)\\' } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($Root, $_.FullName); sha256 = (Get-Sha256Hex $_.FullName) }
        })
    return $result
}

function Get-TreeHashes([string]$Root, [string[]]$RelativeRoots) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($relativeRoot in $RelativeRoots) {
        $treeRoot = Join-Path $Root ($relativeRoot -replace '/', '\')
        if (-not (Test-Path -LiteralPath $treeRoot -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $treeRoot -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            if ($relative -match '(^|/)TestResults/' -or $relative -match '/(bin|obj)/') { continue }
            $list.Add([ordered]@{
                path = $relative
                sha256 = (Get-Sha256Hex $file.FullName)
                length = $file.Length
            })
        }
    }
    return @($list | Sort-Object { $_.path })
}

function Parse-DiscoveredTests([string[]]$Lines) {
    $marker = -1
    for ($i = $Lines.Count - 1; $i -ge 0; $i--) {
        if ($Lines[$i] -match '测试可用|Tests are available') { $marker = $i; break }
    }
    if ($marker -lt 0) { throw 'The ListTests output did not contain the discovery marker.' }
    $tests = [System.Collections.Generic.List[string]]::new()
    for ($i = $marker + 1; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($line -match '^\s{4,}\S') { $tests.Add($line.Trim()); continue }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        break
    }
    return @($tests)
}

function Get-Fqn([string]$DisplayName) {
    if ($DisplayName -match '^([^()]+)\(') { return $Matches[1].Trim() }
    return $DisplayName.Trim()
}

# ---- 1. discovery snapshot -------------------------------------------------
$assemblyReports = [System.Collections.Generic.List[object]]::new()
foreach ($assembly in $assemblies) {
    $dll = Join-Path $RepositoryRoot ('tests\{0}\bin\Release\{1}\{0}.dll' -f $assembly.name, $assembly.tfm)
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Release test assembly not found: $dll" }
    $raw = Join-Path $rawDirectory ('listtests-' + $assembly.name + '.txt')
    $lines = @(& dotnet vstest $dll /ListTests 2>&1 | ForEach-Object { $_.ToString() })
    $exit = $LASTEXITCODE
    [IO.File]::WriteAllText($raw, (($lines -join [Environment]::NewLine) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $tests = Parse-DiscoveredTests $lines
    if ($tests.Count -eq 0) { throw "ListTests discovered no cases for $($assembly.name) (exit $exit)." }
    $entries = @($tests | ForEach-Object {
        [ordered]@{ displayName = $_; fullyQualifiedName = (Get-Fqn $_) }
    })
    $assemblyReports.Add([ordered]@{
        assembly = $assembly.name
        tfm = $assembly.tfm
        dllPath = $dll
        dllSha256 = (Get-Sha256Hex $dll)
        listTestsExitCode = $exit
        rawOutput = $raw
        rawOutputSha256 = (Get-Sha256Hex $raw)
        caseCount = $entries.Count
        v156CaseCount = @($entries | Where-Object { $_.displayName -match 'V156' }).Count
        cases = $entries
    })
    Write-Host ("discovery {0}: {1} cases ({2} V156)" -f $assembly.name, $entries.Count, $assemblyReports[-1].v156CaseCount)
}

$totalCases = ($assemblyReports | Measure-Object -Property caseCount -Sum).Sum
$totalV156 = ($assemblyReports | Measure-Object -Property v156CaseCount -Sum).Sum
if ($totalCases -ne $expectedSolutionCases) {
    throw "Discovery total $totalCases does not match the frozen expectation $expectedSolutionCases."
}
if ($totalV156 -ne $expectedV156Cases) {
    throw "V156 discovery total $totalV156 does not match the frozen expectation $expectedV156Cases."
}
$crashMatches = @($assemblyReports | ForEach-Object { $_.cases } | Where-Object { $_.fullyQualifiedName -ceq $crashFqn })
if ($crashMatches.Count -ne 1) {
    throw "The exact crash case was not discovered exactly once ($($crashMatches.Count))."
}

$discovery = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryRoot = $RepositoryRoot
    expectedSolutionCases = $expectedSolutionCases
    expectedV156Cases = $expectedV156Cases
    totalCases = $totalCases
    totalV156CaseCount = $totalV156
    crashCase = [ordered]@{
        fullyQualifiedName = $crashFqn
        displayName = $crashMatches[0].displayName
        assembly = 'SharpInspect.Runtime.Tests'
        tfm = 'net6.0'
    }
    assemblies = @($assemblyReports)
}
Write-JsonFile (Join-Path $OutDirectory 'baseline-discovery.json') $discovery

# ---- 2. binary dependency snapshot ----------------------------------------
$treeRoots = [System.Collections.Generic.List[string]]::new()
foreach ($projectDirectory in (Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -Directory)) {
    $treeRoots.Add('src/' + $projectDirectory.Name + '/bin/Release')
}
foreach ($projectDirectory in (Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests') -Directory)) {
    $treeRoots.Add('tests/' + $projectDirectory.Name + '/bin/Release')
}
$binaryEntries = Get-TreeHashes $RepositoryRoot $treeRoots
$runtimeCopies = @($binaryEntries | Where-Object { $_.path -match '/SharpInspect\.Runtime\.dll$' })
foreach ($copy in $runtimeCopies) {
    if ($copy.sha256 -cne $actualRuntimeDllSha256) {
        throw ("SharpInspect.Runtime.dll copy does not match the declared baseline hash: {0} -> {1}" -f $copy.path, $copy.sha256)
    }
}
$runtimeDllCopies = @($binaryEntries | Where-Object { $_.path -match '/SharpInspect\.Abstractions\.dll$' })
$distinctRuntime = @($runtimeCopies | Select-Object -ExpandProperty sha256 -Unique)
$distinctAbstractions = @($runtimeDllCopies | Select-Object -ExpandProperty sha256 -Unique)
if ($distinctRuntime.Count -ne 1 -or $distinctAbstractions.Count -ne 1) {
    throw ('Release trees contain inconsistent product dependency copies: Runtime={0} Abstractions={1}' -f
        ($distinctRuntime -join ','), ($distinctAbstractions -join ','))
}
$binaries = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryRoot = $RepositoryRoot
    roots = @($treeRoots)
    fileCount = $binaryEntries.Count
    runtimeDllSha256 = $distinctRuntime[0]
    abstractionsDllSha256 = $distinctAbstractions[0]
    files = @($binaryEntries)
}
Write-JsonFile (Join-Path $OutDirectory 'baseline-binaries.json') $binaries
Write-Host ("binary dependency snapshot: {0} files; Runtime={1}; Abstractions={2}" -f
    $binaryEntries.Count, $distinctRuntime[0], $distinctAbstractions[0])

# ---- 3. source-hash snapshot from run 47 ----------------------------------
$baselineValidationPath = Join-Path $Baseline 'validation.json'
if (-not (Test-Path -LiteralPath $baselineValidationPath -PathType Leaf)) {
    throw "Baseline validation.json not found: $baselineValidationPath"
}
$baselineValidationSha256 = Get-Sha256Hex $baselineValidationPath
[IO.File]::WriteAllText((Join-Path $OutDirectory 'baseline-run47-validation.json'),
    (Get-Content -LiteralPath $baselineValidationPath -Raw), [Text.UTF8Encoding]::new($false))
$baselineValidation = Get-Content -LiteralPath $baselineValidationPath -Raw | ConvertFrom-Json
$baselineSourceHashes = @($baselineValidation.sourceHashes)
$currentSourceHashes = Get-SourceHashes $RepositoryRoot
$baselineComparable = @($baselineSourceHashes | ForEach-Object {
    [ordered]@{ path = $_.path; sha256 = $_.sha256 }
})
$currentComparable = @($currentSourceHashes | ForEach-Object {
    [ordered]@{ path = $_.path; sha256 = $_.sha256 }
})
$engineMatches = (($baselineComparable | ConvertTo-Json -Depth 4 -Compress) -ceq
    ($currentComparable | ConvertTo-Json -Depth 4 -Compress))
if (-not $engineMatches) {
    throw 'Source-hash engine does not reproduce the driver-recorded sourceHashes for the baseline source state.'
}
Write-JsonFile (Join-Path $OutDirectory 'baseline-source-hashes.json') ([ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    baselineRun = $Baseline
    baselineValidationPath = $baselineValidationPath
    baselineValidationSha256 = $baselineValidationSha256
    startingHead = $baselineValidation.startingHead
    startedAtUtc = $baselineValidation.startedAtUtc
    sourceFileCount = $baselineSourceHashes.Count
    sourceHashes = $baselineComparable
})
Write-Host ("source-hash snapshot: {0} files; engine reproduces the driver's recorded hashes" -f $baselineSourceHashes.Count)

# ---- 4. frozen constants ---------------------------------------------------
$constants = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    baselineRun = $Baseline
    expectedSolutionCases = $expectedSolutionCases
    expectedV156Cases = $expectedV156Cases
    actualRuntimeDllSha256 = $actualRuntimeDllSha256
    crashCase = $discovery.crashCase
    allowedSourceChanges = $allowedSourceChanges
    allowedBinaryChangePatterns = @(
        '^src/SharpInspect\.Wpf/bin/Release/net6\.0-windows/SharpInspect\.Wpf\.(dll|pdb)$',
        '^tests/SharpInspect\.Runtime\.Tests/bin/Release/net6\.0/SharpInspect\.Runtime\.Tests\.(dll|pdb)$',
        '^tests/SharpInspect\.Wpf\.Tests/bin/Release/net6\.0-windows/SharpInspect\.Wpf\.Tests\.(dll|pdb)$',
        '^tests/SharpInspect\.Wpf\.Tests/bin/Release/net6\.0-windows/SharpInspect\.Wpf\.(dll|pdb)$'
    )
    testAssemblies = @($assemblies)
}
Write-JsonFile (Join-Path $OutDirectory 'baseline-constants.json') $constants

Write-Host ("BASELINE CAPTURED: {0} cases total, {1} V156, crash case display '{2}'" -f
    $totalCases, $totalV156, $discovery.crashCase.displayName)
