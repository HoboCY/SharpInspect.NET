param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed,
    [Parameter(Mandatory)][string]$Schema32PackageFeed,
    [int[]]$Phases = @(1..14)
)
$ErrorActionPreference = 'Stop'
$migrationRun = [IO.Path]::GetFullPath($Run)
$migrationRoot = Join-Path $migrationRun 'store-migration-consumer'
if (Test-Path -LiteralPath $migrationRoot) { throw 'Use a fresh store migration consumer directory.' }
[void][IO.Directory]::CreateDirectory($migrationRoot)
$migrationCurrentFeed = [IO.Path]::GetFullPath($PackageFeed)
$migrationOldFeed = [IO.Path]::GetFullPath($Schema32PackageFeed)
if ($migrationCurrentFeed -eq $migrationOldFeed) { throw 'The old writer must come from independently preserved schema-32 packages.' }
foreach ($migrationPhase in $Phases) {
    if ($migrationPhase -lt 1 -or $migrationPhase -gt 14) { throw 'Unsupported public migration phase.' }
}

function Build-MigrationConsumer([string]$Name, [string]$Feed, [bool]$Current) {
    $consumer = Join-Path $migrationRoot $Name
    [void][IO.Directory]::CreateDirectory($consumer)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'migration-probe/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
    $define = if ($Current) { 'CURRENT_MIGRATION' } else { '' }
    $xml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <DefineConstants>$define</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="6.0.0" />
  </ItemGroup>
</Project>
"@
    $project = Join-Path $consumer 'MigrationConsumer.csproj'
    [IO.File]::WriteAllText($project, $xml, [Text.UTF8Encoding]::new($false))
    $config = Join-Path $consumer 'NuGet.Config'
    $feedXml = '<configuration><packageSources><clear/><add key="migration-snapshot" value="' +
        [Security.SecurityElement]::Escape($Feed) +
        '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
    [IO.File]::WriteAllText($config, $feedXml, [Text.UTF8Encoding]::new($false))
    & dotnet restore $project --configfile $config --packages (Join-Path $consumer 'cache') *> (Join-Path $consumer 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration consumer restore failed: $consumer" }
    & dotnet build $project -c Release --no-restore -m:1 *> (Join-Path $consumer 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration consumer build failed: $consumer" }
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) {
        throw 'Migration consumer unexpectedly references source projects.'
    }
    $runtime = Join-Path $consumer 'bin/Release/net6.0/SharpInspect.Runtime.dll'
    $package = Join-Path $Feed 'SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg'
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('lib/net6.0/SharpInspect.Runtime.dll')
        if (-not $entry) { throw 'Runtime DLL absent from preserved package.' }
        $stream = $entry.Open()
        try { $packagedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -cne $packagedHash) {
            throw 'The running consumer runtime does not match its declared package.'
        }
    }
    finally { $archive.Dispose() }
    return [ordered]@{ dll=(Join-Path $consumer 'bin/Release/net6.0/MigrationConsumer.dll'); runtime=$runtime;
        runtimeHash=$packagedHash; packageHash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash }
}

function Start-MigrationProbe([string]$Dll, [string[]]$Arguments, [string]$LogPrefix) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($Dll)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    return @{ process=$process; stdout=$process.StandardOutput.ReadToEndAsync();
        stderr=$process.StandardError.ReadToEndAsync(); log=$LogPrefix }
}

function Finish-MigrationProbe($Probe, [bool]$Killed = $false) {
    try {
        if (-not $Probe.process.WaitForExit(30000)) { $Probe.process.Kill($true); throw 'Migration probe process deadline exceeded.' }
        $Probe.stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Probe.log + '.stdout.log') -Encoding utf8
        $Probe.stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Probe.log + '.stderr.log') -Encoding utf8
        if (-not $Killed -and $Probe.process.ExitCode -ne 0) { throw ('Migration probe failed: ' + $Probe.log) }
    }
    finally { $Probe.process.Dispose() }
}

