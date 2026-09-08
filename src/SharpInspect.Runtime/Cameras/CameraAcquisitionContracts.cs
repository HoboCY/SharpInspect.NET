using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Host-side bounds for one controlled camera acquisition service. The camera's
/// effective configuration supplies the frame deadline; these bounds cover the
/// adapter acknowledgement and the service shutdown/protocol surfaces.
/// </summary>
public sealed class CameraAcquisitionOptions
{
    public CameraAcquisitionOptions(TimeSpan pendingInstallTimeout,
        TimeSpan shutdownWaitTimeout, int protocolCapacity = 256,
        TimeSpan? protocolReadTimeout = null)
    {
        ValidateTimeout(pendingInstallTimeout, nameof(pendingInstallTimeout));
        ValidateTimeout(shutdownWaitTimeout, nameof(shutdownWaitTimeout));
        if (protocolCapacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(protocolCapacity));

        var readTimeout = protocolReadTimeout ?? TimeSpan.FromMilliseconds(250);
        ValidateTimeout(readTimeout, nameof(protocolReadTimeout));

        PendingInstallTimeout = pendingInstallTimeout;
        ShutdownWaitTimeout = shutdownWaitTimeout;
        ProtocolCapacity = protocolCapacity;
        ProtocolReadTimeout = readTimeout;
    }

    public TimeSpan PendingInstallTimeout { get; }
    public TimeSpan ShutdownWaitTimeout { get; }
    public int ProtocolCapacity { get; }
    public TimeSpan ProtocolReadTimeout { get; }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>Current service-owned acquisition gate projection.</summary>
public sealed class CameraAcquisitionBusySnapshot
{
    internal CameraAcquisitionBusySnapshot(ExecutionCorrelationId correlation,
        string logicalCameraRole, FrameAcquisitionStart? start, bool isBusy,
        bool cleanupPending, string reasonCode)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        LogicalCameraRole = logicalCameraRole ?? throw new ArgumentNullException(nameof(logicalCameraRole));
        Start = start;
        IsBusy = isBusy;
        CleanupPending = cleanupPending;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
    }

    public ExecutionCorrelationId Correlation { get; }
    public string LogicalCameraRole { get; }
    public FrameAcquisitionStart? Start { get; }
    public bool IsBusy { get; }
    public bool CleanupPending { get; }
    public string ReasonCode { get; }
}

/// <summary>One accepted camera attempt. Admission and its eventual frame outcome are separate.</summary>
public sealed class CameraAcquisitionAttempt
{
    internal CameraAcquisitionAttempt(bool accepted, string reasonCode,
        ExecutionCorrelationId? correlation, CameraAcquisitionOutcome? outcome)
    {
        Accepted = accepted;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        Correlation = correlation;
        Outcome = outcome;
    }

    public bool Accepted { get; }
    public ExecutionCorrelationId? Correlation { get; }
    public string ReasonCode { get; }
    public CameraAcquisitionOutcome? Outcome { get; }
}

/// <summary>
/// Terminal result for an accepted controlled acquisition. A successful result
/// transfers exactly one frame lease to its caller; failure results never expose one.
/// </summary>
public sealed class CameraAcquisitionOutcome : IDisposable
{
    internal CameraAcquisitionOutcome(ExecutionCorrelationId correlation,
        string logicalCameraRole, FrameAcquisitionStart? start,
        IFrameBufferLease? lease, CameraAcquisitionFailure? failure,
        string reasonCode, ExecutionStatus executionStatus)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        LogicalCameraRole = logicalCameraRole ?? throw new ArgumentNullException(nameof(logicalCameraRole));
        if ((lease is null) == (failure is null))
            throw new ArgumentException("CameraAcquisitionOutcomeStateInvalid");
        Start = start;
        _lease = lease;
        Failure = failure;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        ExecutionStatus = executionStatus;
        Decision = InspectionDecision.Unknown;
    }

    private IFrameBufferLease? _lease;

    public ExecutionCorrelationId Correlation { get; }
    public string LogicalCameraRole { get; }
    public FrameAcquisitionStart? Start { get; }
    public IFrameBufferLease? Lease => Volatile.Read(ref _lease);
    public CameraAcquisitionFailure? Failure { get; }
    public bool Succeeded => Failure is null;
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public bool IsUnknown => Failure is not null;
    public string ReasonCode { get; }
    public CameraAcquisitionFailureKind? FailureKind => Failure?.Kind;

    /// <summary>Atomically transfers the successful frame lease to its consumer.</summary>
    public IFrameBufferLease? TakeFrame() => Interlocked.Exchange(ref _lease, null);

    /// <summary>Returns an unclaimed successful lease, if any, to its owner.</summary>
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
