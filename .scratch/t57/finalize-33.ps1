$ErrorActionPreference='Stop'
$taskRepo='D:\projects\SharpInspect.NET'
$taskRun='F:\si57c31\ticket57\20260916-160130-463'
$taskOriginalPath=Join-Path $taskRun 'validation.json'
$taskOriginal=Get-Content -LiteralPath $taskOriginalPath -Raw | ConvertFrom-Json
if($taskOriginal.result -cne 'Fail' -or $taskOriginal.kind -cne 'V1-57-composite-development-validation') {throw 'Original continuation failure must be retained.'}
$taskFiles=@(Get-ChildItem -LiteralPath src,tests,samples,tools -Recurse -File)+@(Get-Item -LiteralPath Directory.Build.props,global.json,SharpInspect.NET.sln)
$taskCurrent=@($taskFiles | Where-Object {$_.FullName -notmatch '\\(bin|obj|TestResults)\\'} | Sort-Object FullName | ForEach-Object {
    [pscustomobject][ordered]@{path=[IO.Path]::GetRelativePath($taskRepo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if(($taskCurrent.path -join '|') -cne ($taskOriginal.sourceHashes.path -join '|')) {throw 'Source file set changed.'}
$taskChanged=@()
for($taskIndex=0;$taskIndex -lt $taskCurrent.Count;$taskIndex++) {
    if($taskCurrent[$taskIndex].sha256 -cne $taskOriginal.sourceHashes[$taskIndex].sha256) {
        $taskChanged += [ordered]@{path=$taskCurrent[$taskIndex].path;before=$taskOriginal.sourceHashes[$taskIndex].sha256;after=$taskCurrent[$taskIndex].sha256}
    }
}
if($taskChanged.Count -ne 1 -or $taskChanged[0].path -cne 'tools\Test-PerformanceConsumer.ps1') {throw 'Only the diagnosed validation-script repair is authorized for this composite.'}
$taskPerformancePath='F:\si57perf33\performance-consumer.json'
$taskPerformance=Get-Content -LiteralPath $taskPerformancePath -Raw | ConvertFrom-Json
$taskRawPath=Join-Path $taskRun 'performance-runtime-evidence/fixed-virtual-runtime.performance.json'
$taskRaw=Get-Content -LiteralPath $taskRawPath -Raw | ConvertFrom-Json
if($taskPerformance.result -cne 'Pass' -or $taskPerformance.runtimeEvidenceSha256 -cne (Get-FileHash -LiteralPath $taskRawPath).Hash -or
    $taskRaw.Raw.Header.FrameworkAssemblyHash -cne $taskPerformance.hashes.RuntimeLoadedIdentity -or
    $taskRaw.Raw.Header.AbstractionsAssemblyHash -cne $taskPerformance.hashes.AbstractionsLoadedIdentity) {throw 'Corrected independent assembly identity readback is missing.'}
$taskFinalPath=Join-Path $PSScriptRoot 'final-validation33.json'
if(Test-Path -LiteralPath $taskFinalPath) {throw 'Final composite must not overwrite prior evidence.'}
$taskOriginal.result='PendingEvidenceAudit'
$taskOriginal.sourceHashes=$taskCurrent
$taskOriginal.validationMethod='Full run and one mandatory recheck per original failed identity; unchanged product/test DLLs and packages; all remaining original driver checks retained; only the final consumer script identity-hash comparison was repaired and independently validated against the same raw capture.'
$taskOriginal | Add-Member -NotePropertyName continuationFailure -NotePropertyValue ([ordered]@{path=$taskOriginalPath;sha256=(Get-FileHash -LiteralPath $taskOriginalPath).Hash;result='Fail';cause='Loaded assembly canonical identity was compared to file SHA-256';changedFiles=$taskChanged})
$taskOriginal | Add-Member -NotePropertyName correctedConsumer -NotePropertyValue ([ordered]@{path=$taskPerformancePath;sha256=(Get-FileHash -LiteralPath $taskPerformancePath).Hash;rawEvidenceSha256=$taskPerformance.runtimeEvidenceSha256})
$taskOriginal | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $taskFinalPath -Encoding utf8
& (Join-Path $PSScriptRoot 'collect-evidence.ps1') -Run $taskRun -CoveragePath (Join-Path $taskRun 'coverage.json') -ManifestPath $taskFinalPath -PerformancePath $taskPerformancePath -FinalizeManifest