$migrationOld = Build-MigrationConsumer 'old' $migrationOldFeed $false
$migrationNew = Build-MigrationConsumer 'current' $migrationCurrentFeed $true
if ($migrationOld.runtimeHash -ceq $migrationNew.runtimeHash) { throw 'Old and current runtime binaries are identical.' }
$migrationCases = @()
foreach ($migrationPhase in $Phases) {
    $case = Join-Path $migrationRoot ('phase-' + $migrationPhase)
    [void][IO.Directory]::CreateDirectory($case)
    Finish-MigrationProbe (Start-MigrationProbe $migrationOld.dll @('seed',$case) (Join-Path $case 'seed'))
    $seed = Get-Content -LiteralPath (Join-Path $case 'seed-writer.json') -Raw | ConvertFrom-Json
    if ($seed.schema -ne 32 -or $seed.state -ne 'Healthy' -or $seed.audit -ne 'Persisted' -or $seed.Ready -or
        $seed.assemblyHash -cne $migrationOld.runtimeHash) { throw 'Preserved old writer did not create a real writable schema-32 database.' }

    $probe = Start-MigrationProbe $migrationNew.dll @('pause',$case,$migrationOld.runtime,[string]$migrationPhase) (Join-Path $case 'pause')
    $proofPath = Join-Path $case 'pause-phase.json'
    $phaseProof = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline -and -not $probe.process.HasExited) {
            if (Test-Path -LiteralPath $proofPath) {
                try { $phaseProof = Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json }
                catch { $phaseProof = $null }
                if ($phaseProof) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $phaseProof -or $phaseProof.phase -ne $migrationPhase -or $phaseProof.Ready) {
            throw 'The public maintenance phase was not observed before interruption.'
        }
        $probe.process.Kill($true)
    }
    finally {
        if (-not $probe.process.HasExited) { $probe.process.Kill($true) }
        Finish-MigrationProbe $probe $true
    }
    Finish-MigrationProbe (Start-MigrationProbe $migrationNew.dll @('migrate',$case,$migrationOld.runtime,'14') (Join-Path $case 'resume'))
    $resumed = Get-Content -LiteralPath (Join-Path $case 'migrate-phase.json') -Raw | ConvertFrom-Json
    if (-not $resumed.Completed -or $resumed.Ready -or $resumed.schema -ne 33 -or
        $resumed.OperationId -ne $phaseProof.OperationId -or -not $resumed.Backup) { throw 'Cold resume did not prove the target generation and verified backup.' }
    $database = Join-Path $case 'store.sqlite'
    $before = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
    Finish-MigrationProbe (Start-MigrationProbe $migrationOld.dll @('old-reopen',$case) (Join-Path $case 'old-reopen'))
    $denied = Get-Content -LiteralPath (Join-Path $case 'old-reopen-writer.json') -Raw | ConvertFrom-Json
    if ($denied.state -ne 'Faulted' -or $denied.audit -eq 'Persisted' -or $denied.schema -ne 33 -or $denied.Ready -or
        $denied.assemblyHash -cne $migrationOld.runtimeHash -or
        (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -cne $before) { throw 'The actual old binary did not refuse the newer database without modifying it.' }
    Finish-MigrationProbe (Start-MigrationProbe $migrationNew.dll @('current-reopen',$case) (Join-Path $case 'current-reopen'))
    $current = Get-Content -LiteralPath (Join-Path $case 'current-reopen-writer.json') -Raw | ConvertFrom-Json
    if ($current.state -ne 'Healthy' -or $current.audit -ne 'Persisted' -or $current.schema -ne 33 -or $current.Ready) {
        throw 'The current public writer did not reopen the completed migration.'
    }
    $migrationCases += [ordered]@{ id='V149_N02'; phase=$migrationPhase; result='Pass'; interruptedBy='Process.Kill(entireProcessTree:true)';
        operation=$resumed.OperationId; backup=$resumed.Backup; oldWriterReason=$denied.reason; oldDatabaseUnchanged=$true }
    Write-Output "V149 phase $migrationPhase crash/resume and actual old writer refusal PASS"
}
$migrationEvidence = [ordered]@{ id='V149_N01'; result='Pass'; scope='isolated package consumers, real SQLite backup, process crashes; no physical station or deployment handoff';
    old=$migrationOld; current=$migrationNew; cases=$migrationCases; completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
$migrationEvidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $migrationRun 'store-migration-consumer.json') -Encoding utf8
