param([Parameter(Mandatory)][string]$LibraryPath, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($LibraryPath) -or -not [IO.Path]::IsPathFullyQualified($OutputPath)) {
    throw 'Absolute paths required.'
}
$taskStream = [IO.FileStream]::new($LibraryPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
if ($taskStream.Length -gt 64MB) { throw 'Runtime image exceeds the inspection bound.' }
$taskReader = [IO.BinaryReader]::new($taskStream,[Text.Encoding]::UTF8,$true)
try { $taskBytes = $taskReader.ReadBytes([int]$taskStream.Length) }
finally { $taskReader.Dispose() }
function Read-TaskU16([int]$Offset) { [BitConverter]::ToUInt16($taskBytes,$Offset) }
function Read-TaskU32([int]$Offset) { [BitConverter]::ToUInt32($taskBytes,$Offset) }
if ($taskBytes.Length -lt 256 -or (Read-TaskU16 0) -ne 0x5A4D) { throw 'DOS header missing.' }
$taskPeOffset = [int](Read-TaskU32 0x3C)
if ((Read-TaskU32 $taskPeOffset) -ne 0x4550) { throw 'PE signature missing.' }
$taskMachine = Read-TaskU16 ($taskPeOffset + 4)
$taskSectionCount = Read-TaskU16 ($taskPeOffset + 6)
$taskOptionalSize = Read-TaskU16 ($taskPeOffset + 20)
$taskOptional = $taskPeOffset + 24
if ((Read-TaskU16 $taskOptional) -ne 0x20B -or $taskMachine -ne 0x8664) { throw 'AMD64 PE32+ required.' }
if ($taskSectionCount -gt 96) { throw 'Section count exceeds inspection bound.' }
$taskSections = @()
for ($taskIndex = 0; $taskIndex -lt $taskSectionCount; $taskIndex++) {
    $taskSection = $taskOptional + $taskOptionalSize + $taskIndex * 40
    $taskSections += [pscustomobject]@{ virtualSize=Read-TaskU32 ($taskSection+8); rva=Read-TaskU32 ($taskSection+12);
        rawSize=Read-TaskU32 ($taskSection+16); rawOffset=Read-TaskU32 ($taskSection+20) }
}
function Convert-TaskRva([uint32]$Rva) {
    foreach ($taskSection in $taskSections) {
        if ($Rva -ge $taskSection.rva -and $Rva -lt ($taskSection.rva + $taskSection.rawSize)) {
            return [int]($taskSection.rawOffset + $Rva - $taskSection.rva)
        }
    }
    throw 'Export RVA has no file-backed section.'
}
$taskExport = Convert-TaskRva (Read-TaskU32 ($taskOptional + 112))
$taskNamesCount = Read-TaskU32 ($taskExport+24)
if ($taskNamesCount -gt 4096) { throw 'Export count exceeds inspection bound.' }
$taskNamesTable = Convert-TaskRva (Read-TaskU32 ($taskExport+32))
$taskExports = @()
for ($taskIndex = 0; $taskIndex -lt $taskNamesCount; $taskIndex++) {
    $taskName = Convert-TaskRva (Read-TaskU32 ($taskNamesTable + $taskIndex * 4))
    $taskLength = 0
    while ($taskLength -lt 256 -and $taskBytes[$taskName+$taskLength] -ne 0) { $taskLength++ }
    if ($taskLength -eq 256) { throw 'Export name exceeds inspection bound.' }
    $taskExports += [Text.Encoding]::ASCII.GetString($taskBytes,$taskName,$taskLength)
}
$taskApiSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/SharpInspect.Cameras.Hikrobot/HikrobotNativeApi.cs') -Raw
$taskRequired = @([regex]::Matches($taskApiSource,'Export<[^>]+>\("(MV_CC_[^"]+)"\)') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$taskMissing = @($taskRequired | Where-Object { $_ -cnotin $taskExports })
$taskSignature = Get-AuthenticodeSignature -LiteralPath $LibraryPath
$taskVersion = (Get-Item -LiteralPath $LibraryPath).VersionInfo
$taskSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskBytes))
# S02 binds the exact signed candidate retained in hikrobot-sdk-sources.md.
# This engineering check does not extend the empty production catalog.
$taskExpectedSha256 = '98CA43BD9B4872B4D2F1C09822B0FBCEBF43AFE6BC0140B0BEB077742454C3C2'
$taskPassed = $taskMissing.Count -eq 0 -and $taskRequired.Count -eq 22 -and
    $taskSignature.Status -eq 'Valid' -and $taskVersion.FileVersion -ceq '4.8.1.2' -and
    $taskSha256 -ceq $taskExpectedSha256
[ordered]@{ verificationId='V121-S02'; kind='StaticFileInspection'; nativeSdkLoaded=$false; deviceAccess='NotRun';
    result=$(if ($taskPassed) { 'Pass' } else { 'Fail' });
    hardwareQualification='NotRun'; fileVersion=$taskVersion.FileVersion; productVersion=$taskVersion.ProductVersion;
    machine='AMD64'; sha256=$taskSha256; expectedSha256=$taskExpectedSha256;
    signatureStatus=[string]$taskSignature.Status; signer=$taskSignature.SignerCertificate.Subject;
    exportCount=$taskNamesCount; requiredExports=$taskRequired; missingExports=$taskMissing } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
if (-not $taskPassed) { throw 'Frozen candidate hash, version, valid signature or required exports failed; see static inspection evidence.' }
Write-Output ('V121-S02 static PE inspection PASS requiredExports=' + $taskRequired.Count + ' nativeSdkLoaded=false')
}
finally { $taskStream.Dispose() }
