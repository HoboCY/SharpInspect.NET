$ErrorActionPreference='Stop'
$taskRun=(Resolve-Path -LiteralPath 'F:\si58v22\ticket58\20260916-190223-743').Path.TrimEnd('\')
$taskInventory=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 't58-restore-cache-inventory.json') -Raw | ConvertFrom-Json)
$taskReferenced=@((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cache-evidence-reference-hits.json') -Raw | ConvertFrom-Json).cache | Sort-Object -Unique)
$taskProtected=@('.scratch/t58/final-evidence.json','.scratch/t58/publication.json',(Join-Path $taskRun 'validation.json'))
$taskBefore=@(foreach($taskFile in $taskProtected){[pscustomobject]@{path=$taskFile;sha256=(Get-FileHash -LiteralPath $taskFile).Hash}})
$taskTargets=@(foreach($taskCache in $taskInventory){
 if($taskCache.path -in $taskReferenced){continue}
 $taskTarget=(Resolve-Path -LiteralPath $taskCache.path).Path.TrimEnd('\')
 if(-not $taskTarget.StartsWith($taskRun+'\',[StringComparison]::OrdinalIgnoreCase) -or $taskTarget -eq (Join-Path $taskRun 'packages')){throw 'Cache target escaped the exact run or selected the release feed.'}
 $taskItem=Get-Item -LiteralPath $taskTarget
 while($taskItem.FullName.StartsWith($taskRun,[StringComparison]::OrdinalIgnoreCase)) {
  if($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cache ancestor is a reparse point.'}
  $taskItem=$taskItem.Parent
 }
 $taskChildren=@(Get-ChildItem -LiteralPath $taskTarget -Recurse -Force)
 if(@($taskChildren | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count){throw 'Cache contains a reparse point.'}
 if(-not @($taskChildren | Where-Object Name -eq '.nupkg.metadata').Count){throw 'Target is not a populated restored NuGet cache.'}
 [pscustomobject]@{path=$taskTarget;bytes=$taskCache.bytes}
})
if($taskTargets.Count -ne 39){throw 'Exact cache deletion denominator changed.'}
$taskTargets | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'cache-cleanup-targets.json')
foreach($taskTarget in $taskTargets){Remove-Item -LiteralPath $taskTarget.path -Recurse -Force}
foreach($taskTarget in $taskTargets){if(Test-Path -LiteralPath $taskTarget.path){throw 'Cache deletion incomplete.'}}
foreach($taskFile in $taskBefore){if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256){throw 'Pinned evidence changed.'}}
foreach($taskCache in $taskReferenced){if(-not (Test-Path -LiteralPath $taskCache)){throw 'Referenced evidence cache was removed.'}}
$taskCollector=Join-Path (Split-Path $PSScriptRoot -Parent) 't58/collect-evidence.ps1'
$taskReadback=Join-Path (Split-Path $PSScriptRoot -Parent) 't58/collect-cache-readback59.ps1'
$taskText=Get-Content -LiteralPath $taskCollector -Raw
if(-not $taskText.Contains("'final-evidence.json'")){throw 'Collector output anchor missing.'}
[IO.File]::WriteAllText($taskReadback,$taskText.Replace("'final-evidence.json'","'cache-cleanup-readback59.json'"))
& $taskReadback -Run $taskRun
foreach($taskFile in $taskBefore){if((Get-FileHash -LiteralPath $taskFile.path).Hash -cne $taskFile.sha256){throw 'Readback changed pinned evidence.'}}
[ordered]@{result='Pass';deletedCaches=$taskTargets.Count;deletedBytes=($taskTargets|Measure-Object bytes -Sum).Sum;preservedReferencedCaches=$taskReferenced;originalEvidence=$taskBefore;freeBytes=(Get-PSDrive F).Free;completedUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'cache-cleanup-result.json')
