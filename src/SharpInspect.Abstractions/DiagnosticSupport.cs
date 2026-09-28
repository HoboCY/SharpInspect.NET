using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>A closed maintenance reason; never free text containing technical detail or secrets.</summary>
public enum DiagnosticSupportReason : byte
{
    MaintenanceInvestigation = 1, FaultInvestigation = 2, AcceptanceSupport = 3
}

/// <summary>One immutable requested elevation. Runtime also checks every value against its deployed policy.</summary>
public sealed class DiagnosticCaptureProfile
{
    public DiagnosticCaptureProfile(string loggingPolicyHash, IEnumerable<string> components,
        DiagnosticLevel minimumLevel, TimeSpan duration, int maximumEvents)
    {
        LoggingPolicyHash = TraceStoragePolicyValidation.Hash(loggingPolicyHash, nameof(loggingPolicyHash));
        var copied = AlgorithmContractValidation.Copy(components, nameof(components), 32);
        if (copied.Count == 0 || copied.Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("DiagnosticCaptureComponentsInvalid");
        foreach (var component in copied) AlgorithmContractValidation.Identifier(component, nameof(components));
        if (minimumLevel is not (DiagnosticLevel.Trace or DiagnosticLevel.Debug) ||
            duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1) || maximumEvents is < 1 or > 1_000_000)
            throw new ArgumentException("DiagnosticCaptureBoundsInvalid");
        Components = Array.AsReadOnly(copied.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        MinimumLevel = minimumLevel; Duration = duration; MaximumEvents = maximumEvents;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "diagnostic-capture-profile-v1", LoggingPolicyHash,
            minimumLevel.ToString(), duration.Ticks.ToString(CultureInfo.InvariantCulture),
            maximumEvents.ToString(CultureInfo.InvariantCulture) }.Concat(Components));
    }
    public string LoggingPolicyHash { get; }
    public ReadOnlyCollection<string> Components { get; }
    public DiagnosticLevel MinimumLevel { get; }
    public TimeSpan Duration { get; }
    public int MaximumEvents { get; }
    public string ContentHash { get; }
}

