$ErrorActionPreference='Stop'
$taskRoot='D:\projects\SharpInspect.NET'
function Edit-Task([string]$path,[string]$old,[string]$replacement,[int]$expected=1) {
    $full=Join-Path $taskRoot $path
    $content=[IO.File]::ReadAllText($full).Replace("`r`n","`n")
    $count=[regex]::Matches($content,[regex]::Escape($old)).Count
    if($count -ne $expected){throw "Edit precondition failed: $path expected $expected got $count"}
    [IO.File]::WriteAllText($full,$content.Replace($old,$replacement),[Text.UTF8Encoding]::new($false))
}
$taskChanged=@()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src/SharpInspect.Runtime') -Recurse -File -Filter '*.cs') {
    $content=[IO.File]::ReadAllText($file.FullName)
    $old='or EvidenceReconciliationStoreOptions.SchemaVersion'
    if(-not $content.Contains($old)){continue}
    $count=[regex]::Matches($content,[regex]::Escape($old)).Count
    $replacement='or EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion'
    [IO.File]::WriteAllText($file.FullName,$content.Replace($old,$replacement),[Text.UTF8Encoding]::new($false))
    $taskChanged += [ordered]@{path=$file.FullName.Substring($taskRoot.Length+1);extendedClosedVersionSets=$count}
}
$taskChanged | ConvertTo-Json | Set-Content -LiteralPath '.scratch/t55/schema39-version-sets.json' -Encoding utf8
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.cs' '    private int SchemaVersion => EvidenceReconciliationEnabled ? EvidenceReconciliationStoreOptions.SchemaVersion :' @'
    internal bool StorageRetentionEnabled => _options.StorageRetention is not null;
    private int SchemaVersion => StorageRetentionEnabled ? TraceStorageRetentionOptions.SchemaVersion :
        EvidenceReconciliationEnabled ? EvidenceReconciliationStoreOptions.SchemaVersion :
'@
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.cs' '        options.EvidenceReconciliation?.ValidateProfile(options);' @'
        options.EvidenceReconciliation?.ValidateProfile(options);
        options.StorageRetention?.ValidateProfile(options);
'@
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.cs' '    private string MigrationReason(int existingVersion) => EvidenceReconciliationEnabled' @'
    private string MigrationReason(int existingVersion) => StorageRetentionEnabled
        ? "RetentionGovernedMigrationRequired" : EvidenceReconciliationEnabled
'@
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.cs' @'
                AuditChainDatabase.AppendEvidenceReconciliationActivation(database, _options, _policy!, _signingKey!, deadline);
            }
'@ @'
                AuditChainDatabase.AppendEvidenceReconciliationActivation(database, _options, _policy!, _signingKey!, deadline);
            }
            if (StorageRetentionEnabled)
            {
                InitializeRetentionTables(database, _options, deadline);
                AuditChainDatabase.AppendRetentionActivation(database, _options, _policy!, _signingKey!, deadline);
            }
'@
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.EvidenceReconciliationSchema.cs' 'version != EvidenceReconciliationStoreOptions.SchemaVersion' 'version is not (EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion)'
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.EvidenceReconciliationSchema.cs' 'version == EvidenceReconciliationStoreOptions.SchemaVersion' 'version is EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion'
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.EvidenceReconciliation.cs' 'version != EvidenceReconciliationStoreOptions.SchemaVersion' 'version is not (EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion)'
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' @'
        if (version == EvidenceReconciliationStoreOptions.SchemaVersion)
            return EvidenceReconciliationAuditSchema();
'@ @'
        if (version == EvidenceReconciliationStoreOptions.SchemaVersion)
            return EvidenceReconciliationAuditSchema();
        if (version == TraceStorageRetentionOptions.SchemaVersion)
            return RetentionAuditSchema();
'@
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' 'long? evidenceReconciliationReserveOverride = null)' "long? evidenceReconciliationReserveOverride = null,`n        long? retentionReserveOverride = null)" 2
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' 'evidenceReconciliationReserveOverride: evidenceReconciliationReserveOverride);' @'
evidenceReconciliationReserveOverride: evidenceReconciliationReserveOverride,
            retentionReserveOverride: retentionReserveOverride);
