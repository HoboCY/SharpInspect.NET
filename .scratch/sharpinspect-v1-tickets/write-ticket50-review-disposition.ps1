$ErrorActionPreference = 'Stop'
$taskRun = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-full-run.txt') -Raw).Trim()
$taskValidation = Get-Content -LiteralPath (Join-Path $taskRun 'validation.json') -Raw | ConvertFrom-Json
if ($taskValidation.kind -cne 'V1-50-development-validation') { throw 'Wrong source freeze.' }
foreach ($taskSource in $taskValidation.sourceHashes) {
    if ((Get-FileHash -LiteralPath $taskSource.path).Hash -cne $taskSource.sha256) { throw 'Source drift.' }
}
$taskReports = @('ticket50-schema-review.md','ticket50-runtime-review.md','ticket50-delta-review.md') |
    ForEach-Object { $taskPath = Join-Path $PSScriptRoot $_
        [ordered]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash} }
$taskOriginalPin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-runtime-review-pin.json') -Raw | ConvertFrom-Json
$taskFollowupPin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-followup-review-pin.json') -Raw | ConvertFrom-Json
$taskOriginalDeltas = @($taskOriginalPin | Where-Object { (Get-FileHash -LiteralPath $_.Path).Hash -cne $_.Hash } |
    ForEach-Object { [ordered]@{path=$_.Path;reviewedSha256=$_.Hash;currentSha256=(Get-FileHash -LiteralPath $_.Path).Hash} })
$taskFollowupDeltas = @($taskFollowupPin | Where-Object { (Get-FileHash -LiteralPath $_.path).Hash -cne $_.sha256 } |
    ForEach-Object { [ordered]@{path=$_.path;reviewedSha256=$_.sha256;currentSha256=(Get-FileHash -LiteralPath $_.path).Hash} })
$taskScope = [ordered]@{
    kind='V1-50-review-scope';startingHead=$taskValidation.startingHead;
    originalRuntimeReviewPin=$taskOriginalPin;followupReviewPin=$taskFollowupPin;
    originalPinDeltas=$taskOriginalDeltas;followupPinDeltas=$taskFollowupDeltas;
    finalSourceHashes=$taskValidation.sourceHashes;
    note='Independent reviews pin their recorded snapshots. Primary dispositions explicitly account for post-pin test repairs and the two consumer evidence-hardening files; no claim that every final byte was independently reviewed.'
}
$taskScope | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-review-scope.json') -Encoding utf8
$taskFindings = @(
    [ordered]@{report='schema';finding='P1 identity schema upper bound';status='Resolved';evidence='IdentityAuditEvent Encode/VerifyPayload uses EnvelopeGeneration(34)=33; schema/migration regression passed; original 32/33 encodings remain unchanged.'},
    [ordered]@{report='schema';finding='P2 command trace schema upper bound';status='Resolved';evidence='Trace and alarm numeric allowlists include 34; all audit Verify calls pass exact image options; final runtime review confirmed acceptance paths.'},
    [ordered]@{report='schema';finding='P2 torn marker replacement';status='Resolved';evidence='Durable unique temporary marker followed by atomic File.Replace; Schema08 five injected boundaries passed in migration-regression-05.'},
    [ordered]@{report='schema';finding='P3 image configuration omitted from journal';status='Resolved';evidence='Target 34 freezes ImageEvidenceConfigurationHash; nullable omission preserves old journal bytes; Schema09 passed in migration-regression-05.'},
    [ordered]@{report='runtime';finding='P2 orphan stage cold startup coverage';status='Resolved';evidence='V150_R10 two cases passed in cold-start-14: orphan stage/partial with empty ledger blocks Ready/Arm and preserves bytes.'},
    [ordered]@{report='runtime';finding='P2 unresolved capture policy coverage';status='Resolved';evidence='V150_P08-P15 thirteen cases passed in policy-rejection-16, including real activation rejection, exact catalog mismatch and activation A16/A18 failures.'},
    [ordered]@{report='runtime';finding='P3 endpoint change bypasses reservation recovery';status='NotReproduced';evidence='ReadCurrentAsync is unfiltered and RecoveryRequired(scoped) includes every endpoint; Transport endpoint filter only collects cycle keys after the global recovery gate. Independent final delta reviewer confirmed no counterexample.'},
    [ordered]@{report='runtime';finding='P3 duplicate schema patterns';status='Resolved';evidence='Removed redundant or SchemaVersion in ProductionArm and RecipeSelection; independently checked in eight-file follow-up pin.'},
    [ordered]@{report='runtime';finding='Post-pin stager scheduling failure and image reason propagation';status='Resolved';evidence='Follow-up reviewer confirmed Dispose before active/slot release and completion; ProductionImage prefix preserves cold-read failure reasons. F stager tests and R08 cold tamper tests passed in boundary-11.'},
    [ordered]@{report='delta';finding='P3 Ready evidence interpretation';status='Resolved';evidence='Removed constant maintenance Ready assertions and renamed its field maintenanceReady. Writer Ready remains actual StationStateSnapshot.Ready and is still asserted. Reviewer statement that writer Ready was constant was incorrect; actual runtime writer code is preserved.'},
    [ordered]@{report='delta';finding='P3 subset phase scope and completed-phase interpretation';status='Resolved';evidence='Explicit requestedPhases/publicPhaseCount/fullPhaseCoverage; empty or duplicate phases rejected. Completed phase labeled CompletedOperationReopenIdempotency; other phases InterruptedOperationResume.'},
    [ordered]@{report='delta';finding='P3 backup source and bytes';status='Resolved';evidence='Consumer requires source schema 33, true preserved old Runtime SHA, positive exact byte length, file presence and matching backup SHA.'},
    [ordered]@{report='delta';finding='P3 WAL omitted from no-change comparison';status='Resolved';evidence='Compare main database SHA plus WAL length/SHA before/after actual old writer; absent/empty WAL normalize to empty bytes, SHM excluded as transient locks. Preflight-17 phases 1/14 passed with oldDatabaseAndWalUnchanged=true.'}
)
$taskReview = [ordered]@{
    kind='V1-50-independent-review-disposition';result='Pass';
    scope='Primary disposition of schema, complete runtime and final eight-file independent reviews. Full development validation is recorded separately and is not implied by this review result.';
    independentReviewer='DeepSeek-V4.1-Flash / deepseek-flash / max';
    jobs=@('7969dbf2b0f243cbac2818fc8a69fadd','0117f26b076a4557815f285b976d962f','a0d6a2914271474592b886954072fc37');
    independentReports=$taskReports;findings=$taskFindings;sourceHashes=$taskValidation.sourceHashes;
    scopeSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ticket50-review-scope.json')).Hash;
    limits=@('Reviewers did not run tests. Primary ran all cited checks.','Final two consumer files were strengthened after the review per its recommendations and checked by primary plus preflight-17.','No physical power-loss or real station qualification.');
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
}
$taskReview | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ticket50-review-disposition.json') -Encoding utf8
[ordered]@{kind=$taskReview.kind;result=$taskReview.result;reports=$taskReports.Count;dispositions=$taskFindings.Count;finalSourceFiles=$taskValidation.sourceHashes.Count;postFollowupFiles=$taskFollowupDeltas.path} | ConvertTo-Json
