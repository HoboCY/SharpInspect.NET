param()
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskRun = Join-Path $taskRepo ('artifacts\ticket01\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($taskRun)

function Invoke-TaskDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $taskRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
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
        kind = 'V1-01-development-validation'
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
    $taskConfigXml = '<configuration><packageSources><clear/><add key="ticket01" value="' +
        [Security.SecurityElement]::Escape($taskFeed) +
        '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
    [IO.File]::WriteAllText($taskNugetConfig, $taskConfigXml, [Text.UTF8Encoding]::new($false))
    Invoke-TaskDotnet 'consumer-restore.log' @('restore',$taskConsumerProject,'-p:UseLocalPackages=true',
        '--configfile',$taskNugetConfig,'--packages',(Join-Path $taskRun 'consumer-cache'))
    Invoke-TaskDotnet 'consumer-build.log' @('build',$taskConsumerProject,'-c','Release','-p:UseLocalPackages=true','--no-restore')
    $taskConsumerDll = Join-Path $taskConsumer 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
    Invoke-TaskDotnet 'consumer-smoke.log' @($taskConsumerDll,'--smoke','--screenshot',(Join-Path $taskRun 'consumer-window.png'))
    $taskFinalHashes = @(Get-TaskSourceHashes)
    if (($taskFinalHashes | ConvertTo-Json -Depth 4 -Compress) -cne
        ($taskEvidence.sourceHashes | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Source path set or content changed during validation; results are not bound to a single source state.'
    }
    $taskEvidence.result = 'Pass'
    $taskEvidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    Write-Output "V101 VALIDATION PASS: $taskRun"
}
catch {
    if ($null -ne $taskEvidence) {
        $taskEvidence.result = 'Fail'
        $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Encoding utf8
    }
    throw
}
finally { Pop-Location }
