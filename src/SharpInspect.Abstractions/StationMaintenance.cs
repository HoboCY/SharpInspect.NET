using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>A closed reason for stopping this installation. No reason authorizes an install or restore by itself.</summary>
public enum StationMaintenanceReason : byte { SoftwareUpgrade = 1, StationRecovery = 2, MaintenanceInspection = 3 }

/// <summary>Admission of a shutdown request is distinct from physical exit and coordinator takeover.</summary>
public enum StationMaintenancePhase : byte
{
    Unavailable = 0, Idle = 1, Authorizing = 2, Draining = 3, Retiring = 4,
    PreparedAwaitingHostExit = 5, SafeMaintenance = 6
}

/// <summary>Installation-bound limits. Missing work or expired authority never become successful retirement.</summary>
public sealed class StationMaintenancePolicy
{
    public StationMaintenancePolicy(TimeSpan retirementTimeout, TimeSpan handoffLifetime, TimeSpan hostExitTimeout)
    {
        if (retirementTimeout < TimeSpan.FromMilliseconds(100) || retirementTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(retirementTimeout));
        if (handoffLifetime < TimeSpan.FromSeconds(1) || handoffLifetime > TimeSpan.FromMinutes(15) || handoffLifetime <= retirementTimeout)
            throw new ArgumentOutOfRangeException(nameof(handoffLifetime));
        if (hostExitTimeout < TimeSpan.FromMilliseconds(100) || hostExitTimeout > TimeSpan.FromMinutes(1) || hostExitTimeout >= handoffLifetime)
            throw new ArgumentOutOfRangeException(nameof(hostExitTimeout));
        RetirementTimeout = retirementTimeout; HandoffLifetime = handoffLifetime; HostExitTimeout = hostExitTimeout;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "station-maintenance-policy-v1",
            retirementTimeout.Ticks.ToString(CultureInfo.InvariantCulture), handoffLifetime.Ticks.ToString(CultureInfo.InvariantCulture),
            hostExitTimeout.Ticks.ToString(CultureInfo.InvariantCulture) });
    }
    public TimeSpan RetirementTimeout { get; }
    public TimeSpan HandoffLifetime { get; }
    public TimeSpan HostExitTimeout { get; }
    public string ContentHash { get; }
}

/// <summary>A fresh human grant must name this exact operation, installation and reason.</summary>
public sealed record PrepareStationMaintenanceCommand : RuntimeCommand
{
    public PrepareStationMaintenanceCommand(Guid correlationId, CommandInvocation invocation, Guid operationId,
        string installationBindingHash, StationMaintenanceReason reason) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty || operationId == Guid.Empty) throw new ArgumentException("MaintenanceOperationIdRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        InstallationBindingHash = AlgorithmConfigurationValidation.Hash(installationBindingHash, nameof(installationBindingHash)).ToUpperInvariant();
        OperationId = operationId; Reason = reason;
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[] { "prepare-station-maintenance-v1",
            operationId.ToString("D"), InstallationBindingHash, ((byte)reason).ToString(CultureInfo.InvariantCulture) });
    }
    public Guid OperationId { get; }
    public string InstallationBindingHash { get; }
    public StationMaintenanceReason Reason { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>Read-only progress. Prepared means an authorized handoff awaits process exit, not takeover completion.</summary>
public sealed record StationMaintenanceSnapshot(bool Available, Guid RuntimeEpoch, Guid? OperationId,
    StationMaintenancePhase Phase, string ReasonCode, DateTimeOffset? ExpiresAtUtc, bool HostShutdownRequested);

public interface IStationMaintenanceQuery
{
    ValueTask<StationMaintenanceSnapshot> ReadMaintenanceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A separately registered host-lifetime callback. It requests process shutdown after durable handoff;
/// returning from it never proves that the process has exited. Ordinary page close must not call it.
/// </summary>
public interface IStationHostShutdown
{
    ValueTask RequestShutdownAsync(Guid operationId, CancellationToken cancellationToken = default);
}
