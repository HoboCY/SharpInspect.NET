using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Manual;

namespace SharpInspect.Runtime.Storage;

/// <summary>Exact dependency snapshot supplied to a Manual command evaluator.</summary>
internal sealed record ManualInspectionAdmissionInput(
    RecipeDraftRevision Draft,
    RecipeReleaseRecord? Release,
    CameraSetupStoreSnapshot CurrentBinding,
    RecipeActivationRecord? ActiveBaseline);

/// <summary>
/// Bounded state read from the schema-21 ledger before the authenticated writer
/// evaluates a command. The lists are immutable snapshots of one SQLite
/// transaction; they are not mutable authority.
/// </summary>
internal sealed record ManualInspectionCommandState(
    bool Enabled,
    ManualInspectionSessionHeader? Header,
    IReadOnlyList<ManualInspectionSessionEvent> Events,
    IReadOnlyList<ManualInspectionRunRecord> Runs,
    RecipeDraftRevision? Draft,
    RecipeReleaseRecord? Release,
    CameraSetupStoreSnapshot? CurrentBinding,
    RecipeActivationRecord? ActiveBaseline,
    bool RecoveryRequired,
    ManualInspectionSessionHeader? PendingHeader = null,
    CommandAuditFact? StartCommandFact = null,
    IReadOnlyList<CommandAuditFact>? PendingCommandFacts = null);

/// <summary>One command-owned append operation for the independent Manual ledger.</summary>
internal sealed record ManualInspectionMutation(
    ManualInspectionSessionHeader Header,
    ManualInspectionSessionEvent Event,
    ManualInspectionRunRecord? Run = null,
    IReadOnlyList<CommandAuditFact>? AdditionalTerminalFacts = null);

/// <summary>Startup projection used to resume or close accepted Manual work.</summary>
internal sealed record ManualInspectionRecoveryState(
    bool Available,
    string ReasonCode,
    ManualInspectionSessionHeader? Header = null,
    CommandAuditFact? StartFact = null,
    RecipeDraftRevision? Draft = null,
    RecipeReleaseRecord? Release = null,
    RecipeActivationRecord? ActiveBaseline = null,
    IReadOnlyList<ManualInspectionSessionEvent>? Events = null,
    IReadOnlyList<ManualInspectionRunRecord>? Runs = null,
    IReadOnlyList<CommandAuditFact>? PendingCommandFacts = null,
    bool RecoveryRequired = false)
{
    internal IReadOnlyList<ManualInspectionSessionEvent> SessionEvents => Events ??
        Array.Empty<ManualInspectionSessionEvent>();

    internal IReadOnlyList<ManualInspectionRunRecord> RunRecords => Runs ??
        Array.Empty<ManualInspectionRunRecord>();

    internal IReadOnlyList<CommandAuditFact> CommandFacts => PendingCommandFacts ??
        Array.Empty<CommandAuditFact>();
}

/// <summary>Resolved execution plan retained only inside the storage transaction.</summary>
internal sealed record ManualInspectionResolvedInputs(
    ManualRecipeExecutionPlan Plan,
    CameraSetupStoreSnapshot CurrentBinding,
    RecipeActivationRecord? ActiveBaseline);
