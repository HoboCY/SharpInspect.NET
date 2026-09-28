$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$taskUtf8=New-Object Text.UTF8Encoding($false)
$taskRows=@()
foreach($name in @('core','devices','operations','evidence','release')){
    $taskRows+=@(Get-Content -LiteralPath (Join-Path $taskRoot ('tickets-'+$name+'.json')) -Raw | ConvertFrom-Json)
}
$by=@{};foreach($row in $taskRows){if($by.ContainsKey($row.key)){throw "Duplicate key $($row.key)"};$by[$row.key]=$row}
$ordered=[Collections.Generic.List[object]]::new();$done=@{};$anc=@{}
while($ordered.Count -lt $taskRows.Count){
    $candidate=$null
    foreach($row in $taskRows){
        if($done.ContainsKey($row.key)){continue}
        if(@($row.deps | Where-Object {-not $done.ContainsKey($_)}).Count -eq 0){$candidate=$row;break}
    }
    if(-not $candidate){throw 'Missing dependency or cycle'}
    $set=[Collections.Generic.HashSet[string]]::new()
    foreach($d in $candidate.deps){[void]$set.Add($d);foreach($a in $anc[$d]){[void]$set.Add($a)}}
    $anc[$candidate.key]=$set
    $candidate | Add-Member -NotePropertyName ordinal -NotePropertyValue ($ordered.Count+1) -Force
    $ordered.Add($candidate);$done[$candidate.key]=$true
}
$removed=@()
foreach($row in $ordered){
    $original=@($row.deps);$keep=@()
    foreach($d in $original){
        $redundant=$false
        foreach($other in $original){if($d -ne $other -and $anc[$other].Contains($d)){$redundant=$true;break}}
        if($redundant){$removed+=,@($row.key,$d)}else{$keep+=$d}
    }
    $row.deps=$keep
}
$coverage=@{}
foreach($definition in @(@('adrs',111,''),@('stories',82,''),@('groups',23,'D'),@('tests',25,'T'))){
    $field=$definition[0];$max=[int]$definition[1];$prefix=$definition[2]
    $actual=@($taskRows | ForEach-Object {$_.$field} | Sort-Object -Unique)
    $missing=@()
    foreach($i in 1..$max){$item=if($prefix){'{0}{1:d2}' -f $prefix,$i}else{$i};if($item -notin $actual){$missing+=$item}}
    if($missing.Count){throw "Missing $field : $($missing -join ',')"}
    $coverage[$field]=@()
}
$post=@('full-conformance-profile','framework-qualification','hardware-provider-qualification','release-package-finalize','station-performance-qualification','power-loss-qualification','station-functional-qualification','station-acceptance')
$orphans=@($taskRows | Where-Object { $_.key -notin $post -and -not $anc['full-conformance-profile'].Contains($_.key) })
if($orphans.Count){throw "Implementation omitted by complete profile: $($orphans.key -join ',')"}
$manifest=[ordered]@{parent=1;repo='HoboCY/SharpInspect.NET';tickets=@($ordered.ToArray());redundantEdgesRemoved=$removed}
[IO.File]::WriteAllText((Join-Path $taskRoot 'manifest.json'),(ConvertTo-Json -InputObject $manifest -Depth 30),$taskUtf8)
$report=[ordered]@{count=$ordered.Count;edges=@($ordered | ForEach-Object {$_.deps}).Count;acyclic=$true;coverageMissing=$coverage;unmappedImplementationLeaves=@();frontier=@($ordered | Where-Object {@($_.deps).Count -eq 0} | ForEach-Object {$_.key})}
[IO.File]::WriteAllText((Join-Path $taskRoot 'plan-validation.json'),(ConvertTo-Json -InputObject $report -Depth 10),$taskUtf8)
$report | ConvertTo-Json -Depth 10 -Compress
