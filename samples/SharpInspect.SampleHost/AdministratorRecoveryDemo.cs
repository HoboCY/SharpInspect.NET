using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// A development consumer for the public administrator-recovery boundary.  It deliberately
/// exercises the closed path only: a consumer cannot assert that the station is physically
/// stopped, and it never receives or prints a recovery secret.
/// </summary>
internal static class AdministratorRecoveryDemo
{
    private const string ForgedRecoveryCode = "consumer-forged-recovery-code";
    private const string ForgedPassword = "consumer-forged-password";
    private const string ForgedConfirmationCode = "consumer-forged-confirmation-code";

    internal static int Run(ProductionStoreOptions options)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.LocalIdentity is null)
                throw new InvalidOperationException("RecoveryIdentityConfigurationMissing");

            ExecuteAsync(options).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Keep the consumer failure channel free of request fields, secrets and exception
            // messages.  The reason codes emitted by the service are safe bounded labels.
            Console.Error.WriteLine($"V108-P01 administrator-recovery-consumer FAIL reason={SafeReason(exception)}");
            return 1;
        }
    }

    private static async Task ExecuteAsync(ProductionStoreOptions options)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(500));
        await using var provider = services.BuildServiceProvider();

        var recovery = provider.GetService<ILocalAdministratorRecovery>()
            ?? throw new InvalidOperationException("RecoveryServiceUnavailable");
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var before = await WaitForRuntimeAsync(runtime, options);
        var initial = await recovery.GetRecoveryStatusAsync();

        Require(!before.Ready, "RecoveryConsumerReadyMustBeFalse");
        Require(before.Handshake == HandshakePhase.Unknown && before.Recovery == RecoveryState.Required,
            "RecoveryConsumerMustObserveUnverifiedStopState");
        Require(!initial.RecoveryAvailable, "RecoveryConsumerMustRemainClosed");
        Require(initial.ReasonCode == "SafetyStopUnverified", "RecoveryConsumerMustUseClosedStopReason");
        RequireSafeReason(initial.ReasonCode);

        // These are intentionally forged, non-secret inputs.  The recovery service must reject
        // them at its runtime/session/physical-stop boundary before any identity or kit change.
        var recoveryAttempt = await recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), initial.StationId, ForgedRecoveryCode, "consumer-forged-user",
            "Consumer Forged User", ForgedPassword));
        Require(!recoveryAttempt.Succeeded && recoveryAttempt.Identity is null,
            "ForgedRecoveryMustNotCreateIdentity");
        RequireSafeReason(recoveryAttempt.ReasonCode);

        var forgedInvocation = new CommandInvocation(CommandSource.PhysicalConsole,
            Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var rotationAttempt = await recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            Guid.NewGuid(), initial.StationId, forgedInvocation, ForgedPassword));
        rotationAttempt.RecoveryKit?.Dispose();
        Require(!rotationAttempt.Succeeded && rotationAttempt.RecoveryKit is null,
            "ForgedRecoveryMustNotReturnKit");
        RequireSafeReason(rotationAttempt.ReasonCode);

        var custodyAttempt = await recovery.ConfirmRecoveryKitCustodyAsync(
            new ConfirmRecoveryKitCustodyRequest(Guid.NewGuid(), initial.StationId, Guid.NewGuid(),
                forgedInvocation, ForgedConfirmationCode));
        Require(!custodyAttempt.Succeeded && custodyAttempt.Identity is null,
            "ForgedCustodyMustNotCreateIdentity");
        RequireSafeReason(custodyAttempt.ReasonCode);

        var after = await recovery.GetRecoveryStatusAsync();
        var finalSnapshot = await runtime.GetSnapshotAsync();
        Require(!finalSnapshot.Ready, "RecoveryConsumerFinalReadyMustBeFalse");
        Require(!after.RecoveryAvailable, "RecoveryConsumerFinalMustRemainClosed");
        Require(after.ReasonCode == "SafetyStopUnverified", "RecoveryConsumerFinalMustUseClosedStopReason");
        Require(after.UsableAdministratorCount == initial.UsableAdministratorCount &&
            after.ValidRecoveryCodeCount == initial.ValidRecoveryCodeCount &&
            after.KitState == initial.KitState && after.KitId == initial.KitId &&
            after.RecoveredPrincipalId == initial.RecoveredPrincipalId &&
            after.ProductionIdentityPrerequisitesMet == initial.ProductionIdentityPrerequisitesMet,
            "ForgedRecoveryChangedIdentityState");
        RequireSafeReason(after.ReasonCode);

        Console.WriteLine($"V108-P01 administrator-recovery-consumer PASS ready=false " +
            $"recoveryAvailable=false reason={after.ReasonCode} " +
            $"administrators={after.UsableAdministratorCount} validCodes={after.ValidRecoveryCodeCount} " +
            "kitDelivered=false physicalStop=NotRun");
    }

    private static async Task<StationStateSnapshot> WaitForRuntimeAsync(
        IStationRuntime runtime, ProductionStoreOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token);
            if (options.AuditIntegrityPolicy is null || snapshot.AuditIntegrity is
                { State: AuditIntegrityState.Verified })
                return snapshot;
            if (snapshot.AuditIntegrity is { State: AuditIntegrityState.Faulted })
                throw new InvalidOperationException("RecoveryAuditUnavailable");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void RequireSafeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 ||
            reason.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidOperationException("RecoveryReasonCodeInvalid");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static string SafeReason(Exception exception) => exception.GetType().Name;
}
