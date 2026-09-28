$ErrorActionPreference = 'Stop'
$taskRoot = 'D:\projects\SharpInspect.NET'
$taskFiles = @(
    'src/SharpInspect.Abstractions/RecipeActivationActor.cs',
    'src/SharpInspect.Abstractions/RecipeActivations.cs',
    'src/SharpInspect.Abstractions/RecipeChangeProtocol.cs',
    'src/SharpInspect.Runtime/Storage/RecipeActivationStorageCodec.cs',
    'src/SharpInspect.Runtime/Identity/IdentityAuditEvent.cs',
    'src/SharpInspect.Runtime/Identity/PlcRecipeActivationIdentityCodec.cs',
    'src/SharpInspect.Runtime/Plc/ModbusRecipeChangeBinding.cs',
    'src/SharpInspect.Runtime/Plc/ModbusProductionProfile.cs',
    'src/SharpInspect.Runtime/Plc/ModbusQualificationChannel.cs',
    'src/SharpInspect.Runtime/Plc/ModbusQualificationChannel.RecipeChange.cs',
    'src/SharpInspect.Runtime/Plc/PlcRecipeChangeHandshake.cs',
    'tests/SharpInspect.Runtime.Tests/RecipeActivationPlcActorCodecTests.cs',
    'tests/SharpInspect.Runtime.Tests/PlcRecipeActivationIdentityTests.cs',
    'tests/SharpInspect.Runtime.Tests/ModbusRecipeChangeTransportTests.cs',
    'tests/SharpInspect.Runtime.Tests/PlcRecipeChangeHandshakeTests.cs'
)
$taskPins = foreach ($taskRelative in $taskFiles) {
    [pscustomobject]@{ path = $taskRelative; sha256 = (Get-FileHash -LiteralPath (Join-Path $taskRoot $taskRelative) -Algorithm SHA256).Hash }
}
$taskPins | ConvertTo-Json | Set-Content -LiteralPath "$taskRoot\.scratch\sharpinspect-v1-tickets\ticket46-foundation-pins.json" -Encoding utf8
Write-Output ('Pinned {0} foundation files for independent review.' -f $taskPins.Count)
