using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private void ValidateRecipeDraftLifecycleForSave(sqlite3 database, RecipeDraftWork work,
        RecipeDraftHead? head, StoreDeadline deadline)
    {
        var lifecycle = ReadRecipeLifecycleRecords(database, _options.RecipeLifecycle, deadline);
        if (RecipeLifecycleProjection.Abandonment(lifecycle, work.Request.DraftId) is not null)
            throw new InvalidOperationException("RecipeDraftAbandoned");
        var candidate = work.Document.Content;
        if (head is not null)
        {
            var old = DecodeAndValidateRecipeDraftRow(head, _options.RecipeDrafts!, _options.CameraSetup is not null);
            if (old.LifecycleLineage?.ContentHash != candidate.LifecycleLineage?.ContentHash)
                throw new InvalidOperationException("RecipeDraftLifecycleLineageImmutable");
            return;
        }
        if (candidate.LifecycleLineage is null) return;
        if (_options.RecipeLifecycle is null) throw new InvalidOperationException("RecipeLifecycleConfigurationRequired");
        if (work.Request.ExpectedRevision != 0 || work.Request.ExpectedRevisionContentHash is not null)
            throw new InvalidOperationException("RecipeDraftDerivationRequiresNewIdentity");
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0) FROM recipe_draft_revisions;", deadline) + 1);
        ValidateRecipeDraftLifecycleSource(database, work.Request.DraftId, position, candidate,
            _options.RecipeDrafts!, _options.CameraSetup is not null, deadline, requireAuditOrder: false);
    }

    private static void ValidateRecipeDraftLifecycleSource(sqlite3 database, Guid draftId, long position,
        RecipeDraftContent content, RecipeDraftStoreOptions options, bool cameraSetupEnabled,
        StoreDeadline deadline, bool requireAuditOrder)
    {
        var lineage = content.LifecycleLineage ?? throw new InvalidOperationException("RecipeDraftLifecycleLineageRequired");
        if (draftId == lineage.SourceDraft.DraftId) throw new InvalidOperationException("RecipeDraftDerivationRequiresNewIdentity");
        if (AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is not
            (RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion))
            throw new InvalidOperationException("RecipeDraftLifecycleConfigurationRequired");
        var transitions = AuditChainDatabase.Read(database, @"SELECT Payload,TransitionId,RecordContentHash,AuditSequence
            FROM recipe_lifecycle_events WHERE Position=? LIMIT 2;", deadline, row =>
            (Record: RecipeLifecycleStorageCodec.Decode(DecodeRecipeLifecyclePayload(SqliteNative.ColumnText(row, 0)!)),
                Id: SqliteNative.ColumnText(row, 1), Hash: SqliteNative.ColumnText(row, 2),
                Audit: SqliteNative.ColumnInt64(row, 3)), LifecycleNumber(lineage.Transition.Position));
        if (transitions.Count != 1 || transitions[0].Record.Reference != lineage.Transition ||
            transitions[0].Id != lineage.Transition.TransitionId.ToString("D") || transitions[0].Hash != lineage.Transition.ContentHash ||
            new RecipeDraftLifecycleLineage(transitions[0].Record).ContentHash != lineage.ContentHash)
            throw new InvalidOperationException("RecipeDraftLifecycleSourceMismatch");
        var source = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=? LIMIT 2", deadline,
            lineage.SourceDraft.DraftId.ToString("D"), LifecycleNumber(lineage.SourceDraft.Revision)).SingleOrDefault();
        if (source is null || source.Position >= position || source.RevisionContentHash != lineage.SourceDraft.RevisionContentHash)
            throw new InvalidOperationException("RecipeDraftLifecycleSourceRevisionMismatch");
        var sourceContent = DecodeAndValidateRecipeDraftRow(source, options, cameraSetupEnabled);
        if (sourceContent.ContentHash != lineage.SourceContentHash)
            throw new InvalidOperationException("RecipeDraftLifecycleSourceContentMismatch");
        if (content.MigrationLineage is null)
        {
            // Derivation copies the complete preserved content into a fresh identity.
            // Earlier migration provenance remains reachable through this source edge.
            var expected = new RecipeDraftContent(lineage, null, sourceContent.RecipeKey, sourceContent.DisplayName,
                sourceContent.Algorithm, sourceContent.Configuration, sourceContent.CameraRole, sourceContent.Camera,
                sourceContent.AlgorithmExecutionTimeout, sourceContent.AssetRequirements, sourceContent.PolicyRequirements,
                sourceContent.ValueOrigins, sourceContent.CameraProviderExtension, sourceContent.CalibrationRequirements,
                sourceContent.PartIdentityRequirement);
            if (expected.ContentHash != content.ContentHash) throw new InvalidOperationException("RecipeDraftDerivationContentMismatch");
        }
        else
        {
            var migration = content.MigrationLineage.Plan;
            var migrationSource = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=? LIMIT 2", deadline,
                migration.Source.DraftId.ToString("D"), LifecycleNumber(migration.Source.Revision)).SingleOrDefault();
            if (migrationSource is null || migrationSource.Position >= position ||
                migrationSource.RevisionContentHash != migration.Source.RevisionContentHash)
                throw new InvalidOperationException("RecipeDraftLifecycleMigrationSourceMismatch");
            var inherited = DecodeAndValidateRecipeDraftRow(migrationSource, options, cameraSetupEnabled).LifecycleLineage;
            if (migration.Source != lineage.SourceDraft && inherited?.ContentHash != lineage.ContentHash)
                throw new InvalidOperationException("RecipeDraftLifecycleMigrationSourceMismatch");
        }
        if (requireAuditOrder)
        {
            var draftAudit = AuditChainDatabase.Read(database, @"SELECT Sequence FROM audit_entries
                WHERE Kind='RecipeDraftRevision' AND DraftPosition=? LIMIT 2;", deadline,
                row => SqliteNative.ColumnInt64(row, 0), LifecycleNumber(position));
            if (draftAudit.Count != 1 || transitions[0].Audit >= draftAudit[0])
                throw new InvalidOperationException("RecipeDraftLifecycleSourceOrderInvalid");
        }
    }
}