'@
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' '            var limit = checked(policy.MaximumVerificationEntries - controlReserve -' @'
            var retentionReserve = TableExists(db, "evidence_retention_events", deadline)
                ? retentionReserveOverride ?? SqliteCommandStore.ReadRetentionAuditReserve(db, deadline) : 0;
            var limit = checked(policy.MaximumVerificationEntries - controlReserve - retentionReserve -
'@
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' '        var reconciliationVerification = BeginEvidenceReconciliationVerification(db, storedSchemaVersion, deadline);' @'
        var reconciliationVerification = BeginEvidenceReconciliationVerification(db, storedSchemaVersion, deadline);
        var retentionVerification = BeginRetentionVerification(db, storedSchemaVersion, deadline);
'@
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' '(reconciliationVerification?.MetadataCount ?? 0)) == tail.Sequence - 1' '(reconciliationVerification?.MetadataCount ?? 0) + (retentionVerification?.MetadataCount ?? 0)) == tail.Sequence - 1'
Edit-Task 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs' '               var encodedPayloadLimit = reconciliationKind ?' @'
               var retentionKind = row.Kind is SqliteCommandStore.RetentionActivationKind or SqliteCommandStore.RetentionEventAuditKind;
               var encodedPayloadLimit = retentionKind ? (TraceStorageRetentionOptions.MaximumPayloadBytesHardLimit * 2 + 1024) * 2 :
                   reconciliationKind ?
'@
$taskAuditPath=Join-Path $taskRoot 'src/SharpInspect.Runtime/Integrity/AuditChainDatabase.cs'
$taskAudit=[IO.File]::ReadAllText($taskAuditPath)
$taskStart=$taskAudit.IndexOf('            else if (reconciliationKind && reconciliationVerification is not null)')
$taskEnd=$taskAudit.IndexOf('            else if (productionOutboxKind && productionOutboxVerification is not null)',$taskStart)
if($taskStart -lt 0 -or $taskEnd -le $taskStart){throw 'Metadata branch not found'}
$taskBranch=$taskAudit.Substring($taskStart,$taskEnd-$taskStart)
$taskNewBranch=$taskBranch.Replace('reconciliationKind','retentionKind').Replace('reconciliationVerification','retentionVerification').Replace('EvidenceReconciliationAuditPositionInvalid','RetentionAuditPositionInvalid').Replace('VerifyEvidenceReconciliationMetadata','VerifyRetentionMetadata')
[IO.File]::WriteAllText($taskAuditPath,$taskAudit.Insert($taskStart,$taskNewBranch),[Text.UTF8Encoding]::new($false))
Edit-Task 'src/SharpInspect.Runtime/Storage/EvidenceRetentionCodec.cs' 'value.SourceAuditSequence > 0 && value.ByteLength > 0 &&' 'value.SourceAuditSequence > 0 && value.ByteLength >= 0 &&'
Edit-Task 'src/SharpInspect.Runtime/Storage/EvidenceRetentionCodec.cs' 'value.InspectionId is { } id && id != Guid.Empty && value.EvidenceClass == TraceRetentionClass.AuthoritativeImage' 'value.InspectionId is { } id && id != Guid.Empty && value.ByteLength > 0 && value.EvidenceClass == TraceRetentionClass.AuthoritativeImage'
Edit-Task 'src/SharpInspect.Runtime/Storage/EvidenceRetentionCodec.cs' 'value.ByteLength > 0, "FileIdentityInvalid");' 'value.ByteLength >= 0, "FileIdentityInvalid");'
Edit-Task 'src/SharpInspect.Runtime/Storage/SqliteCommandStore.RetentionSources.cs' '        EvidenceRetentionCodec.Require(released == 1, "ImageWorkUnfinished");' @'
        EvidenceRetentionCodec.Require(released == 1, "ImageWorkUnfinished");
        var lastCycleKind = AuditChainDatabase.Scalar(database, @"SELECT COALESCE((SELECT Kind
            FROM production_inspection_events WHERE InspectionId=? AND AuditSequence<?
            ORDER BY AuditSequence DESC LIMIT 1),0);", deadline,
            obligation.InspectionId!.Value.ToString("D"), RetentionNumber(before));
        EvidenceRetentionCodec.Require(lastCycleKind is (long)ProductionInspectionEventKind.AcknowledgementReset or
            (long)ProductionInspectionEventKind.RecoveryCompleted, "InspectionLifecycleUnfinished");
'@
[pscustomobject]@{versionSetFiles=$taskChanged.Count;versionSets=($taskChanged.extendedClosedVersionSets | Measure-Object -Sum).Sum} | ConvertTo-Json