/// <summary>Explicit bounded UTC time and Runtime identity, with optional additional correlation restrictions.</summary>
public sealed class SupportBundleScope
{
    public SupportBundleScope(Guid runtimeEpoch, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IEnumerable<ExecutionCorrelationId> executions, IEnumerable<Guid> commandCorrelations,
        IEnumerable<Guid> captureSessions)
    {
        if (runtimeEpoch == Guid.Empty || fromUtc.Offset != TimeSpan.Zero || throughUtc.Offset != TimeSpan.Zero ||
            throughUtc <= fromUtc || throughUtc - fromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("SupportBundleScopeInvalid");
        var executionList = AlgorithmContractValidation.Copy(executions, nameof(executions), 64);
        var commandList = AlgorithmContractValidation.Copy(commandCorrelations, nameof(commandCorrelations), 64);
        var captureList = AlgorithmContractValidation.Copy(captureSessions, nameof(captureSessions), 64);
        if (executionList.Count + commandList.Count + captureList.Count > 64 ||
            executionList.Distinct().Count() != executionList.Count || commandList.Distinct().Count() != commandList.Count ||
            captureList.Distinct().Count() != captureList.Count || commandList.Any(value => value == Guid.Empty) ||
            captureList.Any(value => value == Guid.Empty) ||
            executionList.Any(value => !Enum.IsDefined(value.Kind) || value.Value == Guid.Empty))
            throw new ArgumentException("SupportBundleScopeIdentitiesInvalid");
        RuntimeEpoch = runtimeEpoch; FromUtc = fromUtc; ThroughUtc = throughUtc;
        Executions = Array.AsReadOnly(executionList.OrderBy(value => (int)value.Kind).ThenBy(value => value.Value).ToArray());
        CommandCorrelations = Array.AsReadOnly(commandList.OrderBy(value => value).ToArray());
        CaptureSessions = Array.AsReadOnly(captureList.OrderBy(value => value).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "support-bundle-scope-v1", runtimeEpoch.ToString("D"),
            fromUtc.ToString("O", CultureInfo.InvariantCulture), throughUtc.ToString("O", CultureInfo.InvariantCulture),
            "executions" }.Concat(Executions.SelectMany(value => new[] { value.Kind.ToString(), value.Value.ToString("D") }))
            .Concat(new[] { "commands" }).Concat(CommandCorrelations.Select(value => value.ToString("D")))
            .Concat(new[] { "captures" }).Concat(CaptureSessions.Select(value => value.ToString("D"))));
    }
    public Guid RuntimeEpoch { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ThroughUtc { get; }
    public ReadOnlyCollection<ExecutionCorrelationId> Executions { get; }
    public ReadOnlyCollection<Guid> CommandCorrelations { get; }
    public ReadOnlyCollection<Guid> CaptureSessions { get; }
    public string ContentHash { get; }
}

/// <summary>Capture/export commands grant no destination-path, raw-data, or production authority.</summary>
public abstract record DiagnosticSupportCommand : RuntimeCommand
{
    protected DiagnosticSupportCommand(Guid correlationId, CommandInvocation invocation, Guid operationId,
        DiagnosticSupportReason reason) : base(correlationId, invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (correlationId == Guid.Empty || operationId == Guid.Empty || !Enum.IsDefined(reason))
            throw new ArgumentException("DiagnosticSupportCommandInvalid");
        OperationId = operationId; Reason = reason;
    }
    public Guid OperationId { get; }
    public DiagnosticSupportReason Reason { get; }
    public abstract string AuthorizationTarget { get; }
    protected string Target(string kind, params string?[] values) => AlgorithmContractValidation.HashParts(
        new[] { "diagnostic-support-command-v1", kind, OperationId.ToString("D"), Reason.ToString() }.Concat(values));
}

public sealed record StartDiagnosticCaptureCommand : DiagnosticSupportCommand
{
    public StartDiagnosticCaptureCommand(Guid correlationId, CommandInvocation invocation, Guid captureSessionId,
        DiagnosticCaptureProfile profile, DiagnosticSupportReason reason) : base(correlationId, invocation, captureSessionId, reason)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        AuthorizationTarget = Target("StartCapture", profile.ContentHash);
    }
    public DiagnosticCaptureProfile Profile { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record StopDiagnosticCaptureCommand : DiagnosticSupportCommand
{
    public StopDiagnosticCaptureCommand(Guid correlationId, CommandInvocation invocation, Guid captureSessionId,
        DiagnosticSupportReason reason) : base(correlationId, invocation, captureSessionId, reason)
    { AuthorizationTarget = Target("StopCapture"); }
    public override string AuthorizationTarget { get; }
}

public sealed record CreateSupportBundleCommand : DiagnosticSupportCommand
{
    public CreateSupportBundleCommand(Guid correlationId, CommandInvocation invocation, Guid operationId,
        SupportBundleScope scope, string supportPolicyHash, DiagnosticSupportReason reason)
        : base(correlationId, invocation, operationId, reason)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        SupportPolicyHash = TraceStoragePolicyValidation.Hash(supportPolicyHash, nameof(supportPolicyHash));
        AuthorizationTarget = Target("CreateBundle", scope.ContentHash, SupportPolicyHash);
    }
    public SupportBundleScope Scope { get; }
    public string SupportPolicyHash { get; }
    public override string AuthorizationTarget { get; }
}

public enum DiagnosticCapturePhase : byte
{
    Baseline = 1, Admitted = 2, Active = 3, Draining = 4, Completed = 5, Failed = 6, Interrupted = 7
}

public enum SupportBundlePhase : byte
{
    Idle = 1, Admitted = 2, Collecting = 3, Staged = 4, Completed = 5, Failed = 6, Interrupted = 7
}

/// <summary>Scalar current-state observation. Neither this snapshot nor an Accepted command proves completion.</summary>
public sealed record DiagnosticCaptureSnapshot(bool Configured, Guid RuntimeEpoch, Guid? CaptureSessionId,
    DiagnosticCapturePhase Phase, bool Elevated, string ReasonCode, string? ProfileHash,
    long ObservedEvents, int? MaximumEvents, DateTimeOffset? StartedAtUtc, DateTimeOffset? ExpiresAtUtc);

public sealed record SupportBundleSnapshot(bool Configured, Guid RuntimeEpoch, Guid? BundleId,
    SupportBundlePhase Phase, string ReasonCode, string? ContentHash, long? Bytes, DateTimeOffset? ExpiresAtUtc);

public interface IDiagnosticSupportQuery
{
    DiagnosticCaptureSnapshot ReadCapture();
    SupportBundleSnapshot ReadBundle();
}

public sealed record SupportBundleReadRequest(Guid BundleId, CommandInvocation Invocation);
public sealed record SupportBundleReadResult(bool Available, string ReasonCode, string? ContentHash, ReadOnlyMemory<byte> Bytes);
public interface ISupportBundleReader
{
    ValueTask<SupportBundleReadResult> ReadAsync(SupportBundleReadRequest request, CancellationToken cancellationToken = default);
}
