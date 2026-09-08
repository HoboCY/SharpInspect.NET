namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Bounded local-store settings. The database path is validated again by each capability
/// before it opens a file; these options never bypass the production volume checks.
/// </summary>
public sealed class ProductionStoreOptions
{
    public ProductionStoreOptions()
    {
    }

    public ProductionStoreOptions(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; init; } = string.Empty;

    public TimeSpan CommitTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int QueueCapacity { get; init; } = 64;

    public Integrity.AuditIntegrityPolicy? AuditIntegrityPolicy { get; init; }
    public Identity.LocalIdentityOptions? LocalIdentity { get; init; }
    public SharpInspect.Abstractions.AlarmPolicy? AlarmPolicy { get; init; }
    public SharpInspect.Abstractions.IExternalAuditAnchor? ExternalAuditAnchor { get; init; }
    /// <summary>Explicit opt-in for the schema 8 DevelopmentComputation archive.</summary>
    public AlgorithmResultArchiveOptions? AlgorithmResultArchive { get; init; }

    /// <summary>Explicit opt-in for the schema 9 append-only Recipe Draft ledger.</summary>
    public RecipeDraftStoreOptions? RecipeDrafts { get; init; }

    /// <summary>Explicit opt-in for the schema 10 camera binding and setup ledger.</summary>
    public CameraSetupStoreOptions? CameraSetup { get; init; }
}
