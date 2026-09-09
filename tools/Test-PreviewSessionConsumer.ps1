param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed
)
$ErrorActionPreference = 'Stop'
$previewRepo = Split-Path -Parent $PSScriptRoot
$previewRun = [IO.Path]::GetFullPath($Run)
$previewFeed = [IO.Path]::GetFullPath($PackageFeed)
$previewCopy = Join-Path $previewRun 'preview-session-consumer'
$previewEvidence = Join-Path $previewRun 'preview-session-demo'
if (Test-Path -LiteralPath $previewCopy) { throw 'Use a fresh Preview consumer validation directory.' }
[void][IO.Directory]::CreateDirectory($previewCopy)
[void][IO.Directory]::CreateDirectory($previewEvidence)
$previewSource = Join-Path $previewRepo 'samples\SharpInspect.SampleHost'
foreach ($previewFile in Get-ChildItem -LiteralPath $previewSource -Recurse -File) {
    $previewRelative = [IO.Path]::GetRelativePath($previewSource, $previewFile.FullName)
    if ($previewRelative -match '^(bin|obj|TestResults)[\\/]') { continue }
    $previewTarget = Join-Path $previewCopy $previewRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $previewTarget))
    Copy-Item -LiteralPath $previewFile.FullName -Destination $previewTarget
}
Copy-Item -LiteralPath (Join-Path $previewRepo 'Directory.Build.props') -Destination $previewCopy
$previewProject = Join-Path $previewCopy 'SharpInspect.SampleHost.csproj'
$previewConfig = Join-Path $previewCopy 'NuGet.Config'
$previewXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($previewFeed) +
    '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
[IO.File]::WriteAllText($previewConfig, $previewXml, [Text.UTF8Encoding]::new($false))
function Invoke-PreviewConsumerDotnet([string]$LogName, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $previewRun $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LogName (exit $LASTEXITCODE)" }
}
Invoke-PreviewConsumerDotnet 'preview-consumer-restore.log' @('restore', $previewProject,
    '-p:UseLocalPackages=true', '--configfile', $previewConfig,
    '--packages', (Join-Path $previewRun 'preview-consumer-cache'))
Invoke-PreviewConsumerDotnet 'preview-consumer-build.log' @('build', $previewProject,
    '-c', 'Release', '-p:UseLocalPackages=true', '--no-restore')
$previewAssets = Get-Content -LiteralPath (Join-Path $previewCopy 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($previewAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Preview consumer still references a source project.'
}
foreach ($previewPackage in 'Abstractions', 'Runtime', 'Wpf', 'Cameras.Virtual', 'OpenCvSharp') {
    if (-not $previewAssets.libraries.PSObject.Properties[('SharpInspect.NET.' + $previewPackage + '/0.1.0-dev.1')]) {
        throw "Preview consumer package is missing: $previewPackage"
    }
}
$previewDll = Join-Path $previewCopy 'bin\Release\net6.0-windows\SharpInspect.SampleHost.dll'
$previewPreviousConsumer = [Environment]::GetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_CONSUMER', 'Process')
$previewPreviousEvidence = [Environment]::GetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_EVIDENCE_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_CONSUMER', $previewDll, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_EVIDENCE_ROOT', $previewEvidence, 'Process')
    Invoke-PreviewConsumerDotnet 'preview-consumer-tests.log' @('test',
        (Join-Path $previewRepo 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj'),
        '-c', 'Release', '--no-build', '--no-restore', '--filter',
        'FullyQualifiedName~PreviewSessionConsumerAcceptanceTests', '--logger', 'trx',
        '--results-directory', (Join-Path $previewRun 'preview-consumer-tests'))
    foreach ($previewName in 'preview-evidence.json', 'acceptance.json', 'process.log') {
        $previewArtifact = Join-Path $previewEvidence $previewName
        if (-not (Test-Path -LiteralPath $previewArtifact -PathType Leaf) -or
            (Get-Item -LiteralPath $previewArtifact).Length -eq 0) {
            throw "Preview evidence is missing or empty: $previewName"
        }
    }
    $previewDetail = Get-Content -LiteralPath (Join-Path $previewEvidence 'preview-evidence.json') -Raw | ConvertFrom-Json
    $previewHash = (Get-FileHash -LiteralPath $previewDll -Algorithm SHA256).Hash
    if ($previewDetail.Result -cne 'Pass' -or $previewDetail.ExternalNuGetConsumer -cne $true -or
        $previewDetail.ConsumerSha256 -cne $previewHash -or
        $previewDetail.Restoration -cne 'NoActiveBaselineClosed' -or
        $previewDetail.SavedDraftRevision -le $previewDetail.InitialDraftRevision) {
        throw 'Preview public package evidence does not prove the required session and Draft transition.'
    }
    Write-Output 'V133_N01 Preview public NuGet consumer PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_CONSUMER', $previewPreviousConsumer, 'Process')
    [Environment]::SetEnvironmentVariable('SHARPINSPECT_PREVIEW_SESSION_EVIDENCE_ROOT', $previewPreviousEvidence, 'Process')
}
