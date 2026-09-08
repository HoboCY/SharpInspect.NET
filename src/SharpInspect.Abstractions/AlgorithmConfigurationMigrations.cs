using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>An exact persisted local Draft revision. Released/imported sources require future capabilities.</summary>
public sealed record RecipeDraftRevisionReference
{
    public RecipeDraftRevisionReference(Guid draftId, long revision, string revisionContentHash)
    {
        if (draftId == Guid.Empty) throw new ArgumentException("RecipeDraftIdRequired", nameof(draftId));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        DraftId = draftId; Revision = revision;
        RevisionContentHash = AlgorithmConfigurationValidation.Hash(revisionContentHash,
            nameof(revisionContentHash)).ToUpperInvariant();
    }
    public Guid DraftId { get; }
    public long Revision { get; }
    public string RevisionContentHash { get; }
}

/// <summary>The complete, explicit registration boundary of a host-trusted configuration transformer.</summary>
public sealed class AlgorithmConfigurationMigrationDescriptor
{
    public AlgorithmConfigurationMigrationDescriptor(RecipeContractReference migrator,
        AlgorithmIdentity sourceAlgorithm, RecipeContractReference sourceSchema,
        AlgorithmIdentity targetAlgorithm, RecipeContractReference targetSchema)
    {
        Migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        SourceAlgorithm = sourceAlgorithm ?? throw new ArgumentNullException(nameof(sourceAlgorithm));
        SourceSchema = sourceSchema ?? throw new ArgumentNullException(nameof(sourceSchema));
        TargetAlgorithm = targetAlgorithm ?? throw new ArgumentNullException(nameof(targetAlgorithm));
        TargetSchema = targetSchema ?? throw new ArgumentNullException(nameof(targetSchema));
        ContentHash = AlgorithmContractValidation.HashParts(new[] {
            "sharpinspect-configuration-migrator-v1", migrator.Id, migrator.Version, migrator.ContentHash,
            sourceAlgorithm.Id, sourceAlgorithm.Version, sourceSchema.Id, sourceSchema.Version, sourceSchema.ContentHash,
            targetAlgorithm.Id, targetAlgorithm.Version, targetSchema.Id, targetSchema.Version, targetSchema.ContentHash });
    }
    public RecipeContractReference Migrator { get; }
    public AlgorithmIdentity SourceAlgorithm { get; }
    public RecipeContractReference SourceSchema { get; }
    public AlgorithmIdentity TargetAlgorithm { get; }
    public RecipeContractReference TargetSchema { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable intent, also used as the complete Step-Up authorization target.</summary>
public sealed class RecipeDraftMigrationPlan
{
    public RecipeDraftMigrationPlan(Guid operationId, RecipeDraftRevisionReference source, Guid targetDraftId,
        AlgorithmIdentity targetAlgorithm, RecipeContractReference targetSchema,
        RecipeContractReference migrator, string changeReason)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("RecipeDraftOperationIdRequired", nameof(operationId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (targetDraftId == Guid.Empty || targetDraftId == source.DraftId)
            throw new ArgumentException("RecipeDraftMigrationNewDraftRequired", nameof(targetDraftId));
        OperationId = operationId; TargetDraftId = targetDraftId;
        TargetAlgorithm = targetAlgorithm ?? throw new ArgumentNullException(nameof(targetAlgorithm));
        TargetSchema = targetSchema ?? throw new ArgumentNullException(nameof(targetSchema));
        Migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        ChangeReason = AlgorithmContractValidation.BoundedText(changeReason, nameof(changeReason), 256);
        if (string.IsNullOrWhiteSpace(ChangeReason)) throw new ArgumentException("RecipeDraftChangeReasonRequired");
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-draft-migration-plan-v1",
            operationId.ToString("D"), source.DraftId.ToString("D"), source.Revision.ToString(CultureInfo.InvariantCulture),
            source.RevisionContentHash, targetDraftId.ToString("D"), targetAlgorithm.Id, targetAlgorithm.Version,
            targetSchema.Id, targetSchema.Version, targetSchema.ContentHash,
            migrator.Id, migrator.Version, migrator.ContentHash, ChangeReason });
    }
    public Guid OperationId { get; }
    public RecipeDraftRevisionReference Source { get; }
    public Guid TargetDraftId { get; }
    public AlgorithmIdentity TargetAlgorithm { get; }
    public RecipeContractReference TargetSchema { get; }
    public RecipeContractReference Migrator { get; }
    public string ChangeReason { get; }
    public string ContentHash { get; }
}

/// <summary>One immutable migration hop. The enclosing persisted revision supplies the verified actor and UTC time.</summary>
public sealed class RecipeDraftMigrationLineage
{
    internal RecipeDraftMigrationLineage(RecipeDraftMigrationPlan plan,
        AlgorithmConfigurationMigrationDescriptor descriptor, string inputConfigurationContentHash,
        string initialOutputConfigurationContentHash, IEnumerable<AlgorithmValidationIssue>? warnings)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        if (plan.Migrator != descriptor.Migrator || plan.TargetSchema != descriptor.TargetSchema ||
            plan.TargetAlgorithm.Id != descriptor.TargetAlgorithm.Id ||
            plan.TargetAlgorithm.Version != descriptor.TargetAlgorithm.Version)
            throw new ArgumentException("RecipeDraftMigrationDescriptorMismatch");
        InputConfigurationContentHash = AlgorithmConfigurationValidation.Hash(inputConfigurationContentHash,
            nameof(inputConfigurationContentHash)).ToUpperInvariant();
        InitialOutputConfigurationContentHash = AlgorithmConfigurationValidation.Hash(initialOutputConfigurationContentHash,
            nameof(initialOutputConfigurationContentHash)).ToUpperInvariant();
        Warnings = MigrationDiagnostics.Copy(warnings);
        var parts = new List<string?> { "sharpinspect-recipe-draft-migration-lineage-v1", plan.ContentHash,
            descriptor.ContentHash, InputConfigurationContentHash, InitialOutputConfigurationContentHash,
            Warnings.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var warning in Warnings) { parts.Add(warning.Code); parts.Add(warning.FieldKey); }
        ContentHash = AlgorithmContractValidation.HashParts(parts);
    }
    public RecipeDraftMigrationPlan Plan { get; }
    public AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
    public string InputConfigurationContentHash { get; }
    public string InitialOutputConfigurationContentHash { get; }
    public ReadOnlyCollection<AlgorithmValidationIssue> Warnings { get; }
    public string ContentHash { get; }
    public bool ApprovalInherited => false;
}

