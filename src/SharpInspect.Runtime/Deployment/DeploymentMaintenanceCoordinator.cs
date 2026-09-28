using System.Diagnostics;
using System.Security.Cryptography;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Deployment;

/// <summary>Explicit independent-coordinator configuration. The trust key is provisioned separately from a handoff.</summary>
public sealed class DeploymentMaintenanceCoordinatorOptions
{
    public DeploymentMaintenanceCoordinatorOptions(StationInstallationOptions installation, ProductionStoreOptions store,
        string trustedAuditPublicKeyBase64, TimeSpan lockTimeout)
    {
        Installation = installation ?? throw new ArgumentNullException(nameof(installation));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        if (store.StationMaintenance is null || store.StationMaintenance.InstallationBindingHash != installation.BindingHash ||
            store.StationInstallation?.BindingHash != installation.BindingHash ||
            !string.Equals(store.DatabasePath, installation.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("StationMaintenanceCoordinatorBindingMismatch");
        if (lockTimeout < TimeSpan.Zero || lockTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        if (trustedAuditPublicKeyBase64 is not { Length: > 0 and <= 1024 })
            throw new ArgumentException("StationMaintenanceTrustKeyRequired", nameof(trustedAuditPublicKeyBase64));
        try
        {
            var bytes = Convert.FromBase64String(trustedAuditPublicKeyBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new ArgumentException("StationMaintenanceTrustKeyInvalid");
        }
        catch (Exception failure) when (failure is FormatException or CryptographicException)
        { throw new ArgumentException("StationMaintenanceTrustKeyInvalid", nameof(trustedAuditPublicKeyBase64)); }
        TrustedAuditPublicKeyBase64 = trustedAuditPublicKeyBase64;
        LockTimeout = lockTimeout;
    }
    public StationInstallationOptions Installation { get; }
    public ProductionStoreOptions Store { get; }
    public string TrustedAuditPublicKeyBase64 { get; }
    public TimeSpan LockTimeout { get; }
}

public sealed record DeploymentMaintenanceTakeoverResult(bool TakenOver, string ReasonCode,
    Guid? OperationId = null, DeploymentMaintenanceLease? Lease = null);

/// <summary>
/// Instantiate this coordinator in a separate process. It owns exclusion and consumes one verified handoff;
/// it exposes no production, package-switch, install or restore operation.
/// </summary>
public sealed class DeploymentMaintenanceCoordinator
{
    private readonly DeploymentMaintenanceCoordinatorOptions _options;
    public DeploymentMaintenanceCoordinator(DeploymentMaintenanceCoordinatorOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<DeploymentMaintenanceTakeoverResult> TakeoverAsync(CancellationToken cancellationToken = default)
    {
        StationDeploymentLock? ownership = null;
        FileStream? databaseLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Await the bounded acquisition itself; cancellation never leaves an unobserved future lock owner.
            ownership = await Task.Run(() => StationDeploymentLock.Acquire(_options.Installation,
                StationDeploymentOwner.Coordinator, _options.LockTimeout)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = StationMaintenanceHandoffStore.Read(ownership);
            if (!StoragePathValidator.TryValidate(_options.Store, out var databasePath, out var pathReason))
                throw new InvalidOperationException(pathReason);
            databaseLease = new FileStream(databasePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            var verified = await Task.Run(() => StationMaintenanceHandoffVerifier.ValidatePreparedHandoff(_options.Store,
                artifact, _options.TrustedAuditPublicKeyBase64, DateTimeOffset.UtcNow)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!verified.Verified || verified.Projection is not { } handoff)
                throw new InvalidOperationException(verified.ReasonCode);
            if (!OriginalProcessExited(handoff.ProcessId, handoff.ProcessStartedUtc))
                throw new InvalidOperationException("StationMaintenanceSourceProcessStillAlive");
            if (DateTimeOffset.UtcNow > handoff.PreparedAtUtc + _options.Store.StationMaintenance!.Policy.HostExitTimeout)
                throw new InvalidOperationException("StationMaintenanceHostExitDeadlineExceeded");
            // Re-read the same protected bytes immediately before the durable one-time consume.
            if (!artifact.AsSpan().SequenceEqual(StationMaintenanceHandoffStore.Read(ownership)))
                throw new InvalidOperationException("StationMaintenanceHandoffChanged");
            StationMaintenanceHandoffStore.Consume(ownership, handoff.OperationId, artifact);
            var lease = new DeploymentMaintenanceLease(ownership, databaseLease, handoff.OperationId);
            ownership = null; databaseLease = null;
            return new(true, "StationMaintenanceTakenOver", handoff.OperationId, lease);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            var reason = failure is OperationCanceledException ? "StationMaintenanceTakeoverCancelled" :
                failure is InvalidOperationException && IsReason(failure.Message) ? failure.Message :
                failure is IOException ? "StationMaintenanceExclusiveStoreUnavailable" : "StationMaintenanceTakeoverUnavailable";
            return new(false, reason);
        }
        finally { databaseLease?.Dispose(); ownership?.Dispose(); }
    }

    private static bool OriginalProcessExited(int processId, DateTimeOffset processStartedUtc)
    {
        if (processId <= 0 || processStartedUtc.Offset != TimeSpan.Zero) return false;
        try
        {
            using var original = Process.GetProcessById(processId);
            return original.HasExited || new DateTimeOffset(original.StartTime.ToUniversalTime()) != processStartedUtc;
        }
        catch (ArgumentException) { return true; } // No such PID. Access denial or unknown identity is never exit evidence.
    }

    private static bool IsReason(string reason) => reason.Length is > 0 and <= 128 &&
        reason.All(character => character <= 127 && (char.IsLetterOrDigit(character) || character is '.' or '_' or '-'));
}

/// <summary>Held by the independent process after durable consume. Disposal releases exclusion and grants no production authority.</summary>
public sealed class DeploymentMaintenanceLease : IDisposable
{
    private readonly StationDeploymentLock _ownership;
    private FileStream? _databaseLease;
    internal DeploymentMaintenanceLease(StationDeploymentLock ownership, FileStream databaseLease, Guid operationId)
    { _ownership = ownership; _databaseLease = databaseLease; OperationId = operationId; }
    public Guid OperationId { get; }
    public bool IsHeld => _ownership.IsHeld && Volatile.Read(ref _databaseLease) is not null;
    public bool ProductionAuthority => false;
    public void Dispose()
    { Interlocked.Exchange(ref _databaseLease, null)?.Dispose(); _ownership.Dispose(); }
}
