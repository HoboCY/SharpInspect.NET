using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Read-only presentation of every orthogonal field in one complete immutable snapshot.
/// Raw values remain available for inspection; displayed status is separately freshness-gated.
/// </summary>
public sealed class StationStateViewModel : ObservableObject
{
    private StationStateSnapshot? _rawSnapshot;
    private bool _isFresh;

    public StationStateSnapshot? RawSnapshot => _rawSnapshot;
    public bool HasSnapshot => _rawSnapshot is not null;
    public bool IsFresh => _isFresh && _rawSnapshot is not null;

    public Guid RuntimeEpoch => _rawSnapshot?.RuntimeEpoch ?? Guid.Empty;
    public long Revision => _rawSnapshot?.Revision ?? 0;
    public DateTimeOffset? ObservedAtUtc => _rawSnapshot?.ObservedAtUtc;
    public RuntimeLifecycle Lifecycle => _rawSnapshot?.Lifecycle ?? RuntimeLifecycle.Stopped;
    public ExclusiveMode Mode => _rawSnapshot?.Mode ?? ExclusiveMode.None;
    public ProductionArmState ArmState => _rawSnapshot?.ArmState ?? ProductionArmState.Disarmed;
    public HandshakePhase Handshake => _rawSnapshot?.Handshake ?? HandshakePhase.Unknown;
    public RecoveryState Recovery => _rawSnapshot?.Recovery ?? RecoveryState.Required;
    public RecipeReference? ActiveRecipe => _rawSnapshot?.ActiveRecipe;
    public ExecutionCorrelationId? CurrentExecution => _rawSnapshot?.CurrentExecution;
    public CameraHealth Camera => _rawSnapshot?.Camera ?? UnknownCamera;
    public PlcHealth Plc => _rawSnapshot?.Plc ?? UnknownPlc;
    public SubsystemHealth Store => _rawSnapshot?.Store ?? UnknownSubsystem;
    public EvidenceHealth Evidence => _rawSnapshot?.Evidence ?? UnknownEvidence;
    public QualificationState Qualification => _rawSnapshot?.Qualification ?? MissingQualification;
    public PerformanceHealth Performance => _rawSnapshot?.Performance ?? UnknownPerformance;
    public AlarmSummary Alarms => _rawSnapshot?.Alarms ?? EmptyAlarms;
    public InteractiveSession Session => _rawSnapshot?.Session ?? UnauthenticatedSession;
    public CommandProgress? LastCommand => _rawSnapshot?.LastCommand;
    public AdmissionBlockers AdmissionBlockers => _rawSnapshot?.AdmissionBlockers ?? EmptyBlockers;

    /// <summary>Visible Ready is false whenever presentation freshness is not trusted.</summary>
    public bool Ready => IsFresh && (_rawSnapshot?.Ready ?? false);
    public bool Busy => _rawSnapshot?.Busy ?? false;
    public bool DisplayedHealthy => IsFresh &&
        Camera.Connection == HealthState.Healthy && Plc.Connection == HealthState.Healthy &&
        Store.State == HealthState.Healthy;
    public HealthState DisplayedCameraConnection => IsFresh ? Camera.Connection : HealthState.Unknown;
    public HealthState DisplayedPlcConnection => IsFresh ? Plc.Connection : HealthState.Unknown;
    public HealthState DisplayedStoreState => IsFresh ? Store.State : HealthState.Unknown;
    public CameraHealth DisplayedCamera => IsFresh ? Camera : UnknownCamera;
    public PlcHealth DisplayedPlc => IsFresh ? Plc : UnknownPlc;
    public SubsystemHealth DisplayedStore => IsFresh ? Store : UnknownSubsystem;
    public EvidenceHealth DisplayedEvidence => IsFresh ? Evidence : UnknownEvidence;
    public QualificationState DisplayedQualification => IsFresh ? Qualification : MissingQualification;
    public PerformanceHealth DisplayedPerformance => IsFresh ? Performance : UnknownPerformance;
    public HealthState DisplayedEvidenceState => DisplayedEvidence.State;
    public bool DisplayedPerformanceBudgetViolation => IsFresh && Performance.BudgetViolation;

    internal void SetSnapshot(StationStateSnapshot snapshot, bool fresh)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _rawSnapshot = snapshot;
        _isFresh = fresh;
        OnPropertyChanged(string.Empty);
    }

    internal void SetUnknown()
    {
        _isFresh = false;
        OnPropertyChanged(nameof(IsFresh));
        OnPropertyChanged(nameof(Ready));
        OnPropertyChanged(nameof(DisplayedHealthy));
        OnPropertyChanged(nameof(DisplayedCameraConnection));
        OnPropertyChanged(nameof(DisplayedPlcConnection));
        OnPropertyChanged(nameof(DisplayedStoreState));
        OnPropertyChanged(nameof(DisplayedEvidenceState));
        OnPropertyChanged(nameof(DisplayedCamera));
        OnPropertyChanged(nameof(DisplayedPlc));
        OnPropertyChanged(nameof(DisplayedStore));
        OnPropertyChanged(nameof(DisplayedEvidence));
        OnPropertyChanged(nameof(DisplayedQualification));
        OnPropertyChanged(nameof(DisplayedPerformance));
        OnPropertyChanged(nameof(DisplayedPerformanceBudgetViolation));
    }

    private static readonly CameraHealth UnknownCamera =
        new(HealthState.Unknown, HealthState.Unknown, HealthState.Unknown, HealthState.Unknown);
    private static readonly PlcHealth UnknownPlc =
        new(HealthState.Unknown, HealthState.Unknown, HealthState.Unknown);
    private static readonly SubsystemHealth UnknownSubsystem = new(HealthState.Unknown, "SnapshotUnavailable");
    private static readonly EvidenceHealth UnknownEvidence = new(HealthState.Unknown, 0, 0);
    private static readonly QualificationState MissingQualification = new(
        QualificationMatch.Missing, QualificationMatch.Missing,
        QualificationMatch.Missing, QualificationMatch.Missing);
    private static readonly PerformanceHealth UnknownPerformance = new(HealthState.Unknown, false);
    private static readonly AlarmSummary EmptyAlarms = new(0, 0, false);
    private static readonly InteractiveSession UnauthenticatedSession =
        new(InteractiveSessionState.Unauthenticated, null, null);
    private static readonly AdmissionBlockers EmptyBlockers = new(Array.Empty<string>());
}