/// <summary>Read-only configuration input; no device, store, session or approval capabilities.</summary>
public sealed class AlgorithmConfigurationMigrationContext
{
    internal AlgorithmConfigurationMigrationContext(RecipeDraftRevisionReference sourceRevision,
        RecipeAlgorithmBinding sourceBinding, AlgorithmConfigurationSnapshot sourceConfiguration,
        AlgorithmDescriptor targetDescriptor)
    { SourceRevision = sourceRevision; SourceBinding = sourceBinding;
      SourceConfiguration = sourceConfiguration; TargetDescriptor = targetDescriptor; }
    public RecipeDraftRevisionReference SourceRevision { get; }
    public RecipeAlgorithmBinding SourceBinding { get; }
    public AlgorithmConfigurationSnapshot SourceConfiguration { get; }
    public AlgorithmDescriptor TargetDescriptor { get; }
}

public sealed class AlgorithmConfigurationMigrationTransformResult
{
    public AlgorithmConfigurationMigrationTransformResult(bool succeeded, string reasonCode,
        IEnumerable<AlgorithmConfigurationEntry>? values = null,
        IEnumerable<AlgorithmValidationIssue>? issues = null)
    {
        Succeeded = succeeded;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        Values = AlgorithmContractValidation.Copy(values, nameof(values), 256);
        Issues = MigrationDiagnostics.Copy(issues);
        if (!succeeded && Values.Count != 0)
            throw new ArgumentException("RecipeDraftMigrationFailedOutputForbidden", nameof(values));
    }
    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<AlgorithmConfigurationEntry> Values { get; }
    public ReadOnlyCollection<AlgorithmValidationIssue> Issues { get; }
}

public interface IAlgorithmConfigurationMigrator
{
    AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
    ValueTask<AlgorithmConfigurationMigrationTransformResult> MigrateAsync(
        AlgorithmConfigurationMigrationContext context, CancellationToken cancellationToken = default);
}

public sealed record RecipeDraftMigrationRequest(RecipeDraftMigrationPlan Plan,
    CommandInvocation Invocation, Guid? StepUpGrantId = null);
public sealed record RecipeDraftMigrationResult(bool Created, string ReasonCode,
    RecipeDraftRevision? Revision, IReadOnlyList<AlgorithmValidationIssue> Issues);

/// <summary>Explicit authoring only. It never loads automatically, approves, releases or activates a Recipe.</summary>
public interface IAlgorithmConfigurationMigrationService
{
    IReadOnlyList<AlgorithmConfigurationMigrationDescriptor> Migrations { get; }
    ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<RecipeDraftMigrationResult> MigrateAsync(RecipeDraftMigrationRequest request,
        CancellationToken cancellationToken = default);
}

internal static class MigrationDiagnostics
{
    internal static ReadOnlyCollection<AlgorithmValidationIssue> Copy(IEnumerable<AlgorithmValidationIssue>? values)
    {
        var copied = AlgorithmContractValidation.Copy(values, nameof(values), 32);
        foreach (var issue in copied)
        {
            _ = AlgorithmContractValidation.Identifier(issue.Code, nameof(issue.Code));
            if (issue.FieldKey is not null)
                _ = AlgorithmConfigurationValidation.Identifier(issue.FieldKey, nameof(issue.FieldKey));
        }
        return copied;
    }
}
