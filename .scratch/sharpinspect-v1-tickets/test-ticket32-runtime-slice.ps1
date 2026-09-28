$ErrorActionPreference = 'Stop'
$taskSource = 'D:\projects\SharpInspect.NET'
$taskEvidence = 'F:\temp\SharpInspect.NET-validation-artifacts\ticket32'
$taskStage = Join-Path $taskEvidence 'runtime-slice-20260909'
$taskZip = Join-Path $taskEvidence 'runtime-slice-t31-base.zip'
if (Test-Path -LiteralPath $taskStage) { throw 'SliceStageAlreadyExists' }
git archive --format=zip --output=$taskZip b2e1dc7c1504fd21ba8932d37884fe8fea949fce
if ($LASTEXITCODE -ne 0) { throw 'BaseArchiveFailed' }
Expand-Archive -LiteralPath $taskZip -DestinationPath $taskStage
$taskPaths = @(
 'src/SharpInspect.Abstractions/PartIdentityRequirements.cs',
 'src/SharpInspect.Abstractions/RecipeActivations.cs',
 'src/SharpInspect.Abstractions/CommandTrace.cs',
 'src/SharpInspect.Abstractions/RecipeDrafts.cs',
 'src/SharpInspect.Runtime/Storage/RecipeDraftStorageCodec.cs',
 'src/SharpInspect.Runtime/Storage/RecipeActivationStoreOptions.cs',
 'src/SharpInspect.Runtime/Recipes/AlgorithmConfigurationMigrationService.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeReleaseProjection.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeActivationCalibrationEvaluator.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeActivationRuntimeLease.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeActivationChecks.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeActivationPreparation.cs',
 'src/SharpInspect.Runtime/Recipes/RecipeActivationExecution.cs',
 'src/SharpInspect.Runtime/Cameras/CameraSetupRuntime.cs',
 'src/SharpInspect.Runtime/Cameras/CameraSetupRuntime.Activation.cs',
 'src/SharpInspect.Runtime/Cameras/CameraSetupRuntime.ImagingSetup.cs',
 'src/SharpInspect.Runtime/StationRuntime.cs',
 'src/SharpInspect.Runtime/StationRuntime.RecipeActivation.cs',
 'src/SharpInspect.Runtime/StationRuntime.CameraSetup.cs',
 'src/SharpInspect.Runtime/StationRuntime.CameraNetwork.cs',
 'src/SharpInspect.Runtime/StationRuntime.Calibration.cs',
 'src/SharpInspect.Runtime/StationRuntime.PlcResultContract.cs',
 'src/SharpInspect.Runtime/StationRuntime.AdministratorRecovery.cs',
 'tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj',
 'tests/SharpInspect.Runtime.Tests/CalibrationValidityTests.cs',
 'tests/SharpInspect.Runtime.Tests/RecipeDraftStorageCodecTests.cs',
 'tests/SharpInspect.Runtime.Tests/RecipeActivationCameraTests.cs',
 'tests/SharpInspect.Runtime.Tests/RecipeActivationCalibrationTests.cs',
 'tests/SharpInspect.Runtime.Tests/RecipeActivationRuntimeReservationTests.cs'
)
$taskPaths += @(Get-ChildItem -LiteralPath (Join-Path $taskSource 'tests/SharpInspect.Runtime.Tests/Fixtures/recipe-draft-legacy-t31') -File |
 ForEach-Object { 'tests/SharpInspect.Runtime.Tests/Fixtures/recipe-draft-legacy-t31/' + $_.Name })
$taskManifest = foreach ($taskPath in $taskPaths) {
    $taskDestination = Join-Path $taskStage $taskPath
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskDestination))
    Copy-Item -LiteralPath (Join-Path $taskSource $taskPath) -Destination $taskDestination
    [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash}
}
[IO.File]::WriteAllText((Join-Path $taskStage 'slice-manifest.json'), ([ordered]@{
    basis='T31 b2e1dc7 plus listed completed T32 runtime/camera/contract slice; excludes unfinished schema18 integration.'
    scope='Focused owned-slice validation only. Not the T32 final integration gate.'
    files=@($taskManifest)
} | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$env:MSBUILDDISABLENODEREUSE='1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
$env:UseSharedCompilation='false'
$taskTemp = Join-Path $taskEvidence 'process-temp'
[void][IO.Directory]::CreateDirectory($taskTemp)
$env:TEMP = $taskTemp
$env:TMP = $taskTemp
Push-Location $taskStage
try {
    dotnet test tests/SharpInspect.Runtime.Tests/SharpInspect.Runtime.Tests.csproj --filter 'FullyQualifiedName~RecipeDraftStorageCodecTests|FullyQualifiedName~RecipeActivationCameraTests|FullyQualifiedName~RecipeActivationCalibrationTests|FullyQualifiedName~RecipeActivationRuntimeReservationTests' -p:UseSharedCompilation=false -nodeReuse:false *> (Join-Path $taskEvidence '04-runtime-slice.log')
    $taskCode = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $taskEvidence '04-runtime-slice.log') -Tail 30
    exit $taskCode
}
finally { Pop-Location }
