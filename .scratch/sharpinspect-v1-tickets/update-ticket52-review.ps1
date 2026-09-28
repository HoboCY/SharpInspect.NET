param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$taskValidation = Get-Content -LiteralPath (Join-Path $Run 'validation.json') -Raw | ConvertFrom-Json
$taskReports = @('ticket52-storage-review-01.md','ticket52-runtime-migration-capacity-review-01.md',
    'ticket52-capacity-architecture-final.md','ticket52-existing-readers-review-01.md',
    'ticket52-required-continuation-architecture.md','ticket52-audit-publication-architecture.md',
    'ticket52-audit-publication-review-01.md','ticket52-bootstrap-retirement-architecture.md',
    'ticket52-bootstrap-retirement-review-01.md','ticket52-probe-continuation-architecture.md') | ForEach-Object {
    $taskPath = Join-Path $PSScriptRoot $_
    [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash}
}
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Review source drift.' }
}
[ordered]@{
    kind='V1-52-review-disposition';result='Pass';sourceHashes=$taskValidation.sourceHashes;independentReports=@($taskReports)
    scope='Five read-only DeepSeek reviews and five native architecture results. Full inventory binds accepted implementation, not a claim every file was independently reviewed.'
    fixed=@('P1 batch rows/Base64 capacity','P2 claim identity versus authority','P3 pending excludes succeeded',
        'Capacity F1 terminal unused reservation','F2 F3 independent receipt bounds','F4 narrow retry window rescan',
        'Schema36 public image query and two internal guards with complete Outbox verification options',
        'Arm and Outbox success completion follows commit-watermark and audit-recheck publication',
        'Publication review F2: negative observation uses a full uncancellable one-second window',
        'Graceful bootstrap exit drains issued IO under existing deadlines and revokes subsequent requests',
        'Preserve pre-send PlcRequestRevokedException instead of converting it to synchronization timeout',
        'Migration probes wait for the real post-command audit tail under the original deadline; seed and current writer watermarks are checked')
    rejectedInterpretations=@('Unlimited BestEffort history may bypass capacity','Cold verification ignored actual receipts',
        'Generic TraceStoreUnavailable may autoheal','Unused internal guards caused the worker fault',
        'Missing Outbox options silently skip verification')
    validation='Image query regression red then green; M03 guards and R02 diagnostic 6/6; bounded R02 repetitions 6/6; R04 PNG/Outbox pass. Audit publication controlled red 0/4 then green 8/8; complete Outbox/Arm focus 78/78; strengthened uncancellable negative-window checks 4/4. Bootstrap controlled red 0/2 then green 4/4 including original normal-exit and Abort cases. Full gate independently required before publication.'
    limitations=@('image-integration-focus-01 78/79; original R02 wait failure cause unproven, diagnostics improved without product gate change',
        'Four interrupted full gates retain logs and NotRun; exact scheduling of the original R13 and R06 failures not reconstructed',
        'Full171531-904 Fail at source35 phase5 pre-migration seed observation; repaired probe is validated by an explicitly linked continuation, with unchanged product results reused',
        'Publication review F1 historical writer consumers need separate evidence; no broad expansion',
        'F3 exception masking remains a diagnostic limitation and cannot make a failed check pass; F4 no reachable non-OOM publication exception found',
        'Bootstrap review P3: production revoke reason becomes Cancelled/AuthorityRevoked instead of timeout; no dependent string consumer found',
        'Bootstrap review P3: no separate graceful-exit plus never-responding-peer combination; existing deadlines and timeout regressions retained',
        'Bootstrap review P3: server-hook assertion and 150ms silence window have diagnostic/scheduling limits, not false-pass evidence',
        'No production endpoints or hardware qualification')
    unresolvedBlockers=@();recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket52-review-disposition.json') -Encoding utf8
[ordered]@{
    kind='V1-52-review-scope';startingHead=$taskValidation.startingHead;sourceHashes=$taskValidation.sourceHashes
    reviewedAreas=@('Outbox contracts/protocol/authority','SQLite Outbox and central audit','Runtime production integration',
        '32/33/34/35 migration optional binding','admission and terminal capacity transfer',
        'existing schema36 readers and verification entry points','Required Permanent versus accepted continuation',
        'Arm and Outbox commit completion versus audit-state publication',
        'Bootstrap graceful retirement versus issued IO cancellation and typed pre-send authority revocation',
        'Post-command verified audit-watermark observation and bounded probe-only continuation provenance')
    limitations=@('Not every mechanical call-site independently reviewed','No independent reviewer builds or tests',
        'No production endpoints or qualification');reports=@($taskReports)
} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket52-review-scope.json') -Encoding utf8
'Review evidence binds current full-gate source inventory.'
