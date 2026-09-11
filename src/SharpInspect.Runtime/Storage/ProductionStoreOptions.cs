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

    /// <summary>Explicit opt-in for the schema 11 camera recovery authorization ledger.</summary>
    public CameraRecoveryStoreOptions? CameraRecovery { get; init; }

    /// <summary>Explicit opt-in for the schema 12 camera network maintenance ledger.</summary>
    public CameraNetworkStoreOptions? CameraNetwork { get; init; }

    /// <summary>Explicit opt-in for the schema 13 immutable imaging setup declaration ledger.</summary>
    public ImagingSetupStoreOptions? ImagingSetup { get; init; }

    /// <summary>Explicit opt-in for the schema 14 governed calibration-session evidence store.</summary>
    public CalibrationSessionStoreOptions? CalibrationSessions { get; init; }

    /// <summary>Explicit opt-in for the schema 15 calibration-governance ledger.</summary>
    public CalibrationGovernanceStoreOptions? CalibrationGovernance { get; init; }

    /// <summary>Explicit opt-in for the schema 16 immutable released-recipe ledger.</summary>
    public RecipeReleaseStoreOptions? RecipeReleases { get; init; }

    /// <summary>Explicit opt-in for the schema 17 immutable PLC result contract ledger. Requires
    /// recipe drafts, released recipes, local identity, and audit integrity options.</summary>
    public PlcResultContractStoreOptions? PlcResultContracts { get; init; }

    /// <summary>Explicit opt-in for the schema 18 immutable recipe activation ledger. Requires
    /// camera setup, recipe drafts, released recipes, PLC result contracts, local identity,
    /// and audit integrity options.</summary>
    public RecipeActivationStoreOptions? RecipeActivations { get; init; }

    /// <summary>Explicit opt-in for the schema 19 immutable non-production Preview session ledger.
    /// Requires local identity, audit integrity, recipe drafts, camera setup, released recipes,
    /// and recipe activations. Preview records no frames and never grants production authority.</summary>
    public PreviewSessionStoreOptions? PreviewSessions { get; init; }

    /// <summary>Explicit opt-in for the schema 20 bounded calibration-import provenance ledger.
    /// Requires the complete schema 19 preview stack plus calibration governance, sessions,
    /// and imaging setup. Imported bytes remain untrusted until the import workflow verifies them.</summary>
    public CalibrationImportStoreOptions? CalibrationImports { get; init; }

    /// <summary>Explicit opt-in for the schema 21 independent non-production Manual Inspection ledger.</summary>
    public ManualInspectionStoreOptions? ManualInspections { get; init; }

    /// <summary>Explicit opt-in for the schema 22 immutable production-admission report ledger.
    /// Reports bind the evaluated gate set and current durable heads; they do not issue
    /// qualification or change Runtime state. The ledger requires only local identity and
    /// central audit integrity, while each missing required gate remains a rejection.</summary>
    public ProductionAdmissionStoreOptions? ProductionAdmission { get; init; }

    /// <summary>Explicit opt-in for the schema 23 non-production station qualification ledger.
    /// It stores only bounded qualification session provenance and typed facility observations;
    /// it never produces a production result or activation.</summary>
    public StationQualificationStoreOptions? StationQualifications { get; init; }

    /// <summary>Explicit opt-in for the schema 24 signed recipe-transfer ledger.
    /// Requires local identity, audit integrity, and recipe drafts.</summary>
    public RecipeTransferStoreOptions? RecipeTransfers { get; init; }

    /// <summary>Explicit opt-in for the schema 25 versioned trace-storage-policy ledger.
    /// Requires only local identity and central audit integrity; the policy is
    /// deployment governance and is independent from recipe and camera ledgers.</summary>
    public TraceStoragePolicyStoreOptions? TraceStoragePolicies { get; init; }

    /// <summary>Explicit opt-in for the schema 26 isolated qualification-cycle ledger.</summary>
    public QualificationCycleStoreOptions? QualificationCycles { get; init; }

    /// <summary>Explicit opt-in for the schema 27 append-only PLC communication ledger.</summary>
    public PlcCommunicationStoreOptions? PlcCommunication { get; init; }

    /// <summary>Explicit opt-in for the schema 28 immutable production inspection Core ledger.</summary>
    public ProductionInspectionStoreOptions? ProductionInspections { get; init; }

    /// <summary>Explicit opt-in for the schema 29 part-identity rejection/correction ledger.
    /// Accepted identity evidence remains in the production admission/Core records; this
    /// independent ledger stores rejected triggers and authorized historical corrections.</summary>
    public PartIdentityStoreOptions? PartIdentities { get; init; }

    /// <summary>Explicit opt-in for schema 30 audited manual production recovery.
    /// Requires production inspections, local identity and central audit integrity.</summary>
    public ProductionRecoveryStoreOptions? ProductionRecovery { get; init; }

    /// <summary>Explicit opt-in for schema 31 immutable recipe selection governance and PLC handshakes.
    /// Requires recipe activation, PLC communication, local identity and central audit integrity.</summary>
    public RecipeSelectionStoreOptions? RecipeSelections { get; init; }
}
