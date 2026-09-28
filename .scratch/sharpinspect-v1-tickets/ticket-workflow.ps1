param(
    [ValidateSet('Draft','Publish','Link','Verify')][string]$Mode = 'Draft',
    [int]$Limit = 0
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskRepo = 'HoboCY/SharpInspect.NET'
$taskUtf8 = New-Object System.Text.UTF8Encoding($false)
$taskManifestPath = Join-Path $taskRoot 'manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
$taskTickets = @($taskManifest.tickets)
$taskByKey = @{}
foreach ($ticket in $taskTickets) { $taskByKey[$ticket.key] = $ticket }
$taskPublicationPath = Join-Path $taskRoot 'publication.json'
$taskPublished = @{}
if (Test-Path -LiteralPath $taskPublicationPath) {
    $taskPriorPublication = Get-Content -LiteralPath $taskPublicationPath -Raw | ConvertFrom-Json
    foreach ($item in @($taskPriorPublication.issues)) { $taskPublished[$item.key] = $item }
}
$taskIssuesDir = Join-Path $taskRoot 'issues'
[void][IO.Directory]::CreateDirectory($taskIssuesDir)

function Get-BodyHash([string]$Value) {
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $hashAlgorithm.Dispose() }
}
function Write-TaskFile([string]$Path,[string]$Text) {
    [IO.File]::WriteAllText($Path,$Text,$taskUtf8)
}
function Save-TaskJson([string]$Path,$Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 30
    Write-TaskFile $Path ($json + [Environment]::NewLine)
}
function Get-TicketTitle($Ticket) { return ('[V1-{0:d2}] {1}' -f [int]$Ticket.ordinal,$Ticket.title) }
function Get-TicketPath($Ticket) { return Join-Path $taskIssuesDir ('{0:d2}-{1}.md' -f [int]$Ticket.ordinal,$Ticket.key) }
function Get-TicketBody($Ticket,[bool]$RequirePublished) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('## Parent')
    $lines.Add('')
    $lines.Add('[PRD: SharpInspect.NET V1 通用工业视觉检测运行框架](https://github.com/HoboCY/SharpInspect.NET/issues/1)')
    $lines.Add('')
    $lines.Add('## What to build')
    $lines.Add('')
    $lines.Add($Ticket.deliver)
    $lines.Add('')
    $adrText = (@($Ticket.adrs | Sort-Object -Unique | ForEach-Object { 'ADR-{0:d4}' -f [int]$_ }) -join '、')
    $storyText = (@($Ticket.stories | Sort-Object -Unique | ForEach-Object { 'US-{0:d2}' -f [int]$_ }) -join '、')
    $lines.Add(('范围依据：{0}；{1}；{2}。验收族：{3}。' -f ($Ticket.groups -join '、'),$adrText,$storyText,($Ticket.tests -join '、')))
    $lines.Add('')
    $lines.Add('继承父规格及相关 ADR 的完整约束。尚未交付或缺证的必需准入检查保持拒绝；开发夹具与局部测试通过不构成正式发行或真实工位合格。')
    $lines.Add('')
    $lines.Add('## Acceptance criteria')
    $lines.Add('')
    foreach ($criterion in $Ticket.criteria) { $lines.Add('- [ ] ' + $criterion) }
    $lines.Add('- [ ] 为本票涉及的规范性不变量维护稳定验证 ID、适用性及可观察证据关联，记录实际验证范围和未执行项；后续完整 Profile 汇总不能替代本票验证。')
    $lines.Add('')
    $lines.Add('## Blocked by')
    $lines.Add('')
    if (@($Ticket.deps).Count -eq 0) { $lines.Add('None — can start immediately.') }
    foreach ($dep in $Ticket.deps) {
        if ($taskPublished.ContainsKey($dep)) {
            $lines.Add(('- #{0} — {1}' -f $taskPublished[$dep].number,(Get-TicketTitle $taskByKey[$dep])))
        } elseif ($RequirePublished) {
            throw "Unpublished prerequisite: $dep"
        } else {
            $lines.Add(('- V1-{0:d2} — {1}（待创建 GitHub Issue）' -f [int]$taskByKey[$dep].ordinal,$taskByKey[$dep].title))
        }
    }
    return (($lines -join [char]10) + [char]10)
}
function Save-Publication {
    $values = @($taskTickets | Where-Object { $taskPublished.ContainsKey($_.key) } | ForEach-Object { $taskPublished[$_.key] })
    Save-TaskJson $taskPublicationPath ([ordered]@{
        repo=$taskRepo;parent=1;manifest_sha256=(Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        updated_at=[DateTimeOffset]::Now.ToString('o');count=$values.Count;issues=$values
    })
}
function Write-Index {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# SharpInspect.NET V1 工单拆分')
    $lines.Add('')
    $lines.Add('来源：[PRD #1](https://github.com/HoboCY/SharpInspect.NET/issues/1)。每票交付可独立演示或验证的行为，完成全部直接阻塞票后再启动。')
    $lines.Add('')
    $lines.Add(('共 {0} 张工单，{1} 条直接阻塞关系；已消除传递冗余，编号遵循拓扑顺序。' -f $taskTickets.Count,@($taskTickets | ForEach-Object { $_.deps }).Count))
    $lines.Add('')
    $lines.Add('范围索引覆盖 82 条故事、111 份 ADR、23 个需求分组及 25 个验收族；这是拆票覆盖检查，不等于逐不变量正式验证或资格通过。真实硬件、项目政策与正式资格在对应工单执行时提供，缺少时如实 Blocked／NotRun。')
    $lines.Add('')
    $lines.Add('正式候选先冻结产品字节，完成资格后再封装引用资格记录的发行包；手动与资格入口可在非 Ready 下验证同一实现。真实硬件断电资格只在项目声明适用时成为额外硬门。')
    $lines.Add('')
    foreach ($ticket in $taskTickets) {
        $title = Get-TicketTitle $ticket
        if ($taskPublished.ContainsKey($ticket.key)) {
            $entry = $taskPublished[$ticket.key]
            $label = '[#' + $entry.number + ' ' + $title + '](' + $entry.url + ')'
        } else {
            $localPath = (Get-TicketPath $ticket).Replace('\','/')
            $label = '[' + $title + '](' + $localPath + ')'
        }
        $deps = @($ticket.deps | ForEach-Object {
            if ($taskPublished.ContainsKey($_)) { '#' + $taskPublished[$_].number }
            else { 'V1-{0:d2}' -f [int]$taskByKey[$_].ordinal }
        })
        $blockerText = if($deps.Count) { $deps -join '、' } else { '无，可立即开始' }
        $lines.Add(('{0}. **{1}**  ' -f [int]$ticket.ordinal,$label))
        $lines.Add(('   **Blocked by：**{0}  ' -f $blockerText))
        $lines.Add(('   **交付：**{0}' -f $ticket.deliver))
        $lines.Add('')
    }
    Write-TaskFile (Join-Path $taskRoot 'breakdown.md') (($lines -join [char]10) + [char]10)
}
if ($Mode -eq 'Draft') {
    foreach ($ticket in $taskTickets) { Write-TaskFile (Get-TicketPath $ticket) (Get-TicketBody $ticket $false) }
    Write-Index
    Write-Output ('DRAFT_READY tickets={0} files={1}' -f $taskTickets.Count,@(Get-ChildItem -LiteralPath $taskIssuesDir -File).Count)
    return
}

. (Join-Path $taskRoot 'github-session.ps1')
Invoke-TicketGithubSession {
    param($taskGh)
    function Invoke-GhJson([string[]]$GhArguments) {
        $output = @(& $taskGh @GhArguments)
        if ($LASTEXITCODE -ne 0) { throw ('GitHub command failed: ' + ($GhArguments -join ' ')) }
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
    }
    function Get-RemoteIssue([int]$Number) {
        return Invoke-GhJson @('api','--method','GET',"repos/$taskRepo/issues/$Number")
    }
    function Get-RemoteBlockers([int]$Number) {
        return @(Invoke-GhJson @('api','--method','GET',"repos/$taskRepo/issues/$Number/dependencies/blocked_by",'--paginate'))
    }
    $before = Get-Content -LiteralPath (Join-Path $taskRoot 'parent-issue-before.json') -Raw | ConvertFrom-Json
    $parent = Invoke-GhJson @('issue','view','1','--repo',$taskRepo,'--json','number,title,body,state,labels,comments,url')
    if ($parent.title -cne $before.title -or $parent.body -cne $before.body -or $parent.state -cne $before.state) { throw 'Parent issue changed since approved baseline; publication stopped.' }
    if ((@($parent.labels.name | Sort-Object) -join ',') -cne (@($before.labels.name | Sort-Object) -join ',')) { throw 'Parent labels changed.' }

    if ($Mode -eq 'Publish') {
        $existing = @(Invoke-GhJson @('issue','list','--repo',$taskRepo,'--state','all','--limit','500','--json','number,title,url'))
        $createdCount=0
        foreach ($ticket in $taskTickets) {
            if ($taskPublished.ContainsKey($ticket.key)) { continue }
            if ($Limit -gt 0 -and $createdCount -ge $Limit) { break }
            $title = Get-TicketTitle $ticket
            $body = Get-TicketBody $ticket $true
            $path = Get-TicketPath $ticket
            Write-TaskFile $path $body
            $matches = @($existing | Where-Object { $_.title -ceq $title })
            if ($matches.Count -gt 1) { throw "Ambiguous duplicate title: $title" }
            if ($matches.Count -eq 1) {
                $remote = Get-RemoteIssue $matches[0].number
                if ($remote.body -cne $body) { throw "Existing issue body differs: $title" }
            } else {
                Start-Sleep -Milliseconds 1100
                $createOutput = @(& $taskGh issue create --repo $taskRepo --title $title --body-file $path --label ready-for-agent)
                if ($LASTEXITCODE -ne 0) { throw "Issue creation uncertain for $title. Inspect live exact title before retry." }
                $createdUrl = ($createOutput -join '').Trim()
                $view = Invoke-GhJson @('issue','view',$createdUrl,'--repo',$taskRepo,'--json','number,url')
                $remote = Get-RemoteIssue $view.number
            }
            $item = [pscustomobject]@{key=$ticket.key;ordinal=$ticket.ordinal;number=$remote.number;database_id=$remote.id;title=$remote.title;url=$remote.html_url;body_sha256=(Get-BodyHash $body);blocker_numbers=@();verified=$false}
            $taskPublished[$ticket.key]=$item
            Save-Publication
            if ($remote.body -cne $body -or $remote.title -cne $title -or $remote.state -ne 'open' -or 'ready-for-agent' -notin @($remote.labels.name)) { throw "Created issue verification failed: $($remote.number)" }
            $item.verified=$true
            Save-Publication
            $createdCount++
            Write-Output ('CREATED {0}/{1} #{2} {3}' -f $taskPublished.Count,$taskTickets.Count,$remote.number,$ticket.key)
        }
        Write-Index
        return
    }
    if ($taskPublished.Count -ne $taskTickets.Count) { throw 'All tickets must be published before dependency linking or final verification.' }
    if ($Mode -eq 'Link') {
        $linked=0
        foreach ($ticket in $taskTickets) {
            $item = $taskPublished[$ticket.key]
            $expected = @($ticket.deps | ForEach-Object { [int]$taskPublished[$_].number } | Sort-Object)
            $current = @(Get-RemoteBlockers $item.number)
            foreach ($extra in @($current | ForEach-Object { $_.number } | Where-Object { $null -ne $_ -and $_ -notin $expected })) { throw "Unexpected blocker $extra on #$($item.number); no automatic removal." }
            foreach ($dep in $ticket.deps) {
                $blockingIssue=$taskPublished[$dep]
                if ([int]$blockingIssue.number -in @($current.number)) { continue }
                if ($Limit -gt 0 -and $linked -ge $Limit) { Save-Publication; return }
                Start-Sleep -Milliseconds 1100
                $edge = Invoke-GhJson @('api','--method','POST',"repos/$taskRepo/issues/$($item.number)/dependencies/blocked_by",'-F',('issue_id=' + $blockingIssue.database_id))
                $linked++
                Write-Output ('LINKED #{0} blocked by #{1}' -f $item.number,$blockingIssue.number)
            }
            $observed = @(Get-RemoteBlockers $item.number)
            if ((@($observed.number | Sort-Object) -join ',') -cne ($expected -join ',')) { throw "Native blockers mismatch on #$($item.number)" }
            $item.blocker_numbers = $expected
            Save-Publication
        }
        Write-Output ('LINKS_COMPLETE edges={0}' -f @($taskTickets | ForEach-Object { $_.deps }).Count)
        return
    }
    if ($Mode -eq 'Verify') {
        $checks=@()
        foreach ($ticket in $taskTickets) {
            $item=$taskPublished[$ticket.key]
            $remote=Get-RemoteIssue $item.number
            $blockers=@(Get-RemoteBlockers $item.number)
            $body=Get-TicketBody $ticket $true
            $expected=@($ticket.deps | ForEach-Object { [int]$taskPublished[$_].number } | Sort-Object)
            $ok = $remote.title -ceq (Get-TicketTitle $ticket) -and $remote.body -ceq $body -and $remote.state -eq 'open' -and 'ready-for-agent' -in @($remote.labels.name) -and ((@($blockers.number | Sort-Object) -join ',') -ceq ($expected -join ','))
            $checks += [pscustomobject]@{key=$ticket.key;number=$item.number;pass=$ok;body_sha256=(Get-BodyHash $remote.body);blockers=@($blockers | ForEach-Object { $_.number })}
            if(-not $ok){throw "Final verification failed: #$($item.number)"}
            Write-Output ('VERIFIED #{0} {1}' -f $item.number,$ticket.key)
        }
        $parentAfter=Invoke-GhJson @('issue','view','1','--repo',$taskRepo,'--json','number,title,body,state,labels,comments,url')
        $commentsBefore=ConvertTo-Json -InputObject @($before.comments) -Depth 20 -Compress
        $commentsAfter=ConvertTo-Json -InputObject @($parentAfter.comments) -Depth 20 -Compress
        $parentUnchanged=$parentAfter.title -ceq $before.title -and $parentAfter.body -ceq $before.body -and $parentAfter.state -ceq $before.state -and ((@($parentAfter.labels.name | Sort-Object) -join ',') -ceq (@($before.labels.name | Sort-Object) -join ',')) -and $commentsBefore -ceq $commentsAfter
        if(-not $parentUnchanged){throw 'Parent protected fields changed.'}
        Save-TaskJson (Join-Path $taskRoot 'parent-issue-after.json') $parentAfter
        $source=Get-Content -LiteralPath (Join-Path $taskRoot 'source-baseline.json') -Raw | ConvertFrom-Json
        $sourceChecks=@($source.files | ForEach-Object {
            $actual=(Get-FileHash -LiteralPath (Join-Path (Split-Path (Split-Path $taskRoot -Parent) -Parent) $_.path) -Algorithm SHA256).Hash.ToLowerInvariant()
            [pscustomobject]@{path=$_.path;unchanged=($actual -ceq $_.sha256);sha256=$actual}
        })
        if(@($sourceChecks | Where-Object { -not $_.unchanged }).Count){throw 'Domain source changed during ticket publication.'}
        Save-TaskJson (Join-Path $taskRoot 'verification.json') ([ordered]@{
            verified_at=[DateTimeOffset]::Now.ToString('o');ticket_count=$checks.Count;native_blocking_edges=@($taskTickets | ForEach-Object { $_.deps }).Count;
            parent_unchanged=$parentUnchanged;parent_body_sha256=(Get-BodyHash $parentAfter.body);source_files_unchanged=$sourceChecks.Count;checks=$checks;source_checks=$sourceChecks
        })
        Write-Index
        Write-Output ('VERIFICATION_COMPLETE tickets={0} parent_unchanged=True source_files={1}' -f $checks.Count,$sourceChecks.Count)
    }
}
