$ErrorActionPreference = 'Stop'
$taskArchive = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket31\20260909-130540-409'
$taskLib = Join-Path $taskArchive 'consumer\bin\Release\net6.0-windows'
$taskOut = 'D:\projects\SharpInspect.NET\tests\SharpInspect.Runtime.Tests\Fixtures\recipe-draft-legacy-t31'
[void][IO.Directory]::CreateDirectory($taskOut)
$taskAbstract = [Reflection.Assembly]::LoadFrom((Join-Path $taskLib 'SharpInspect.Abstractions.dll'))
$taskRuntime = [Reflection.Assembly]::LoadFrom((Join-Path $taskLib 'SharpInspect.Runtime.dll'))
$taskTestsPath = Join-Path $taskArchive 'tested-assemblies\SharpInspect.Runtime.Tests.dll'
$taskTests = [Reflection.Assembly]::LoadFrom($taskTestsPath)
$taskFlags = [Reflection.BindingFlags]'Static, NonPublic, Public'
$taskCodec = $taskRuntime.GetType('SharpInspect.Runtime.Storage.RecipeDraftStorageCodec', $true)
$taskEncode = $taskCodec.GetMethods($taskFlags) | Where-Object Name -eq 'TryEncodeContent'
$taskCommon = $taskTests.GetType('SharpInspect.Runtime.Tests.RecipeDraftStorageCodecTests', $true)
$taskCalibration = $taskTests.GetType('SharpInspect.Runtime.Tests.CalibrationRequirementTests', $true)
$taskMigration = $taskTests.GetType('SharpInspect.Runtime.Tests.RecipeDraftMigrationStorageTests', $true)
$taskV2 = $taskCommon.GetMethod('WithExtension', $taskFlags).Invoke($null, @())
$taskKind = [Enum]::Parse($taskAbstract.GetType('SharpInspect.Abstractions.CalibrationKind'), 'Intrinsic')
$taskRequirement = $taskCalibration.GetMethod('Requirement', $taskFlags).Invoke($null, @($taskKind, 'Inspection'))
$taskRequirementArray = [Array]::CreateInstance($taskRequirement.GetType(), 1)
$taskRequirementArray.SetValue($taskRequirement, 0)
$taskV3 = $taskCalibration.GetMethod('Content', $taskFlags).Invoke($null, @($taskRequirementArray, $false))
$taskV4 = $taskMigration.GetMethod('CreateMigratedContent', $taskFlags).Invoke($null, @($false, $false))
$taskRows = @()
foreach ($taskCase in @(@{Version=2;Content=$taskV2}, @{Version=3;Content=$taskV3}, @{Version=4;Content=$taskV4})) {
    $taskArguments = [object[]]@($taskCase.Content, $null, $null)
    if (-not $taskEncode.Invoke($null, $taskArguments)) { throw $taskArguments[2] }
    $taskDocument = $taskArguments[1]
    $taskPayload = $taskDocument.GetType().GetProperty('PayloadJson', [Reflection.BindingFlags]'Instance, Public, NonPublic').GetValue($taskDocument)
    $taskPayloadHash = $taskDocument.GetType().GetProperty('PayloadHash', [Reflection.BindingFlags]'Instance, Public, NonPublic').GetValue($taskDocument)
    $taskName = 'format-' + $taskCase.Version + '.json'
    [IO.File]::WriteAllText((Join-Path $taskOut $taskName), $taskPayload, [Text.UTF8Encoding]::new($false))
    $taskRows += [ordered]@{format=$taskCase.Version;file=$taskName;payloadSha256=$taskPayloadHash;contentHash=$taskCase.Content.ContentHash}
}
$taskManifest = [ordered]@{
    sourceCommit='b2e1dc7c1504fd21ba8932d37884fe8fea949fce'
    sourceTree='bc066eb76f55136483d60003cbd2e27f0562d74c'
    provenance='Encoded by the archived T31 Runtime and Abstractions assemblies using archived T31 test builders. Format 4 preserves the generated migration IDs as immutable fixture data.'
    assemblies=@('SharpInspect.Abstractions.dll','SharpInspect.Runtime.dll') | ForEach-Object {
        [ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $taskLib $_) -Algorithm SHA256).Hash}
    }
    testAssemblySha256=(Get-FileHash -LiteralPath $taskTestsPath -Algorithm SHA256).Hash
    fixtures=$taskRows
}
[IO.File]::WriteAllText((Join-Path $taskOut 'manifest.json'), ($taskManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$taskRows | ConvertTo-Json -Depth 4
