using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Transaction and replay guards for immutable configuration migration lineage.
/// The migration itself is performed by the authoring service; this slice only
/// verifies the persisted source, target and lineage bindings at the store edge.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    private sealed record RecipeDraftMigrationHistoryEntry(
        long TargetPosition,
        Guid TargetDraftId,
        long TargetRevision,
        Guid TargetOperationId,
        RecipeDraftMigrationLineage Lineage);

    private void ValidateRecipeDraftLineageForSave(sqlite3 database, RecipeDraftWork work,
        RecipeDraftHead? head, StoreDeadline deadline)
    {
        var options = _options.RecipeDrafts ??
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        var candidate = work.Document.Content;

        if (work.MigrationPlan is { } plan)
        {
            AuditChainDatabase.Require(head is null, "RecipeDraftMigrationTargetExists");
            AuditChainDatabase.Require(work.Request.ExpectedRevision == 0 &&
                work.Request.ExpectedRevisionContentHash is null,
                "RecipeDraftMigrationInitialRevisionRequired");
            AuditChainDatabase.Require(work.Request.OperationId == plan.OperationId &&
                work.Request.DraftId == plan.TargetDraftId &&
                work.Request.ChangeReason == plan.ChangeReason,
                "RecipeDraftMigrationRequestMismatch");

            var lineage = candidate.MigrationLineage ??
                throw new InvalidOperationException("RecipeDraftMigrationLineageRequired");
            AuditChainDatabase.Require(MigrationPlanMatches(lineage, plan, work.Request),
                "RecipeDraftMigrationPlanMismatch");
            ValidateMigrationLineageTarget(candidate, lineage);

            var targetPosition = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM recipe_draft_revisions;", deadline) + 1);
            ValidateRecipeDraftMigrationSource(database,
                new RecipeDraftMigrationHistoryEntry(targetPosition, plan.TargetDraftId, 1,
                    plan.OperationId, lineage), options,
                _options.CameraSetup is not null, deadline);
            return;
        }

        if (head is null)
        {
            AuditChainDatabase.Require(candidate.MigrationLineage is null,
                "RecipeDraftMigrationLineageRequiresMigration");
            return;
        }

        var headContent = DecodeAndValidateRecipeDraftRow(head, options, _options.CameraSetup is not null);
        var previous = headContent.MigrationLineage;
        AuditChainDatabase.Require((previous is null) == (candidate.MigrationLineage is null),
            "RecipeDraftMigrationLineageImmutable");
        if (previous is not null && candidate.MigrationLineage is { } current)
            AuditChainDatabase.Require(string.Equals(previous.ContentHash, current.ContentHash,
                StringComparison.Ordinal), "RecipeDraftMigrationLineageImmutable");
    }

    private static bool MigrationPlanMatches(RecipeDraftMigrationLineage lineage,
        RecipeDraftMigrationPlan plan, RecipeDraftSaveRequest request) =>
        MigrationPlanMatches(lineage, plan) &&
        plan.OperationId == request.OperationId &&
        plan.TargetDraftId == request.DraftId &&
        request.ExpectedRevision == 0 && request.ExpectedRevisionContentHash is null &&
        string.Equals(request.ChangeReason, plan.ChangeReason, StringComparison.Ordinal);

    private static bool MigrationPlanMatches(RecipeDraftMigrationLineage lineage,
        RecipeDraftMigrationPlan plan)
    {
        var actual = lineage.Plan;
        return actual.OperationId == plan.OperationId &&
            RevisionReferenceMatches(actual.Source, plan.Source) &&
            actual.TargetDraftId == plan.TargetDraftId &&
            AlgorithmIdentityMatches(actual.TargetAlgorithm, plan.TargetAlgorithm) &&
            ContractReferenceMatches(actual.TargetSchema, plan.TargetSchema) &&
            ContractReferenceMatches(actual.Migrator, plan.Migrator) &&
            string.Equals(actual.ChangeReason, plan.ChangeReason, StringComparison.Ordinal) &&
            string.Equals(actual.ContentHash, plan.ContentHash, StringComparison.Ordinal);
    }

    private static bool RevisionReferenceMatches(RecipeDraftRevisionReference left,
        RecipeDraftRevisionReference right) =>
        left.DraftId == right.DraftId && left.Revision == right.Revision &&
        string.Equals(left.RevisionContentHash, right.RevisionContentHash, StringComparison.Ordinal);

    private static bool AlgorithmIdentityMatches(AlgorithmIdentity left, AlgorithmIdentity right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal);

    private static bool ContractReferenceMatches(RecipeContractReference left,
        RecipeContractReference right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SchemaReferenceMatches(AlgorithmConfigurationSchema schema,
        RecipeContractReference reference) =>
        string.Equals(schema.Id, reference.Id, StringComparison.Ordinal) &&
        string.Equals(schema.Version, reference.Version, StringComparison.Ordinal) &&
        string.Equals(schema.ContentHash, reference.ContentHash, StringComparison.Ordinal);

    private static void ValidateMigrationLineageTarget(RecipeDraftContent content,
        RecipeDraftMigrationLineage lineage)
    {
        var plan = lineage.Plan;
        var descriptor = lineage.Descriptor;
        AuditChainDatabase.Require(ContractReferenceMatches(plan.Migrator, descriptor.Migrator),
            "RecipeDraftMigrationDescriptorMismatch");
        AuditChainDatabase.Require(ContractReferenceMatches(plan.TargetSchema, descriptor.TargetSchema) &&
            AlgorithmIdentityMatches(plan.TargetAlgorithm, descriptor.TargetAlgorithm),
            "RecipeDraftMigrationDescriptorMismatch");
        AuditChainDatabase.Require(AlgorithmIdentityMatches(content.Algorithm.Algorithm, plan.TargetAlgorithm) &&
            AlgorithmIdentityMatches(content.Algorithm.Algorithm, descriptor.TargetAlgorithm),
            "RecipeDraftMigrationTargetAlgorithmMismatch");
        AuditChainDatabase.Require(SchemaReferenceMatches(content.Algorithm.ConfigurationSchema, plan.TargetSchema) &&
            SchemaReferenceMatches(content.Algorithm.ConfigurationSchema, descriptor.TargetSchema),
            "RecipeDraftMigrationTargetSchemaMismatch");
        AuditChainDatabase.Require(string.Equals(content.Configuration.SchemaId, plan.TargetSchema.Id,
                StringComparison.Ordinal) &&
            string.Equals(content.Configuration.SchemaVersion, plan.TargetSchema.Version,
                StringComparison.Ordinal) &&
            string.Equals(content.Configuration.SchemaContentHash, plan.TargetSchema.ContentHash,
                StringComparison.Ordinal),
            "RecipeDraftMigrationTargetConfigurationSchemaMismatch");
        AuditChainDatabase.Require(string.Equals(content.Configuration.ContentHash,
            lineage.InitialOutputConfigurationContentHash, StringComparison.Ordinal),
            "RecipeDraftMigrationOutputMismatch");
    }

    private static void ValidateMigrationLineageSource(RecipeDraftContent content,
        RecipeDraftMigrationLineage lineage)
    {
        var descriptor = lineage.Descriptor;
        AuditChainDatabase.Require(AlgorithmIdentityMatches(content.Algorithm.Algorithm,
                descriptor.SourceAlgorithm), "RecipeDraftMigrationSourceAlgorithmMismatch");
        AuditChainDatabase.Require(SchemaReferenceMatches(content.Algorithm.ConfigurationSchema,
                descriptor.SourceSchema), "RecipeDraftMigrationSourceSchemaMismatch");
        AuditChainDatabase.Require(string.Equals(content.Configuration.SchemaId, descriptor.SourceSchema.Id,
                StringComparison.Ordinal) &&
            string.Equals(content.Configuration.SchemaVersion, descriptor.SourceSchema.Version,
                StringComparison.Ordinal) &&
            string.Equals(content.Configuration.SchemaContentHash, descriptor.SourceSchema.ContentHash,
                StringComparison.Ordinal), "RecipeDraftMigrationSourceConfigurationSchemaMismatch");
        AuditChainDatabase.Require(string.Equals(content.Configuration.ContentHash,
            lineage.InputConfigurationContentHash, StringComparison.Ordinal),
            "RecipeDraftMigrationInputMismatch");
    }

    private static RecipeDraftMigrationLineage? ValidateRecipeDraftLineageHistory(
        RecipeDraftHead row, RecipeDraftContent content,
        RecipeDraftMigrationLineage? currentLineage,
        ICollection<RecipeDraftMigrationHistoryEntry> migrations)
    {
        var lineage = content.MigrationLineage;
        if (row.Revision == 1)
        {
            if (lineage is null) return null;
            AuditChainDatabase.Require(row.DraftId == lineage.Plan.TargetDraftId,
                "RecipeDraftMigrationTargetDraftMismatch");
            AuditChainDatabase.Require(row.OperationId == lineage.Plan.OperationId,
                "RecipeDraftMigrationOperationMismatch");
            AuditChainDatabase.Require(string.Equals(row.ChangeReason, lineage.Plan.ChangeReason,
                StringComparison.Ordinal), "RecipeDraftMigrationChangeReasonMismatch");
            ValidateMigrationLineageTarget(content, lineage);
            migrations.Add(new RecipeDraftMigrationHistoryEntry(row.Position, row.DraftId,
                row.Revision, row.OperationId, lineage));
            return lineage;
        }

        if (lineage is null)
        {
            AuditChainDatabase.Require(currentLineage is null,
                "RecipeDraftMigrationLineageRemoved");
            return null;
        }

        AuditChainDatabase.Require(currentLineage is not null,
            "RecipeDraftMigrationLineageAddedAfterInitialRevision");
        AuditChainDatabase.Require(string.Equals(currentLineage!.ContentHash, lineage.ContentHash,
                StringComparison.Ordinal), "RecipeDraftMigrationLineageImmutable");
        AuditChainDatabase.Require(row.OperationId != currentLineage.Plan.OperationId,
            "RecipeDraftMigrationOperationRevisionInvalid");
        return currentLineage;
    }

    private static void ValidateRecipeDraftMigrationSource(sqlite3 database,
        RecipeDraftMigrationHistoryEntry migration, RecipeDraftStoreOptions options,
        bool cameraSetupEnabled, StoreDeadline deadline)
    {
        var sourceReference = migration.Lineage.Plan.Source;
        var source = ReadRecipeDraftRow(database,
            "WHERE DraftId=? AND Revision=? LIMIT 2", deadline,
            sourceReference.DraftId.ToString("D"),
            sourceReference.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(source is not null, "RecipeDraftMigrationSourceMissing");
        AuditChainDatabase.Require(source!.Position < migration.TargetPosition,
            "RecipeDraftMigrationSourceOrderInvalid");
        AuditChainDatabase.Require(string.Equals(source.RevisionContentHash,
            sourceReference.RevisionContentHash, StringComparison.Ordinal),
            "RecipeDraftMigrationSourceHashMismatch");
        var sourceContent = DecodeAndValidateRecipeDraftRow(source!, options, cameraSetupEnabled);
        ValidateMigrationLineageSource(sourceContent, migration.Lineage);
    }
}
