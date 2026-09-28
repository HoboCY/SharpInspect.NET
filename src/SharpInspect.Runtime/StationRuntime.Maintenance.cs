using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Deployment;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IStationMaintenanceQuery
{
    private ProductionStoreOptions? _maintenanceOptions;
    private IStationHostShutdown? _hostShutdown;
    private StationMaintenanceSnapshot? _maintenanceSnapshot;
    private bool _maintenanceAuthorizing;
    private TaskCompletionSource<bool>? _maintenanceAdmissionRetired;
    private bool _maintenanceInhibit;
    private Guid _maintenanceStopCorrelation;
    private Task? _maintenanceWork;

    internal void ConfigureStationMaintenance(ProductionStoreOptions options, IStationHostShutdown? hostShutdown)
    {
        lock (_sync)
        {
            _maintenanceOptions = options;
            _hostShutdown = hostShutdown;
            _maintenanceSnapshot = new(options.StationMaintenance is not null, _snapshot.RuntimeEpoch, null,
                options.StationMaintenance is null ? StationMaintenancePhase.Unavailable : StationMaintenancePhase.Idle,
                options.StationMaintenance is null ? "StationMaintenanceConfigurationRequired" : "StationMaintenanceIdle", null, false);
        }
    }

    public ValueTask<StationMaintenanceSnapshot> ReadMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return ValueTask.FromResult(_maintenanceSnapshot ?? new(false, _snapshot.RuntimeEpoch, null,
            StationMaintenancePhase.Unavailable, "StationMaintenanceConfigurationRequired", null, false));
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitStationMaintenanceAsync(PrepareStationMaintenanceCommand command,
        CancellationToken token)
    {
        RuntimeCommandOutcome Reject(string reason) => new(command.CorrelationId, CommandDisposition.Rejected,
            reason, AuditPersistence.NotAttempted, Guid.NewGuid());
        SqliteCommandStore store;
        StationDeploymentLock ownership;
        Guid epoch;
        RuntimeCommandOutcome? acceptedOutcome = null;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return Reject("RuntimeStopped");
            if (_maintenanceAuthorizing || _maintenanceInhibit) return Reject("StationMaintenanceAlreadyPending");
            if (_maintenanceOptions?.StationMaintenance is null || _audit is not SqliteCommandStore configured || _authorization is null)
                return Reject("StationMaintenanceConfigurationRequired");
            if (configured.DeploymentOwnership is not { IsHeld: true, Owner: StationDeploymentOwner.Runtime } held)
                return Reject("StationDeploymentOwnershipRequired");
            if (_hostShutdown is null) return Reject("StationHostShutdownRequired");
            if (command.InstallationBindingHash != held.Options.BindingHash) return Reject("StationMaintenanceInstallationMismatch");
            store = configured; ownership = held; epoch = _snapshot.RuntimeEpoch;
            _maintenanceAuthorizing = true;
            _maintenanceAdmissionRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _maintenanceSnapshot = new(true, epoch, command.OperationId, StationMaintenancePhase.Authorizing,
                "StationMaintenanceAuthorizing", null, false);
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            var sourceHash = await Task.Run(CurrentHostApplicationHash, token).ConfigureAwait(false);
            var admitted = await _authorization!.AuthorizeStationMaintenanceAsync(command, epoch, process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                sourceHash == _maintenanceOptions!.StationMaintenance!.RuntimeApplicationHash ? null : "StationMaintenanceApplicationMismatch",
                new StoreDeadline(store.CommitTimeout), token).ConfigureAwait(false);
            if (admitted.Outcome.Disposition != CommandDisposition.Accepted)
            {
                lock (_sync) _maintenanceSnapshot = new(true, epoch, command.OperationId, StationMaintenancePhase.Idle,
                    admitted.Outcome.ReasonCode, null, false);
                return admitted.Outcome;
            }
            acceptedOutcome = admitted.Outcome;
            lock (_sync)
            {
                // Once admitted this host can never reopen production, even after a failed or cancelled handoff.
                _maintenanceInhibit = true;
                ownership.RetainUntilProcessExit();
                _maintenanceStopCorrelation = Guid.NewGuid();
                _maintenanceSnapshot = new(true, epoch, command.OperationId, StationMaintenancePhase.Draining,
                    "StationMaintenanceGracefulStop", admitted.ObservedAtUtc + _maintenanceOptions!.StationMaintenance!.Policy.HandoffLifetime, false);
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
                var witness = CaptureMaintenanceRetirementLocked();
                _maintenanceWork = Task.Run(() => RunStationMaintenanceAsync(command, admitted.Authority!, ownership, store, witness));
            }
            return admitted.Outcome;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            lock (_sync)
            {
                if (acceptedOutcome is not null)
                {
                    _maintenanceInhibit = true;
                    ownership.RetainUntilProcessExit();
                    _snapshot = _snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed };
                }
                _maintenanceSnapshot = new(true, epoch, command.OperationId,
                    acceptedOutcome is null ? StationMaintenancePhase.Idle : StationMaintenancePhase.SafeMaintenance,
                    "StationMaintenanceAdmissionUnavailable", null, false);
            }
            if (acceptedOutcome is not null)
            {
                try { StationMaintenanceHandoffStore.RecordFailure(ownership, command.OperationId, "StationMaintenanceAdmissionInterrupted"); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                try { await store.RecordMaintenanceFailureAsync(command.OperationId, "StationMaintenanceAdmissionInterrupted", CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                return acceptedOutcome;
            }
            return Reject("StationMaintenanceAdmissionUnavailable");
        }
        finally
        {
            lock (_sync)
            {
                _maintenanceAuthorizing = false;
                _maintenanceAdmissionRetired!.TrySetResult(true);
            }
        }
    }

    private async Task RunStationMaintenanceAsync(PrepareStationMaintenanceCommand command,
        StationMaintenanceAuthority authority, StationDeploymentLock ownership, SqliteCommandStore store,
        MaintenanceRetirementWitness witness)
    {
        var policy = _maintenanceOptions!.StationMaintenance!.Policy;
        using var retirement = new CancellationTokenSource(policy.RetirementTimeout);
        var token = retirement.Token;
        try
        {
            var stop = await SubmitAsync(new GracefulProductionStopCommand(_maintenanceStopCorrelation,
                new(CommandSource.PhysicalConsole)), token).ConfigureAwait(false);
            if (stop.Disposition != CommandDisposition.Accepted && stop.ReasonCode != "AlreadyLocallyDisarmed")
                throw new InvalidOperationException("StationMaintenanceGracefulStopRejected");
            if (stop.Disposition == CommandDisposition.Accepted)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        if (_auditFault || _disposed || _shutdownRequested)
                            throw new InvalidOperationException("StationMaintenanceGracefulStopInterrupted");
                        if (_snapshot.LastCommand is { } progress && progress.CorrelationId == _maintenanceStopCorrelation)
                        {
                            if (progress.State == OperationState.Failed) throw new InvalidOperationException("StationMaintenanceGracefulStopFailed");
                            if (progress.State == OperationState.Completed) break;
                        }
                    }
                    await Task.Delay(10, token).ConfigureAwait(false);
                }
            }
            lock (_sync)
            {
                if (_snapshot.Busy || _snapshot.CurrentExecution is not null ||
                    _snapshot.Handshake is not HandshakePhase.Idle || _snapshot.Ready)
                    throw new InvalidOperationException("StationMaintenanceProductionNotQuiescent");
                _maintenanceSnapshot = _maintenanceSnapshot! with { Phase = StationMaintenancePhase.Retiring,
                    ReasonCode = "StationMaintenanceRetiringOwners" };
            }
            // Dispose initiates bounded retirement; only the independent witness proves actual owner exit.
            await DisposeAsync().AsTask().WaitAsync(token).ConfigureAwait(false);
            await witness.WaitAsync(token).ConfigureAwait(false);
            if (!await _authorization!.CheckStationMaintenanceAuthorityAsync(authority, token).ConfigureAwait(false))
                throw new InvalidOperationException("StationMaintenanceAuthorityRevoked");
            var sealedHandoff = await store.SealStationMaintenanceAsync(authority, token).ConfigureAwait(false);
            if (!sealedHandoff.Prepared || sealedHandoff.HandoffBytes is null)
                throw new InvalidOperationException(sealedHandoff.ReasonCode);
            token.ThrowIfCancellationRequested();
            StationMaintenanceHandoffStore.Publish(ownership, command.OperationId, sealedHandoff.HandoffBytes);
            lock (_sync) _maintenanceSnapshot = _maintenanceSnapshot! with
            {
                Phase = StationMaintenancePhase.PreparedAwaitingHostExit,
                ReasonCode = "StationMaintenancePreparedAwaitingHostExit", HostShutdownRequested = true
            };
            // The host callback is an acknowledgement of a request only. The coordinator checks OS process identity.
            using var exit = new CancellationTokenSource(policy.HostExitTimeout);
            await _hostShutdown!.RequestShutdownAsync(command.OperationId, exit.Token).AsTask().WaitAsync(exit.Token).ConfigureAwait(false);
            // If this code still runs at the deadline, the original process has not exited.
            await Task.Delay(Timeout.InfiniteTimeSpan, exit.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            var reason = failure is OperationCanceledException or TimeoutException ?
                (_maintenanceSnapshot?.HostShutdownRequested == true ? "StationMaintenanceHostExitTimeout" : "StationMaintenanceRetirementTimeout") :
                failure is InvalidOperationException && IsMaintenanceReason(failure.Message) ? failure.Message : "StationMaintenanceFailed";
            lock (_sync)
            {
                _maintenanceSnapshot = _maintenanceSnapshot! with { Phase = StationMaintenancePhase.SafeMaintenance, ReasonCode = reason };
                _snapshot = _snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers.Append("StationMaintenanceRequired").Distinct()) };
            }
            try { StationMaintenanceHandoffStore.RecordFailure(ownership, command.OperationId, reason); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            // An unavailable writer or an already sealed handoff remains an explicit failure; do not manufacture completion.
            try { await store.RecordMaintenanceFailureAsync(command.OperationId, reason, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    private static bool IsMaintenanceReason(string reason) => reason.Length is > 0 and <= 128 &&
        reason.All(character => character <= 127 && (char.IsLetterOrDigit(character) || character is '.' or '_' or '-'));

    internal static string CurrentHostApplicationHash()
    {
        var location = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(location)) throw new InvalidOperationException("StationMaintenanceApplicationIdentityUnavailable");
        using var source = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(source));
    }
}
