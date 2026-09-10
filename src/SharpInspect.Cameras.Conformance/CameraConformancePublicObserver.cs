using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.Cameras.Conformance;

/// <summary>
/// Executes the core camera conformance cases through the public provider,
/// device, Runtime and fixture contracts.  It is deliberately a consumer of
/// observations: no provider diagnostics, simulator type or internal Runtime
/// seam is used here.
/// </summary>
internal static class CameraConformancePublicObserver
{
    private const int CoreCaseCount = 12;
    private const int MaximumEvidenceBytes = 32 * 1024;
    private static readonly TimeSpan PublicOperationBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyObservationBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecoveryDriveBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryTick = TimeSpan.FromMilliseconds(1);
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        WriteIndented = false
    };

    internal static Task<ConformanceObservation> ObserveAsync(CameraConformanceCase kind,
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration frozenDeclaration,
        CancellationToken cancellationToken) =>
        ObserveCoreAsync(kind, factory, frozenDeclaration, cancellationToken);

    private static async Task<ConformanceObservation> ObserveCoreAsync(
        CameraConformanceCase kind, ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration frozenDeclaration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(frozenDeclaration);

        var context = new ObservationContext(kind, frozenDeclaration);
        ObservationDecision decision;
        try
        {
            ValidateDeclaration(factory, frozenDeclaration);
            if (!Enum.IsDefined(typeof(CameraConformanceCase), kind))
                throw new BlockedObservationException("CameraConformanceCaseUnsupported");
            if ((int)kind >= CoreCaseCount)
                throw new BlockedObservationException("IndependentPublicInstrumentationRequired");

            await ExecuteCaseAsync(kind, factory, frozenDeclaration, context,
                cancellationToken).ConfigureAwait(false);
            decision = ObservationDecision.Satisfied;
        }
        catch (BlockedObservationException exception)
        {
            decision = ObservationDecision.Blocked(exception.ReasonCode);
        }
        catch (ProductMismatchException exception)
        {
            decision = ObservationDecision.Mismatch(exception.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            decision = ObservationDecision.Blocked("CameraConformanceCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Exception text is deliberately discarded at this public evidence
            // boundary.  The consumer receives only a stable classification.
            decision = ObservationDecision.Blocked("CameraConformanceUnavailable");
        }

        var cleanupSafe = await context.CleanupAsync().ConfigureAwait(false);
        if (!cleanupSafe && !decision.IsBlocked)
            decision = ObservationDecision.Blocked("CameraConformanceCleanupFailed");

        return context.CreateObservation(decision);
    }

    private static void ValidateDeclaration(ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration frozenDeclaration)
    {
        CameraConformanceFixtureDeclaration? actual;
        try { actual = factory.Declaration; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new BlockedObservationException("CameraConformanceDeclarationUnavailable"); }

        if (actual is null || !StringComparer.Ordinal.Equals(actual.ContentHash,
                frozenDeclaration.ContentHash))
            throw new BlockedObservationException("CameraConformanceDeclarationChanged");
    }

    private static async Task ExecuteCaseAsync(CameraConformanceCase kind,
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case CameraConformanceCase.DiscoveryIdentity:
                await ObserveDiscoveryIdentityAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.ConfigurationReadBack:
                await ObserveConfigurationReadBackAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.ConfigurationFailure:
                await ObserveConfigurationFailureAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.SoftwareAcquisition:
                await ObserveSingleAcquisitionAsync(factory, declaration, context,
                    hardware: false, cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.HardwareAcquisition:
                await ObserveSingleAcquisitionAsync(factory, declaration, context,
                    hardware: true, cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.CanonicalMemory:
                await ObserveCanonicalMemoryAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.Cancellation:
                await ObserveCancellationAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.ExtraFrame:
                await ObserveExtraFrameAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.Timeout:
                await ObserveTimeoutAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.DisconnectRecovery:
                await ObserveDisconnectRecoveryAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.LeaseLifetime:
                await ObserveLeaseLifetimeAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            case CameraConformanceCase.PoolExhaustion:
                await ObservePoolExhaustionAsync(factory, declaration, context,
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new BlockedObservationException("CameraConformanceCaseUnsupported");
        }
    }

    private static async Task ObserveDiscoveryIdentityAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration, CameraConformanceCase.DiscoveryIdentity);
        var session = await CreateSessionAsync(factory, declaration, context,
            CameraConformanceCase.DiscoveryIdentity, configuration, 0,
            cancellationToken).ConfigureAwait(false);
        await OpenAsync(session, declaration, requireControlled: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveConfigurationReadBackAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        for (var index = 0; index < declaration.Configurations.Count; index++)
        {
            var configuration = declaration.Configurations[index];
            var session = await CreateSessionAsync(factory, declaration, context,
                CameraConformanceCase.ConfigurationReadBack, configuration, 0,
                cancellationToken).ConfigureAwait(false);
            await OpenAsync(session, declaration, requireControlled: false,
                cancellationToken).ConfigureAwait(false);
            await ApplyConfigurationAsync(session, configuration, context, index,
                cancellationToken).ConfigureAwait(false);

            var health = await ReadHealthAsync(session, context, "readback", cancellationToken)
                .ConfigureAwait(false);
            if (health.Connection != CameraConnectionState.Open ||
                health.Configuration != CameraConfigurationState.Applied ||
                health.Acquisition != CameraAcquisitionState.Stopped)
                throw new ProductMismatchException("CameraConfigurationHealthMismatch");

            // The fixture factory is single-owner.  Retire each configuration
            // before asking it for the next one.
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveConfigurationFailureAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.ConfigurationFailure);
        var index = ConfigurationIndex(declaration, configuration);
        var session = await CreateSessionAsync(factory, declaration, context,
            CameraConformanceCase.ConfigurationFailure, configuration, 0,
            cancellationToken).ConfigureAwait(false);
        await OpenAsync(session, declaration, requireControlled: false,
            cancellationToken).ConfigureAwait(false);

        var expected = ValidateExpectedConfiguration(session, configuration, context, index);
        if (!expected.Succeeded || expected.Effective is null)
            throw new ProductMismatchException("CameraConfigurationUnsupported");
        var applied = await ApplyRawConfigurationAsync(session, configuration,
            context, index, cancellationToken).ConfigureAwait(false);
        var readBack = ValidateReadBack(session, configuration, applied, context, index);
        context.Add("configurationFailure", new ConfigurationFact(index,
            ConfigurationHash(configuration), expected.Succeeded, applied.Succeeded,
            applied.Effective is not null, readBack.Succeeded, applied.ReasonCode));

        if (applied.Succeeded || applied.Effective is not null || readBack.Succeeded ||
            readBack.Effective is not null)
            throw new ProductMismatchException("CameraConfigurationFailureAccepted");

        var health = await ReadHealthAsync(session, context, "failure", cancellationToken)
            .ConfigureAwait(false);
        var safeUnknown = health.Connection == CameraConnectionState.Closed &&
            health.Configuration == CameraConfigurationState.Unknown &&
            health.Acquisition != CameraAcquisitionState.Armed;
        context.Add("configurationFailureHealth", HealthFact.From(health));
        if (!safeUnknown)
            throw new ProductMismatchException("CameraConfigurationFailureArmed");
    }

    private static async Task ObserveSingleAcquisitionAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, bool hardware,
        CancellationToken cancellationToken)
    {
        var kind = hardware ? CameraConformanceCase.HardwareAcquisition :
            CameraConformanceCase.SoftwareAcquisition;
        var configuration = SelectConfiguration(declaration, kind);
        if (hardware && configuration.ProductionAcquisitionMode !=
            ProductionAcquisitionMode.HardwareTrigger)
            throw new BlockedObservationException("HardwareTriggerConfigurationRequired");

        var index = ConfigurationIndex(declaration, configuration);
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            kind, configuration, 1, index, cancellationToken).ConfigureAwait(false);
        var acquisition = await AcquireAndStimulateAsync(session, context,
            hardware, CameraConformanceStimulusKind.AdvanceOrWait,
            declaration.FrameDelay, CancellationToken.None, cancellationToken)
            .ConfigureAwait(false);
        if (!acquisition.Attempt.Accepted || acquisition.Lease is null ||
            acquisition.Attempt.Outcome is null || !acquisition.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException(hardware
                ? "HardwareAcquisitionFailed" : "SoftwareAcquisitionFailed");

        var frame = InspectFrame(session, acquisition.Attempt, acquisition.Lease,
            declaration.CanonicalFrameHashes[index], context, "acquisition");
        if (!frame.ContractSatisfied)
            throw new ProductMismatchException("CameraFrameContractMismatch");
    }

    private static async Task ObserveCanonicalMemoryAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        for (var index = 0; index < declaration.Configurations.Count; index++)
        {
            var configuration = declaration.Configurations[index];
            var session = await PrepareStartedSessionAsync(factory, declaration, context,
                CameraConformanceCase.CanonicalMemory, configuration, 1, index,
                cancellationToken).ConfigureAwait(false);
            var hardware = configuration.ProductionAcquisitionMode ==
                ProductionAcquisitionMode.HardwareTrigger;
            var acquisition = await AcquireAndStimulateAsync(session, context, hardware,
                CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
                CancellationToken.None, cancellationToken).ConfigureAwait(false);
            if (!acquisition.Attempt.Accepted || acquisition.Lease is null ||
                acquisition.Attempt.Outcome is null || !acquisition.Attempt.Outcome.Succeeded)
                throw new ProductMismatchException("CanonicalFrameAcquisitionFailed");

            var frame = InspectFrame(session, acquisition.Attempt, acquisition.Lease,
                declaration.CanonicalFrameHashes[index], context, "canonical");
            context.Append("canonicalFrames", frame);
            if (!frame.ContractSatisfied)
                throw new ProductMismatchException("CanonicalFrameMismatch");
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveCancellationAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.Cancellation);
        var index = ConfigurationIndex(declaration, configuration);
        if (declaration.CanonicalFrameHashes[index] == declaration.ChallengeFrameHashes[index])
            throw new BlockedObservationException("CameraDistinctRequestChallengeRequired");
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.Cancellation, configuration, 2, index,
            cancellationToken).ConfigureAwait(false);

        using var acquisitionCancellation = new CancellationTokenSource();
        var firstTask = StartAcquisition(session, acquisitionCancellation.Token);
        var busy = await WaitForBusyAsync(session.Acquisition!, firstTask,
            cancellationToken).ConfigureAwait(false);
        if (busy is null)
            throw new BlockedObservationException("CameraBusyUnavailableForCancellation");

        acquisitionCancellation.Cancel();
        var first = await AwaitBoundedAsync(firstTask, cancellationToken,
            "CameraCancellationObservationTimeout", "CameraCancellationOperationFailed")
            .ConfigureAwait(false);
        var firstLease = TakeLease(session, first, context, "cancellation");
        context.Add("cancellation", new OutcomeFact(first.Accepted,
            first.Correlation is not null, first.ReasonCode,
            first.Outcome?.ExecutionStatus.ToString(), first.Outcome?.FailureKind?.ToString(),
            firstLease is not null));
        if (!first.Accepted || firstLease is not null || first.Outcome is null ||
            first.Outcome.FailureKind != CameraAcquisitionFailureKind.Cancelled)
            throw new ProductMismatchException("CameraCancellationNotHonoured");

        // Advance after cancellation so the cancelled request's late signal is
        // exposed through the public protocol ring.
        await DeliverStimulusAsync(session, context,
            new CameraConformanceStimulus(CameraConformanceStimulusKind.AdvanceOrWait,
                declaration.FrameDelay), cancellationToken).ConfigureAwait(false);
        var late = await RefreshProtocolAsync(session, context, cancellationToken)
            .ConfigureAwait(false);
        var lateObserved = late.Observations.Any(value =>
            value.Kind == CameraProtocolViolationKind.LateFrame &&
            Equals(value.Correlation, first.Correlation) && value.DroppedFrames > 0);
        context.Add("cancellationLateFrameObserved", lateObserved);
        if (!lateObserved)
            throw new BlockedObservationException("CameraCancellationLateFrameUnavailable");

        // A bounded cancellation result and its late-frame observation do not
        // retire the provider call or its cancellation callbacks. The public Busy
        // projection keeps that owner visible as CleanupPending until it exits.
        // Do not submit the reuse request while it still owns the device.
        var retirementStarted = Stopwatch.GetTimestamp();
        while (session.Acquisition!.Busy is not null)
        {
            if (Stopwatch.GetTimestamp() - retirementStarted >=
                PublicOperationBudget.TotalSeconds * Stopwatch.Frequency)
                throw new BlockedObservationException("CameraCancellationQuiescenceUnavailable");
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        context.Add("cancellationAttemptQuiescedBeforeReuse", true);

        var second = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        if (!second.Attempt.Accepted || second.Lease is null ||
            second.Attempt.Outcome is null || !second.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraCancellationReuseFailed");
        if (Equals(first.Correlation, second.Attempt.Correlation))
            throw new ProductMismatchException("CameraCancellationCorrelationReused");

        var frame = InspectFrame(session, second.Attempt, second.Lease,
            declaration.ChallengeFrameHashes[index], context, "cancellation-reuse");
        if (!frame.ContractSatisfied)
            throw new ProductMismatchException("CameraCancellationFrameMismatch");
    }

    private static async Task ObserveExtraFrameAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.ExtraFrame);
        var index = ConfigurationIndex(declaration, configuration);
        if (declaration.CanonicalFrameHashes[index] == declaration.ChallengeFrameHashes[index])
            throw new BlockedObservationException("CameraDistinctRequestChallengeRequired");
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.ExtraFrame, configuration, 2, index,
            cancellationToken).ConfigureAwait(false);

        var first = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.EmitBurst, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        if (!first.Attempt.Accepted || first.Lease is null || first.Attempt.Outcome is null ||
            !first.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraExtraFrameFirstAcquisitionFailed");
        var firstFrame = InspectFrame(session, first.Attempt, first.Lease,
            declaration.CanonicalFrameHashes[index], context, "extra-first");
        if (!firstFrame.ContractSatisfied)
            throw new ProductMismatchException("CameraExtraFrameFirstFrameMismatch");

        var protocol = await RefreshProtocolAsync(session, context, cancellationToken)
            .ConfigureAwait(false);
        var extraObserved = protocol.Observations.Any(value =>
            (value.Kind == CameraProtocolViolationKind.ExtraFrame ||
                value.Kind == CameraProtocolViolationKind.CorrelationMismatch) &&
            Equals(value.Correlation, first.Correlation) && value.DroppedFrames > 0);
        context.Add("extraFrameObserved", extraObserved);
        if (protocol.Overflowed)
            throw new ProductMismatchException("CameraExtraFrameProtocolGap");
        if (!extraObserved)
            throw new ProductMismatchException("CameraExtraFrameNotObserved");

        var second = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken, allowRejected: true)
            .ConfigureAwait(false);
        if (!second.Attempt.Accepted)
        {
            if (second.Lease is not null || second.Attempt.Correlation is not null ||
                second.Attempt.Outcome is not null || !session.Acquisition!.ProtocolFaulted)
                throw new ProductMismatchException("CameraExtraFrameRejectedWithoutFault");
            context.Add("extraSecond", new OutcomeFact(false, false,
                second.Attempt.ReasonCode, null, null, false));
            return;
        }

        if (Equals(first.Correlation, second.Attempt.Correlation))
            throw new ProductMismatchException("CameraExtraFrameCorrelationReused");

        if (second.Attempt.Outcome is null)
            throw new ProductMismatchException("CameraExtraFrameSecondAcquisitionFailed");
        if (!second.Attempt.Outcome.Succeeded)
        {
            if (second.Lease is not null || second.Attempt.Outcome.FailureKind is not
                (CameraAcquisitionFailureKind.ProtocolViolation or
                 CameraAcquisitionFailureKind.Disconnected))
                throw new ProductMismatchException("CameraExtraFrameSecondAcquisitionFailed");
            return;
        }
        if (second.Lease is null)
            throw new ProductMismatchException("CameraExtraFrameSecondAcquisitionFailed");

        var secondFrame = InspectFrame(session, second.Attempt, second.Lease,
            declaration.ChallengeFrameHashes[index], context, "extra-second");
        if (!secondFrame.ContractSatisfied)
            throw new ProductMismatchException("CameraExtraFrameCrossRequestAccepted");
    }

    private static async Task ObserveTimeoutAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration, CameraConformanceCase.Timeout);
        var index = ConfigurationIndex(declaration, configuration);
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.Timeout, configuration, 1, index,
            cancellationToken).ConfigureAwait(false);
        var task = StartAcquisition(session, CancellationToken.None);
        var busy = await WaitForBusyAsync(session.Acquisition!, task, cancellationToken)
            .ConfigureAwait(false);
        if (busy is null)
            throw new BlockedObservationException("CameraBusyUnavailableForTimeout");

        await DeliverStimulusAsync(session, context,
            new CameraConformanceStimulus(CameraConformanceStimulusKind.AdvanceOrWait,
                TimeSpan.FromMilliseconds(configuration.AcquisitionTimeoutMs)),
            cancellationToken).ConfigureAwait(false);
        var outcome = await AwaitBoundedAsync(task, cancellationToken,
            "CameraTimeoutObservationTimeout", "CameraTimeoutOperationFailed")
            .ConfigureAwait(false);
        var lease = TakeLease(session, outcome, context, "timeout");
        context.Add("timeout", new OutcomeFact(outcome.Accepted,
            outcome.Correlation is not null, outcome.ReasonCode,
            outcome.Outcome?.ExecutionStatus.ToString(), outcome.Outcome?.FailureKind?.ToString(),
            lease is not null));
        if (!outcome.Accepted || lease is not null || outcome.Outcome is null ||
            outcome.Outcome.FailureKind != CameraAcquisitionFailureKind.TimedOut)
            throw new ProductMismatchException("CameraTimeoutNotHonoured");
    }

    private static async Task ObserveDisconnectRecoveryAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.DisconnectRecovery);
        if (configuration.ProductionAcquisitionMode != ProductionAcquisitionMode.SoftwareTrigger)
            throw new BlockedObservationException("RecoverySoftwareConfigurationRequired");
        var index = ConfigurationIndex(declaration, configuration);
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.DisconnectRecovery, configuration, 2, index,
            cancellationToken).ConfigureAwait(false);

        try
        {
            session.Recovery = new CameraRecoveryService(session.Provider,
                new CameraBindingTarget(declaration.Provider, declaration.StableDeviceIdentity),
                session.Fixture.LogicalCameraRole, configuration, session.Acquisition!,
                session.Fixture.Clock, new CameraRecoveryOptions());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ProductMismatchException("CameraRecoveryConstructionFailed");
        }

        await RefreshRecoveryAsync(session, context, cancellationToken).ConfigureAwait(false);
        var initial = session.Recovery.GetSnapshot();
        context.Add("recoveryInitial", RecoveryFact.From(initial));
        if (initial.State != CameraRecoveryState.Healthy || !initial.SourceHealthy)
            throw new ProductMismatchException("CameraRecoveryInitialHealthFailed");
        var recoveryCursor = session.Recovery.ReadEvents(0).ThroughSequence;

        var disconnectedTask = StartAcquisition(session, CancellationToken.None,
            useRecovery: false);
        var busy = await WaitForBusyAsync(session.Acquisition!, disconnectedTask,
            cancellationToken).ConfigureAwait(false);
        if (busy is null)
            throw new BlockedObservationException("CameraBusyUnavailableForDisconnect");
        await DeliverStimulusAsync(session, context,
            new CameraConformanceStimulus(CameraConformanceStimulusKind.AdvanceOrWait,
                declaration.FrameDelay), cancellationToken).ConfigureAwait(false);
        var disconnected = await AwaitBoundedAsync(disconnectedTask, cancellationToken,
            "CameraDisconnectObservationTimeout", "CameraDisconnectOperationFailed")
            .ConfigureAwait(false);
        var disconnectedLease = TakeLease(session, disconnected, context, "disconnect");
        context.Add("disconnectOutcome", new OutcomeFact(disconnected.Accepted,
            disconnected.Correlation is not null, disconnected.ReasonCode,
            disconnected.Outcome?.ExecutionStatus.ToString(),
            disconnected.Outcome?.FailureKind?.ToString(), disconnectedLease is not null));
        if (!disconnected.Accepted || disconnectedLease is not null ||
            disconnected.Outcome?.FailureKind != CameraAcquisitionFailureKind.Disconnected)
            throw new ProductMismatchException("CameraDisconnectNotObserved");

        await DeliverStimulusAsync(session, context,
            new CameraConformanceStimulus(CameraConformanceStimulusKind.RestoreSameDevice,
                TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
        await RefreshRecoveryAsync(session, context, cancellationToken).ConfigureAwait(false);
        var recovered = await DriveRecoveryAsync(session, context, initial,
            cancellationToken).ConfigureAwait(false);
        if (!recovered)
            throw new ProductMismatchException("CameraRecoveryDidNotComplete");
        var recoveredSnapshot = session.Recovery.GetSnapshot();
        var recoveryEvents = session.Recovery.ReadEvents(recoveryCursor);
        context.Add("recoveryEvents", new
        {
            recoveryEvents.Overflowed,
            recoveryEvents.ThroughSequence,
            SameEpoch = recoveryEvents.RecoveryEpoch == initial.RecoveryEpoch,
            Events = recoveryEvents.Events.Select(value => new
            {
                value.Sequence,
                Kind = value.Kind.ToString(),
                value.AttemptNumber,
                SameCycle = value.CycleId == recoveredSnapshot.CycleId,
                value.Timestamp.MonotonicTimestamp,
                value.ReasonCode
            }).ToArray()
        });
        if (!HasCompleteRecoveryEvents(initial.RecoveryEpoch, recoveryCursor,
                recoveredSnapshot.CycleId, recoveryEvents))
            throw new ProductMismatchException("CameraRecoveryEventHistoryMismatch");

        var acquisition = await AcquireAndStimulateAsync(session, context,
            hardware: false, CameraConformanceStimulusKind.AdvanceOrWait,
            declaration.FrameDelay, CancellationToken.None, cancellationToken,
            useRecovery: true).ConfigureAwait(false);
        if (!acquisition.Attempt.Accepted || acquisition.Lease is null ||
            acquisition.Attempt.Outcome is null || !acquisition.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraRecoveryAcquisitionFailed");
        var frame = InspectFrame(session, acquisition.Attempt, acquisition.Lease,
            declaration.CanonicalFrameHashes[index], context, "recovery");
        if (!frame.ContractSatisfied)
            throw new ProductMismatchException("CameraRecoveryFrameMismatch");
    }

    internal static bool HasCompleteRecoveryEvents(Guid epoch, long cursor,
        Guid? cycleId, CameraRecoveryEventPage page)
    {
        var events = page.Events;
        if (page.Overflowed || page.RecoveryEpoch != epoch || cycleId is null ||
            events.Count < 4 || events[^1].Sequence != page.ThroughSequence ||
            events[0].Kind != CameraRecoveryEventKind.SourceDisconnected ||
            events[1].Kind != CameraRecoveryEventKind.CycleStarted ||
            events[^1].Kind != CameraRecoveryEventKind.CycleCompleted ||
            events[0].AttemptNumber != 0 || events[1].AttemptNumber != 0)
            return false;
        var attempt = 0;
        var pending = false;
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.Sequence != cursor + index + 1 || item.RecoveryEpoch != epoch ||
                item.CycleId != cycleId || index > 0 &&
                item.Timestamp.MonotonicTimestamp < events[index - 1].Timestamp.MonotonicTimestamp)
                return false;
            if (index < 2) continue;
            if (item.Kind == CameraRecoveryEventKind.AttemptStarted)
            {
                if (pending || item.AttemptNumber != ++attempt) return false;
                pending = true;
            }
            else if (item.Kind is CameraRecoveryEventKind.AttemptFailed or
                     CameraRecoveryEventKind.CycleCompleted)
            {
                if (!pending || item.AttemptNumber != attempt ||
                    item.Kind == CameraRecoveryEventKind.CycleCompleted && index != events.Count - 1)
                    return false;
                pending = false;
            }
            else return false;
        }
        return attempt > 0 && !pending;
    }

    private static async Task ObserveLeaseLifetimeAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.LeaseLifetime);
        var index = ConfigurationIndex(declaration, configuration);
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.LeaseLifetime, configuration, 2, index,
            cancellationToken).ConfigureAwait(false);
        if (declaration.PoolCapacity < 2 || declaration.CanonicalFrameHashes[index] == declaration.ChallengeFrameHashes[index])
            throw new BlockedObservationException("CameraLeaseDistinctChallengeRequired");
        var first = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        if (!first.Attempt.Accepted || first.Lease is null ||
            first.Attempt.Outcome is null || !first.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraLeaseAcquisitionFailed");

        var firstFrame = InspectFrame(session, first.Attempt, first.Lease,
            declaration.CanonicalFrameHashes[index], context, "lease-before-second");
        if (!firstFrame.ContractSatisfied)
            throw new ProductMismatchException("CameraLeaseFrameMismatch");

        var second = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        if (!second.Attempt.Accepted || second.Lease is null ||
            second.Attempt.Outcome is null || !second.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraLeaseSecondAcquisitionFailed");

        var secondFrame = InspectFrame(session, second.Attempt, second.Lease,
            declaration.ChallengeFrameHashes[index], context, "lease-second");
        if (!secondFrame.ContractSatisfied)
            throw new ProductMismatchException("CameraLeaseSecondFrameMismatch");

        var firstHashStable = StringComparer.Ordinal.Equals(firstFrame.Hash,
            HashFrame(first.Lease.Frame));
        context.Add("leaseLifetimeBeforeDispose", new LeaseLifetimeFact(
            false, false, firstHashStable));
        if (!firstHashStable)
            throw new ProductMismatchException("CameraLeaseOverwrittenWhileHeld");

        await ReturnLeaseAsync(session, first.Lease, context, "lease-first-return")
            .ConfigureAwait(false);
        session.HeldLeases.Remove(first.Lease);

        var accessRejected = false;
        try
        {
            _ = first.Lease.Frame.GetRowSpan(0).Length;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { accessRejected = true; }
        context.Add("leaseLifetime", new LeaseLifetimeFact(
            first.Lease.IsReturned, accessRejected, firstHashStable));
        if (!accessRejected)
            throw new ProductMismatchException("CameraLeaseAccessAfterDisposeAllowed");
    }

    private static async Task ObservePoolExhaustionAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CancellationToken cancellationToken)
    {
        if (declaration.PoolCapacity >= 64)
            throw new BlockedObservationException("PoolExhaustionAcquisitionBoundExceeded");
        var configuration = SelectConfiguration(declaration,
            CameraConformanceCase.PoolExhaustion);
        var index = ConfigurationIndex(declaration, configuration);
        var count = checked(declaration.PoolCapacity + 2);
        var session = await PrepareStartedSessionAsync(factory, declaration, context,
            CameraConformanceCase.PoolExhaustion, configuration, count, index,
            cancellationToken).ConfigureAwait(false);

        for (var indexHeld = 0; indexHeld < declaration.PoolCapacity; indexHeld++)
        {
            var acquisition = await AcquireAndStimulateAsync(session, context,
                hardware: false, CameraConformanceStimulusKind.AdvanceOrWait,
                declaration.FrameDelay, CancellationToken.None, cancellationToken)
                .ConfigureAwait(false);
            if (!acquisition.Attempt.Accepted || acquisition.Lease is null ||
                acquisition.Attempt.Outcome is null || !acquisition.Attempt.Outcome.Succeeded)
                throw new ProductMismatchException("CameraPoolCapacityAcquisitionFailed");
            var frame = InspectFrame(session, acquisition.Attempt, acquisition.Lease,
                declaration.CanonicalFrameHashes[index], context, "pool-held");
            if (!frame.ContractSatisfied)
                throw new ProductMismatchException("CameraPoolHeldFrameMismatch");
        }

        context.Add("poolLeasesHeld", session.HeldLeases.Count);
        if (session.HeldLeases.Count != declaration.PoolCapacity)
            throw new ProductMismatchException("CameraPoolLeaseCountMismatch");

        var final = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        var finalFailure = final.Attempt.Outcome?.Failure;
        var fallback = final.Lease is not null;
        context.Add("poolExhaustion", new OutcomeFact(final.Attempt.Accepted,
            final.Attempt.Correlation is not null, final.Attempt.ReasonCode,
            final.Attempt.Outcome?.ExecutionStatus.ToString(),
            finalFailure?.Kind.ToString(), fallback));
        if (!final.Attempt.Accepted || final.Lease is not null || finalFailure is null ||
            finalFailure.Kind != CameraAcquisitionFailureKind.BufferUnavailable)
            throw new ProductMismatchException("CameraPoolExhaustionNotBounded");

        var released = session.HeldLeases[0];
        await ReturnLeaseAsync(session, released, context, "pool-release")
            .ConfigureAwait(false);
        session.HeldLeases.RemoveAt(0);
        context.Add("poolReleased", released.IsReturned);

        var afterExhaustion = await ReadHealthAsync(session, context, "pool-exhausted", cancellationToken)
            .ConfigureAwait(false);
        if (!IsArmedHealthy(afterExhaustion))
        {
            // A buffer fault may close the device. Reuse must then follow the full
            // public retirement/open/configuration path, never force it back to Armed.
            context.Add("poolRecoveryMode", "RetireReopenReconfigure");
            foreach (var held in session.HeldLeases.ToArray())
                await ReturnLeaseAsync(session, held, context, "pool-retirement-return").ConfigureAwait(false);
            session.HeldLeases.Clear();
            var retirement = await session.Acquisition!.RetireAsync().ConfigureAwait(false);
            if (!retirement.SafeToReplace)
                throw new BlockedObservationException("CameraPoolRetirementUnsafe");
            session.Acquisition = null;
            session.Device = null;
            session.Controlled = null;
            session.OpenTask = null;
            await OpenAsync(session, declaration, requireControlled: true, cancellationToken).ConfigureAwait(false);
            await StartConfiguredSessionAsync(session, configuration, context, index, cancellationToken).ConfigureAwait(false);
        }
        else context.Add("poolRecoveryMode", "ExistingSession");

        var recovered = await AcquireAndStimulateAsync(session, context, hardware: false,
            CameraConformanceStimulusKind.AdvanceOrWait, declaration.FrameDelay,
            CancellationToken.None, cancellationToken).ConfigureAwait(false);
        if (!recovered.Attempt.Accepted || recovered.Lease is null ||
            recovered.Attempt.Outcome is null || !recovered.Attempt.Outcome.Succeeded)
            throw new ProductMismatchException("CameraPoolRecoveryFailed");
        var recoveredFrame = InspectFrame(session, recovered.Attempt, recovered.Lease,
            declaration.CanonicalFrameHashes[index], context, "pool-recovered");
        if (!recoveredFrame.ContractSatisfied)
            throw new ProductMismatchException("CameraPoolRecoveryFrameMismatch");
    }

    private static async Task<ObservationSession> PrepareStartedSessionAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CameraConformanceCase kind,
        RequestedCameraConfiguration configuration, int acquisitionCount,
        int configurationIndex, CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(factory, declaration, context, kind,
            configuration, acquisitionCount, cancellationToken).ConfigureAwait(false);
        await OpenAsync(session, declaration, requireControlled: true,
            cancellationToken).ConfigureAwait(false);
        await StartConfiguredSessionAsync(session, configuration, context, configurationIndex, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static async Task StartConfiguredSessionAsync(ObservationSession session,
        RequestedCameraConfiguration configuration, ObservationContext context, int configurationIndex,
        CancellationToken cancellationToken)
    {
        await ApplyConfigurationAsync(session, configuration, context,
            configurationIndex, cancellationToken).ConfigureAwait(false);

        var startedTask = session.Call(() => session.Device!.StartAsync(CancellationToken.None));
        var started = await AwaitBoundedAsync(startedTask, cancellationToken,
            "CameraStartObservationTimeout", "CameraStartOperationFailed")
            .ConfigureAwait(false);
        context.Append("starts", new OperationFact(started.Succeeded, started.ReasonCode));
        if (!started.Succeeded)
            throw new ProductMismatchException("CameraStartFailed");

        var health = await ReadHealthAsync(session, context, "started", cancellationToken)
            .ConfigureAwait(false);
        if (!IsArmedHealthy(health))
            throw new ProductMismatchException("CameraStartHealthMismatch");

        session.Acquisition = new CameraAcquisitionService(session.Controlled!,
            session.Effective!, session.Fixture.Clock,
            new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2), protocolCapacity: 256,
                protocolReadTimeout: TimeSpan.FromMilliseconds(250)));
        context.Append("services", new ServiceFact(session.Acquisition.Ready,
            session.Acquisition.Configuration.Equals(session.Effective)));
    }

    private static async Task<ObservationSession> CreateSessionAsync(
        ICameraConformanceFixtureFactory factory,
        CameraConformanceFixtureDeclaration declaration,
        ObservationContext context, CameraConformanceCase kind,
        RequestedCameraConfiguration configuration, int acquisitionCount,
        CancellationToken cancellationToken)
    {
        var request = new CameraConformanceFixtureRequest(kind, configuration,
            acquisitionCount);
        Task<ICameraConformanceFixture> createTask;
        try
        {
            createTask = context.Track(InvokeOnWorker(() => factory.CreateAsync(
                request, CancellationToken.None)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlockedObservationException("CameraConformanceFixtureUnavailable");
        }

        ICameraConformanceFixture fixture;
        try
        {
            fixture = await createTask.WaitAsync(PublicOperationBudget,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            context.TrackUnclaimedFixture(createTask);
            throw new BlockedObservationException("CameraConformanceFixtureTimeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.TrackUnclaimedFixture(createTask);
            throw new BlockedObservationException("CameraConformanceCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlockedObservationException("CameraConformanceFixtureUnavailable");
        }

        if (fixture is null)
            throw new BlockedObservationException("CameraConformanceFixtureUnavailable");
        var session = new ObservationSession(context, fixture);
        context.AddSession(session);
        try
        {
            if (!StringComparer.Ordinal.Equals(fixture.StableDeviceIdentity,
                    declaration.StableDeviceIdentity))
                throw new BlockedObservationException("FixtureStableIdentityMismatch");
            if (!Equals(fixture.Configuration, configuration))
                throw new BlockedObservationException("FixtureConfigurationMismatch");
            if (!SameProvider(fixture.Provider.Identity, declaration.Provider))
                throw new BlockedObservationException("FixtureProviderIdentityMismatch");
            if (fixture.Clock.Frequency is < 1 or > 10_000_000_000)
                throw new BlockedObservationException("FixtureClockInvalid");
        }
        catch (BlockedObservationException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new BlockedObservationException("CameraConformanceFixtureUnavailable"); }

        context.Append("configurations", new ConfigurationFact(
            ConfigurationIndex(declaration, configuration), ConfigurationHash(configuration),
            true, false, false, false, "FixtureCreated"));
        return session;
    }

    private static async Task OpenAsync(ObservationSession session,
        CameraConformanceFixtureDeclaration declaration, bool requireControlled,
        CancellationToken cancellationToken)
    {
        CameraProviderIdentity providerIdentity;
        try { providerIdentity = session.Provider.Identity; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new BlockedObservationException("CameraProviderIdentityUnavailable"); }

        var discoveryTask = session.Call(() => session.Provider.DiscoverAsync(
            CancellationToken.None));
        var discovery = await AwaitBoundedAsync(discoveryTask, cancellationToken,
            "CameraDiscoveryTimeout", "CameraDiscoveryFailed").ConfigureAwait(false);
        var exactDiscovery = discovery.Succeeded
            ? discovery.Devices.Count(value =>
                SameBinding(value, declaration.Provider, declaration.StableDeviceIdentity))
            : 0;
        session.Context.Add("discovery", new DiscoveryFact(discovery.Succeeded,
            SafeReason(discovery.ReasonCode, "CameraDiscoveryUnknown"),
            discovery.Devices.Count, exactDiscovery > 0,
            SameProvider(providerIdentity, declaration.Provider)));
        if (!discovery.Succeeded)
            throw new ProductMismatchException(SafeReason(discovery.ReasonCode,
                "CameraDiscoveryFailed"));
        if (exactDiscovery == 0)
            throw new ProductMismatchException("CameraDiscoveryBindingMismatch");

        var openTask = session.Call(() => session.Provider.OpenAsync(
            declaration.StableDeviceIdentity, CancellationToken.None));
        session.OpenTask = openTask;
        var opened = await AwaitBoundedAsync(openTask, cancellationToken,
            "CameraOpenTimeout", "CameraOpenFailed").ConfigureAwait(false);
        if (!opened.Succeeded || opened.Device is null)
            throw new ProductMismatchException(SafeReason(opened.ReasonCode,
                "CameraOpenFailed"));

        session.Device = opened.Device;
        session.Controlled = opened.Device as IControlledCameraDevice;
        var exactOpen = SameBinding(opened.Device.Descriptor, declaration.Provider,
            declaration.StableDeviceIdentity);
        session.Context.Add("open", new OpenFact(opened.Succeeded, exactOpen,
            session.Controlled is not null, opened.Device.Descriptor.ReportedModel is not null));
        if (!exactOpen)
            throw new ProductMismatchException("CameraOpenBindingMismatch");
        if (requireControlled && session.Controlled is null)
            throw new ProductMismatchException("CameraControlledSurfaceUnavailable");
    }

    private static CameraConfigurationResult ValidateExpectedConfiguration(
        ObservationSession session, RequestedCameraConfiguration configuration,
        ObservationContext context, int index)
    {
        CameraConfigurationResult expected;
        try { expected = session.Device!.Capabilities.ValidateConfiguration(configuration); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new ProductMismatchException("CameraCapabilitiesValidationFailed"); }
        context.Append("configurationValidation", new ConfigurationFact(index,
            ConfigurationHash(configuration), expected.Succeeded, false,
            expected.Effective is not null, false, expected.ReasonCode));
        return expected;
    }

    private static async Task ApplyConfigurationAsync(ObservationSession session,
        RequestedCameraConfiguration configuration, ObservationContext context,
        int index, CancellationToken cancellationToken)
    {
        var expected = ValidateExpectedConfiguration(session, configuration, context, index);
        if (!expected.Succeeded || expected.Effective is null)
            throw new ProductMismatchException(SafeReason(expected.ReasonCode,
                "CameraConfigurationUnsupported"));
        var applied = await ApplyRawConfigurationAsync(session, configuration,
            context, index, cancellationToken).ConfigureAwait(false);
        var readBack = ValidateReadBack(session, configuration, applied, context, index);
        if (!readBack.Succeeded || readBack.Effective is null)
            throw new ProductMismatchException(SafeReason(readBack.ReasonCode,
                "CameraReadBackMismatch"));
        session.Effective = readBack.Effective;
    }

    private static async Task<CameraConfigurationResult> ApplyRawConfigurationAsync(
        ObservationSession session, RequestedCameraConfiguration configuration,
        ObservationContext context, int index, CancellationToken cancellationToken)
    {
        var applyTask = session.Call(() => session.Device!.ApplyConfigurationAsync(
            configuration, CancellationToken.None));
        var applied = await AwaitBoundedAsync(applyTask, cancellationToken,
            "CameraConfigurationTimeout", "CameraConfigurationApplyFailed")
            .ConfigureAwait(false);
        context.Append("configurationApplications", new ConfigurationFact(index,
            ConfigurationHash(configuration), false, applied.Succeeded,
            applied.Effective is not null, false, applied.ReasonCode));
        return applied;
    }

    private static CameraConfigurationResult ValidateReadBack(
        ObservationSession session, RequestedCameraConfiguration configuration,
        CameraConfigurationResult reported, ObservationContext context, int index)
    {
        CameraConfigurationResult readBack;
        try
        {
            readBack = session.Device!.Capabilities.ValidateReadBack(configuration, reported);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new ProductMismatchException("CameraReadBackValidationFailed"); }
        context.Append("configurationReadBack", new ConfigurationFact(index,
            ConfigurationHash(configuration), false, reported.Succeeded,
            reported.Effective is not null, readBack.Succeeded, readBack.ReasonCode));
        return readBack;
    }

    private static async Task<CameraHealthSnapshot> ReadHealthAsync(
        ObservationSession session, ObservationContext context, string label,
        CancellationToken cancellationToken)
    {
        var healthTask = session.CallSync(() => session.Device!.GetHealthSnapshot());
        var health = await AwaitBoundedAsync(healthTask, cancellationToken,
            "CameraHealthObservationTimeout", "CameraHealthObservationFailed")
            .ConfigureAwait(false);
        context.Append("health", new HealthFact(label, health));
        return health;
    }

    private static Task<CameraAcquisitionAttempt> StartAcquisition(
        ObservationSession session, CancellationToken cancellationToken,
        bool useRecovery = false)
    {
        if (useRecovery ? session.Recovery is null : session.Acquisition is null)
            throw new BlockedObservationException("CameraAcquisitionSurfaceUnavailable");
        return useRecovery
            ? session.Call(() => session.Recovery!.AcquireAsync(
                ExecutionKind.Qualification, session.Fixture.LogicalCameraRole,
                cancellationToken))
            : session.Call(() => session.Acquisition!.AcquireAsync(
                ExecutionKind.Qualification, session.Fixture.LogicalCameraRole,
                cancellationToken));
    }

    private static async Task<AcquisitionObservation> AcquireAndStimulateAsync(
        ObservationSession session, ObservationContext context, bool hardware,
        CameraConformanceStimulusKind stimulusKind, TimeSpan elapsed,
        CancellationToken acquisitionCancellation, CancellationToken observationCancellation,
        bool useRecovery = false, bool allowRejected = false)
    {
        var task = StartAcquisition(session, acquisitionCancellation, useRecovery);
        CameraAcquisitionBusySnapshot? busy = null;
        if (!useRecovery)
        {
            busy = await WaitForBusyAsync(session.Acquisition!, task,
                observationCancellation).ConfigureAwait(false);
            if (busy is null)
            {
                if (!allowRejected)
                    throw new BlockedObservationException("CameraBusyUnavailable");

                var rejectedAttempt = await AwaitBoundedAsync(task,
                    observationCancellation, "CameraAcquisitionObservationTimeout",
                    "CameraAcquisitionOperationFailed").ConfigureAwait(false);
                var rejectedLease = TakeLease(session, rejectedAttempt, context,
                    "acquisition-rejected");
                context.Append("acquisitions", new OutcomeFact(
                    rejectedAttempt.Accepted, rejectedAttempt.Correlation is not null,
                    rejectedAttempt.ReasonCode,
                    rejectedAttempt.Outcome?.ExecutionStatus.ToString(),
                    rejectedAttempt.Outcome?.FailureKind?.ToString(),
                    rejectedLease is not null));
                return new AcquisitionObservation(rejectedAttempt, rejectedLease, null);
            }
        }

        var stimulus = new CameraConformanceStimulus(
            hardware ? CameraConformanceStimulusKind.HardwarePulse : stimulusKind,
            elapsed, hardware ? busy?.Correlation : null);
        if (useRecovery)
        {
            // Wait for the current Runtime owner's public Busy milestone before advancing time.
            await DriveRecoveryAcquisitionAsync(session, context, task, elapsed,
                stimulusKind, observationCancellation).ConfigureAwait(false);
        }
        else
        {
            await DeliverStimulusAsync(session, context, stimulus,
                observationCancellation).ConfigureAwait(false);
        }

        var attempt = await AwaitBoundedAsync(task, observationCancellation,
            "CameraAcquisitionObservationTimeout", "CameraAcquisitionOperationFailed")
            .ConfigureAwait(false);
        var lease = TakeLease(session, attempt, context, "acquisition");
        context.Append("acquisitions", new OutcomeFact(attempt.Accepted,
            attempt.Correlation is not null, attempt.ReasonCode,
            attempt.Outcome?.ExecutionStatus.ToString(),
            attempt.Outcome?.FailureKind?.ToString(), lease is not null));
        return new AcquisitionObservation(attempt, lease, busy);
    }

    private static async Task DriveRecoveryAcquisitionAsync(ObservationSession session,
        ObservationContext context, Task<CameraAcquisitionAttempt> task,
        TimeSpan elapsed, CameraConformanceStimulusKind stimulusKind,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var target = checked(started + (long)(RecoveryDriveBudget.TotalSeconds * Stopwatch.Frequency));
        while (!task.IsCompleted && Stopwatch.GetTimestamp() < target)
        {
            if (session.Recovery!.Busy is { IsBusy: true, CleanupPending: false })
                break;
            if (!task.IsCompleted)
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        if (!task.IsCompleted)
        {
            if (session.Recovery!.Busy is not { IsBusy: true, CleanupPending: false })
                throw new BlockedObservationException("CameraRecoveryAcquisitionBusy");
            await DeliverStimulusAsync(session, context,
                new CameraConformanceStimulus(stimulusKind,
                    elapsed <= TimeSpan.Zero ? RecoveryTick : elapsed), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<CameraAcquisitionBusySnapshot?> WaitForBusyAsync(
        CameraAcquisitionService service, Task<CameraAcquisitionAttempt> task,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var limit = checked(start + (long)(BusyObservationBudget.TotalSeconds *
            Stopwatch.Frequency));
        while (Stopwatch.GetTimestamp() < limit)
        {
            var busy = service.Busy;
            if (busy?.IsBusy == true)
                return busy;
            if (task.IsCompleted)
                return null;
            try { await Task.Delay(1, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new BlockedObservationException("CameraConformanceCancelled"); }
        }
        return service.Busy?.IsBusy == true ? service.Busy : null;
    }

    private static async Task DeliverStimulusAsync(ObservationSession session,
        ObservationContext context, CameraConformanceStimulus stimulus,
        CancellationToken cancellationToken)
    {
        var task = session.Call(() => session.Fixture.StimulateAsync(stimulus,
            CancellationToken.None));
        var receipt = await AwaitBoundedAsync(task, cancellationToken,
            "CameraConformanceStimulusTimeout", "CameraConformanceStimulusFailed")
            .ConfigureAwait(false);
        context.Append("stimuli", new StimulusFact(stimulus.Kind.ToString(),
            stimulus.Elapsed.Ticks, stimulus.Correlation is not null,
            receipt.Status == CameraConformanceStimulusStatus.Delivered,
            SafeReason(receipt.ReasonCode, "StimulusUnavailable"),
            receipt.ObservedAt.MonotonicTimestamp));
        context.RecordClock(session, "stimulus", receipt.ObservedAt);
        if (receipt.Status != CameraConformanceStimulusStatus.Delivered)
            throw new BlockedObservationException(SafeReason(receipt.ReasonCode,
                "CameraConformanceStimulusUnavailable"));
    }

    private static async Task<CameraProtocolSnapshot> RefreshProtocolAsync(
        ObservationSession session, ObservationContext context,
        CancellationToken cancellationToken)
    {
        if (session.Acquisition is null)
            throw new BlockedObservationException("CameraProtocolSurfaceUnavailable");
        var task = session.Call(() => session.Acquisition.RefreshProtocolObservationsAsync(
            CancellationToken.None));
        var snapshot = await AwaitBoundedAsync(task, cancellationToken,
            "CameraProtocolRefreshTimeout", "CameraProtocolRefreshFailed")
            .ConfigureAwait(false);
        context.Add("protocol", new ProtocolFact(snapshot.ThroughSequence,
            snapshot.Observations.Count, snapshot.Overflowed));
        return snapshot;
    }

    private static async Task RefreshRecoveryAsync(ObservationSession session,
        ObservationContext context, CancellationToken cancellationToken)
    {
        if (session.Recovery is null)
            throw new BlockedObservationException("CameraRecoverySurfaceUnavailable");
        var task = session.Call(() => session.Recovery.RefreshAsync(CancellationToken.None));
        await AwaitBoundedAsync(task, cancellationToken,
            "CameraRecoveryHealthTimeout", "CameraRecoveryHealthFailed")
            .ConfigureAwait(false);
        context.Append("recoveryRefreshes", RecoveryFact.From(session.Recovery.GetSnapshot()));
    }

    private static async Task<bool> DriveRecoveryAsync(ObservationSession session,
        ObservationContext context, CameraRecoverySnapshot initial,
        CancellationToken cancellationToken)
    {
        if (session.Recovery is null)
            throw new BlockedObservationException("CameraRecoverySurfaceUnavailable");
        var started = Stopwatch.GetTimestamp();
        var limit = checked(started + (long)(RecoveryDriveBudget.TotalSeconds *
            Stopwatch.Frequency));
        long? drivenTarget = null;
        while (Stopwatch.GetTimestamp() < limit)
        {
            var snapshot = session.Recovery.GetSnapshot();
            context.Add("recoveryLast", RecoveryFact.From(snapshot));
            if (snapshot.State == CameraRecoveryState.Healthy && snapshot.SourceHealthy &&
                snapshot.CycleId.HasValue && snapshot.Revision > initial.Revision)
                return true;
            if (snapshot.State == CameraRecoveryState.Exhausted)
                throw new ProductMismatchException("CameraRecoveryExhausted");

            if (snapshot.NextAttemptAt is { } next)
            {
                var now = session.Fixture.Clock.GetTimePoint();
                var delta = next.MonotonicTimestamp - now.MonotonicTimestamp;
                if (drivenTarget != next.MonotonicTimestamp)
                {
                    var elapsed = delta > 0
                        ? MonotonicDuration(delta, session.Fixture.Clock.Frequency)
                        : TimeSpan.Zero;
                    await DeliverStimulusAsync(session, context,
                        new CameraConformanceStimulus(
                            CameraConformanceStimulusKind.AdvanceOrWait, elapsed),
                        cancellationToken).ConfigureAwait(false);
                    drivenTarget = next.MonotonicTimestamp;
                    continue;
                }
            }
            // A scheduled callback queues the recovery worker.  Let that worker
            // settle without advancing the virtual clock again; otherwise the
            // number of scheduler polls would become part of frame timestamps.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static IFrameBufferLease? TakeLease(ObservationSession session,
        CameraAcquisitionAttempt attempt, ObservationContext context, string label)
    {
        var outcome = attempt.Outcome;
        if (outcome is null)
            return null;
        IFrameBufferLease? lease;
        try { lease = outcome.TakeFrame(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new ProductMismatchException("CameraAcquisitionLeaseTransferFailed"); }
        if (lease is not null)
        {
            session.HeldLeases.Add(lease);
            context.Append("leases", new LeaseFact(label, true, false));
        }
        else
        {
            try { outcome.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { throw new ProductMismatchException("CameraAcquisitionOutcomeDisposeFailed"); }
            context.Append("leases", new LeaseFact(label, false, true));
        }
        return lease;
    }

    private static async Task ReturnLeaseAsync(ObservationSession session,
        IFrameBufferLease lease, ObservationContext context, string label)
    {
        try
        {
            var task = session.CallSync(() =>
            {
                lease.Dispose();
                return true;
            });
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new ProductMismatchException("CameraLeaseDisposeFailed"); }

        if (!lease.IsReturned)
            throw new ProductMismatchException("CameraLeaseReturnNotObserved");
        context.Append("leases", new LeaseFact(label, true, true));
    }

    private static string HashFrame(VisionFrame frame)
    {
        var bytesPerPixel = frame.PixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => 0
        };
        if (!ValidatePixelRows(frame, bytesPerPixel, out var hash,
                out var upperBoundValid) || !upperBoundValid)
            throw new ProductMismatchException("CameraFrameHashUnavailable");
        return hash;
    }

    private static FrameObservation InspectFrame(ObservationSession session,
        CameraAcquisitionAttempt attempt, IFrameBufferLease lease,
        string expectedHash, ObservationContext context, string label)
    {
        if (lease is null || attempt.Outcome is null || attempt.Correlation is null)
            throw new ProductMismatchException("CameraFrameLeaseMissing");
        try
        {
            var frame = lease.Frame;
            var metadata = frame.Metadata;
            var provenance = lease.Provenance;
            var bytesPerPixel = frame.PixelFormat switch
            {
                VisionPixelFormat.Mono8 => 1,
                VisionPixelFormat.Mono16 => 2,
                VisionPixelFormat.Bgr24 => 3,
                _ => 0
            };
            var validRowBytes = bytesPerPixel > 0 &&
                frame.Width > 0 && frame.Width <= int.MaxValue / bytesPerPixel
                ? frame.Width * bytesPerPixel : 0;
            var pixelFormatMatches = frame.PixelFormat == session.Effective!.PixelFormat;
            var validBitsMatches = frame.ValidBits == session.Effective.ValidBits;
            var layoutValid = bytesPerPixel > 0 && frame.Width ==
                session.Effective!.RegionOfInterest.Width &&
                frame.Height == session.Effective.RegionOfInterest.Height &&
                frame.StrideBytes >= validRowBytes &&
                metadata.ValidRowBytes == validRowBytes &&
                metadata.RequiredBufferLength <= metadata.FullBufferLayoutLength;
            var validBitsValid = ValidatePixelRows(frame, bytesPerPixel, out var hash,
                out var upperBoundValid);
            var hashMatched = StringComparer.Ordinal.Equals(hash, expectedHash);
            var correlationMatches = Equals(frame.Correlation, attempt.Correlation) &&
                Equals(provenance.Correlation, attempt.Correlation);
            var roleMatches = StringComparer.Ordinal.Equals(frame.LogicalCameraRole,
                session.Fixture.LogicalCameraRole);
            var effectiveMatches = frame.EffectiveCameraConfiguration.Equals(session.Effective);
            var providerMatches = StringComparer.Ordinal.Equals(provenance.ProviderId,
                    session.Context.Declaration.Provider.Id) &&
                StringComparer.Ordinal.Equals(provenance.ProviderVersion,
                    session.Context.Declaration.Provider.Version) &&
                StringComparer.Ordinal.Equals(provenance.AdapterId,
                    session.Context.Declaration.Provider.AdapterPackageId) &&
                StringComparer.Ordinal.Equals(provenance.AdapterVersion,
                    session.Context.Declaration.Provider.AdapterVersion);
            var stableMatches = StringComparer.Ordinal.Equals(provenance.StableDeviceIdentity,
                session.Context.Declaration.StableDeviceIdentity);
            var paddingZeroed = provenance.PoolCopyEvidence?.PaddingZeroed == true;
            var identityCopy = provenance.PoolCopyEvidence?.IdentityPixelCopy == true;
            var contractSatisfied = layoutValid && pixelFormatMatches && validBitsMatches &&
                validBitsValid && upperBoundValid &&
                hashMatched && correlationMatches && roleMatches && effectiveMatches &&
                providerMatches && stableMatches && paddingZeroed && identityCopy;
            var observation = new FrameObservation(label, true, correlationMatches,
                roleMatches, effectiveMatches, providerMatches, stableMatches,
                frame.Width, frame.Height, frame.StrideBytes, metadata.ValidRowBytes,
                frame.PixelFormat.ToString(), frame.ValidBits, pixelFormatMatches,
                validBitsMatches, hash, hashMatched,
                layoutValid, validBitsValid, upperBoundValid,
                provenance.NormalizationAllocated, provenance.NormalizationTransformed,
                paddingZeroed, identityCopy, contractSatisfied);
            context.Append("frames", observation);
            return observation;
        }
        catch (ProductMismatchException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new ProductMismatchException("CameraFrameObservationFailed"); }
    }

    private static bool ValidatePixelRows(VisionFrame frame, int bytesPerPixel,
        out string hash, out bool upperBoundValid)
    {
        upperBoundValid = true;
        var validRowBytes = frame.Metadata.ValidRowBytes;
        if (bytesPerPixel == 0 || validRowBytes != frame.Width * bytesPerPixel)
        {
            hash = string.Empty;
            return false;
        }

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            for (var row = 0; row < frame.Height; row++)
            {
                var bytes = frame.GetRowSpan(row);
                if (bytes.Length != validRowBytes)
                {
                    hash = string.Empty;
                    return false;
                }
                if (frame.PixelFormat == VisionPixelFormat.Mono16 &&
                    frame.ValidBits is { } validBits && validBits < 16)
                {
                    var maximum = (ushort)((1 << validBits) - 1);
                    for (var offset = 0; offset < bytes.Length; offset += 2)
                    {
                        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)) > maximum)
                            upperBoundValid = false;
                    }
                }
                incremental.AppendData(bytes);
            }
            hash = Convert.ToHexString(incremental.GetHashAndReset());
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            hash = string.Empty;
            return false;
        }
    }

    private static bool IsArmedHealthy(CameraHealthSnapshot health) =>
        health.ProviderAvailability == CameraProviderAvailability.Available &&
        health.Connection == CameraConnectionState.Open &&
        health.Configuration == CameraConfigurationState.Applied &&
        health.Acquisition == CameraAcquisitionState.Armed;

    private static RequestedCameraConfiguration SelectConfiguration(
        CameraConformanceFixtureDeclaration declaration, CameraConformanceCase kind)
    {
        RequestedCameraConfiguration? selected = null;
        if (kind == CameraConformanceCase.HardwareAcquisition)
            selected = declaration.Configurations.FirstOrDefault(configuration =>
                configuration.ProductionAcquisitionMode == ProductionAcquisitionMode.HardwareTrigger);
        else if (kind == CameraConformanceCase.DisconnectRecovery)
            selected = declaration.Configurations.FirstOrDefault(configuration =>
                configuration.ProductionAcquisitionMode == ProductionAcquisitionMode.SoftwareTrigger);
        else
            selected = declaration.Configurations.FirstOrDefault(configuration =>
                configuration.ProductionAcquisitionMode == ProductionAcquisitionMode.SoftwareTrigger);

        selected ??= declaration.Configurations.FirstOrDefault();
        if (selected is null)
            throw new BlockedObservationException("CameraConfigurationRequired");
        if (kind == CameraConformanceCase.HardwareAcquisition && selected.ProductionAcquisitionMode !=
            ProductionAcquisitionMode.HardwareTrigger)
            throw new BlockedObservationException("HardwareTriggerConfigurationRequired");
        if (kind == CameraConformanceCase.DisconnectRecovery && selected.ProductionAcquisitionMode !=
            ProductionAcquisitionMode.SoftwareTrigger)
            throw new BlockedObservationException("RecoverySoftwareConfigurationRequired");
        return selected;
    }

    private static int ConfigurationIndex(CameraConformanceFixtureDeclaration declaration,
        RequestedCameraConfiguration configuration)
    {
        for (var index = 0; index < declaration.Configurations.Count; index++)
            if (Equals(declaration.Configurations[index], configuration))
                return index;
        throw new BlockedObservationException("CameraConfigurationNotFrozen");
    }

    private static string ConfigurationHash(RequestedCameraConfiguration configuration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, EvidenceJson);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool SameBinding(CameraDeviceDescriptor descriptor,
        CameraProviderIdentity provider, string stableIdentity) =>
        descriptor is not null && SameProvider(descriptor.Provider, provider) &&
        StringComparer.Ordinal.Equals(descriptor.StableDeviceIdentity, stableIdentity);

    private static bool SameProvider(CameraProviderIdentity? actual,
        CameraProviderIdentity? expected) =>
        actual is not null && expected is not null &&
        StringComparer.Ordinal.Equals(actual.Id, expected.Id) &&
        StringComparer.Ordinal.Equals(actual.Version, expected.Version) &&
        StringComparer.Ordinal.Equals(actual.AdapterPackageId, expected.AdapterPackageId) &&
        StringComparer.Ordinal.Equals(actual.AdapterVersion, expected.AdapterVersion);

    private static TimeSpan MonotonicDuration(long delta, long frequency)
    {
        if (delta <= 0) return TimeSpan.Zero;
        if (frequency is < 1 or > 10_000_000_000)
            throw new BlockedObservationException("CameraClockInvalid");
        var ticks = delta * (double)TimeSpan.TicksPerSecond / frequency;
        if (!double.IsFinite(ticks) || ticks >= TimeSpan.MaxValue.Ticks)
            throw new BlockedObservationException("CameraClockDurationInvalid");
        return TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(ticks)));
    }

    private static string SafeReason(string? reason, string fallback)
    {
        if (string.IsNullOrEmpty(reason)) return fallback;
        var length = Math.Min(reason.Length, 128);
        for (var index = 0; index < length; index++)
        {
            var character = reason[index];
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-' or ':'))
                return fallback;
        }
        return reason[..length];
    }

    private static async Task<T> AwaitBoundedAsync<T>(Task<T> task,
        CancellationToken cancellationToken, string timeoutReason, string failureReason)
    {
        try
        {
            return await task.WaitAsync(PublicOperationBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        { throw new BlockedObservationException(timeoutReason); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new BlockedObservationException("CameraConformanceCancelled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new BlockedObservationException(failureReason); }
    }

    private static async Task AwaitBoundedAsync(Task task,
        CancellationToken cancellationToken, string timeoutReason, string failureReason)
    {
        try
        {
            await task.WaitAsync(PublicOperationBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        { throw new BlockedObservationException(timeoutReason); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new BlockedObservationException("CameraConformanceCancelled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new BlockedObservationException(failureReason); }
    }

    private static Task<T> InvokeOnWorker<T>(Func<ValueTask<T>> operation)
    {
        return Task.Factory.StartNew(
            static state => ((Func<ValueTask<T>>)state!).Invoke().AsTask(), operation,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    private static Task InvokeOnWorker(Func<ValueTask> operation)
    {
        return Task.Factory.StartNew(
            static state => ((Func<ValueTask>)state!).Invoke().AsTask(), operation,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    private static Task<T> InvokeSyncOnWorker<T>(Func<T> operation) =>
        Task.Factory.StartNew(static state => ((Func<T>)state!).Invoke(), operation,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private sealed class ObservationContext
    {
        private readonly List<ObservationSession> _sessions = new();
        private readonly List<Task<ICameraConformanceFixture>> _unclaimedFixtures = new();
        private bool _cleaned;
        private bool _cleanupSafe = true;

        internal ObservationContext(CameraConformanceCase kind,
            CameraConformanceFixtureDeclaration declaration)
        {
            Kind = kind;
            Declaration = declaration;
            Facts["schema"] = "camera-public-observation-v1";
            Facts["case"] = kind.ToString();
            Facts["fixtureId"] = declaration.FixtureId;
            Facts["fixtureVersion"] = declaration.FixtureVersion;
            Facts["fixtureContentHash"] = declaration.ContentHash;
            Facts["configurationCount"] = declaration.Configurations.Count;
            Facts["configurationHashes"] = declaration.Configurations
                .Select(ConfigurationHash).ToArray();
            Facts["canonicalHashCount"] = declaration.CanonicalFrameHashes.Count;
            Facts["poolCapacity"] = declaration.PoolCapacity;
            Facts["seed"] = declaration.Seed;
            Facts["qualificationOnly"] = true;
            Facts["ready"] = false;
        }

        internal CameraConformanceCase Kind { get; }
        internal CameraConformanceFixtureDeclaration Declaration { get; }
        internal Dictionary<string, object?> Facts { get; } = new(StringComparer.Ordinal);

        internal void Add(string name, object? value) => Facts[name] = value;

        internal void Append(string name, object? value)
        {
            if (!Facts.TryGetValue(name, out var existing) || existing is not List<object?> list)
            {
                list = new List<object?>();
                Facts[name] = list;
            }
            if (list.Count < 256)
                list.Add(value);
        }

        internal void RecordClock(ObservationSession session, string label,
            FrameTimePoint point)
        {
            Append("clock", new ClockFact(label, session.Fixture.Clock.Frequency,
                point.MonotonicTimestamp, point.HostObservedAtUtc.ToString("O")));
        }

        internal void AddSession(ObservationSession session) => _sessions.Add(session);

        internal Task<T> Track<T>(Task<T> task)
        {
            // The session owns physical tasks.  This context method is used only
            // for fixture creation, whose result may itself arrive late.
            return task;
        }

        internal void TrackUnclaimedFixture(Task<ICameraConformanceFixture> task)
        {
            if (!_unclaimedFixtures.Contains(task))
                _unclaimedFixtures.Add(task);
        }

        internal async Task<bool> CleanupAsync()
        {
            if (_cleaned) return _cleanupSafe;
            _cleaned = true;
            foreach (var session in _sessions.ToArray())
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { _cleanupSafe = false; }
                _cleanupSafe &= session.CleanupSafe;
            }

            foreach (var task in _unclaimedFixtures.ToArray())
            {
                ICameraConformanceFixture? fixture = null;
                try { fixture = await task.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { _cleanupSafe = false; }
                if (fixture is null) continue;
                try
                {
                    await InvokeOnWorker(() => fixture.DisposeAsync()).ConfigureAwait(false);
                    await fixture.CleanupCompletion.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { _cleanupSafe = false; }
            }
            Facts["cleanupCompleted"] = true;
            Facts["cleanupSafe"] = _cleanupSafe;
            return _cleanupSafe;
        }

        internal ConformanceObservation CreateObservation(ObservationDecision decision)
        {
            Facts["status"] = decision.IsBlocked ? "Blocked" :
                decision.IsMismatch ? "Mismatch" : "Satisfied";
            Facts["reasonCode"] = decision.ReasonCode;
            byte[] evidenceBytes;
            try { evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(Facts, EvidenceJson); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                decision = ObservationDecision.Blocked("CameraConformanceEvidenceSerializationFailed");
                evidenceBytes = MinimalEvidence(decision);
            }
            if (evidenceBytes.Length > MaximumEvidenceBytes)
            {
                decision = ObservationDecision.Blocked("CameraConformanceEvidenceCapacityExceeded");
                evidenceBytes = MinimalEvidence(decision);
            }
            var evidence = new ConformanceEvidence("camera-public-observation",
                evidenceBytes);
            var observed = decision.IsBlocked
                ? "CameraContractUnavailable:" + decision.ReasonCode
                : decision.IsMismatch
                    ? "CameraContractMismatch:" + decision.ReasonCode
                    : "CameraContractSatisfied";
            return new ConformanceObservation(observed, new[] { evidence },
                decision.IsBlocked ? ConformanceObservationStatus.Blocked :
                    ConformanceObservationStatus.Observed, decision.ReasonCode);
        }

        private byte[] MinimalEvidence(ObservationDecision decision) =>
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "camera-public-observation-v1",
                @case = Kind.ToString(),
                fixtureId = Declaration.FixtureId,
                fixtureContentHash = Declaration.ContentHash,
                status = decision.IsBlocked ? "Blocked" :
                    decision.IsMismatch ? "Mismatch" : "Satisfied",
                reasonCode = decision.ReasonCode,
                cleanupCompleted = Facts.TryGetValue("cleanupCompleted", out var completed) &&
                    completed is true,
                cleanupSafe = Facts.TryGetValue("cleanupSafe", out var safe) && safe is true
            }, EvidenceJson);
    }

    private sealed class ObservationSession
    {
        private readonly ObservationContext _context;
        private readonly object _sync = new();
        private readonly List<Task> _actualTasks = new();
        private Task? _disposeTask;
        private bool _cleanupSafe = true;

        internal ObservationSession(ObservationContext context,
            ICameraConformanceFixture fixture)
        {
            _context = context;
            Fixture = fixture;
        }

        internal ICameraConformanceFixture Fixture { get; }
        internal ICameraProvider Provider => Fixture.Provider;
        internal ICameraDevice? Device { get; set; }
        internal IControlledCameraDevice? Controlled { get; set; }
        internal Task<CameraOpenResult>? OpenTask { get; set; }
        internal CameraAcquisitionService? Acquisition { get; set; }
        internal CameraRecoveryService? Recovery { get; set; }
        internal EffectiveCameraConfiguration? Effective { get; set; }
        internal List<IFrameBufferLease> HeldLeases { get; } = new();
        internal bool CleanupSafe => _cleanupSafe;
        internal ObservationContext Context => _context;

        internal Task<T> Call<T>(Func<ValueTask<T>> operation)
        {
            var task = InvokeOnWorker(operation);
            lock (_sync) _actualTasks.Add(task);
            return task;
        }

        internal Task Call(Func<ValueTask> operation)
        {
            var task = InvokeOnWorker(operation);
            lock (_sync) _actualTasks.Add(task);
            return task;
        }

        internal Task<T> CallSync<T>(Func<T> operation)
        {
            var task = InvokeSyncOnWorker(operation);
            lock (_sync) _actualTasks.Add(task);
            return task;
        }

        internal Task DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposeTask is not null)
                    return _disposeTask;

                // Start the cleanup body on the default scheduler.  The body
                // calls Call/CallSync, which take _sync; running its initial
                // synchronous portion while this lock is held would deadlock.
                _disposeTask = Task.Factory.StartNew(
                    static state => ((ObservationSession)state!).DisposeCoreAsync(),
                    this, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
                return _disposeTask;
            }
        }

        private async Task DisposeCoreAsync()
        {
            foreach (var lease in HeldLeases.ToArray())
            {
                try
                {
                    var task = CallSync(() =>
                    {
                        lease.Dispose();
                        return true;
                    });
                    await task.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { _cleanupSafe = false; }
            }

            if (Recovery is not null)
            {
                var retired = await RetireRuntimeAsync(
                    () => Recovery.RetireAsync(), "recovery").ConfigureAwait(false);
                if (!retired)
                {
                    _context.Append("sessionCleanup", new SessionCleanupFact(false));
                    await Fixture.CleanupCompletion.ConfigureAwait(false);
                    return;
                }
            }
            else if (Acquisition is not null)
            {
                var retired = await RetireRuntimeAsync(
                    () => Acquisition.RetireAsync(), "acquisition").ConfigureAwait(false);
                if (!retired)
                {
                    _context.Append("sessionCleanup", new SessionCleanupFact(false));
                    await Fixture.CleanupCompletion.ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                // Without a Runtime owner, a late OpenAsync result may be the
                // only owner of the device.  Drain the actual task before
                // disposing the provider, then adopt and dispose that device.
                await DrainActualAsync().ConfigureAwait(false);
                AdoptLateOpenedDeviceIfAvailable();
                if (Device is not null)
                {
                    var stopSafe = false;
                    try
                    {
                        var stop = await Call(() => Device.StopAsync(CancellationToken.None)).ConfigureAwait(false);
                        stopSafe = stop.Succeeded;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                    var disposed = await DisposeResourceAsync(() => Device.DisposeAsync()).ConfigureAwait(false);
                    if (!stopSafe || !disposed)
                    {
                        _cleanupSafe = false;
                        _context.Append("sessionCleanup", new SessionCleanupFact(false));
                        await Fixture.CleanupCompletion.ConfigureAwait(false);
                        return;
                    }
                }
            }

            // RetireAsync owns all Runtime physical calls.  This drain only
            // joins observer handoffs and never precedes a Runtime retirement.
            await DrainActualAsync().ConfigureAwait(false);
            if (Recovery is null && Provider is not null)
            {
                if (!await DisposeResourceAsync(() => Provider.DisposeAsync()).ConfigureAwait(false))
                {
                    await Fixture.CleanupCompletion.ConfigureAwait(false);
                    return;
                }
            }
            await DisposeResourceAsync(() => Fixture.DisposeAsync()).ConfigureAwait(false);
            await DrainActualAsync().ConfigureAwait(false);
            try { await Fixture.CleanupCompletion.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { _cleanupSafe = false; }
            _context.Append("sessionCleanup", new SessionCleanupFact(_cleanupSafe));
        }

        private async Task<bool> RetireRuntimeAsync(
            Func<Task<CameraRetirementObservation>> operation, string label)
        {
            try
            {
                var task = Call(() => new ValueTask<CameraRetirementObservation>(
                    operation()));
                var retirement = await task.ConfigureAwait(false);
                _context.Append("retirements", new RetirementFact(label,
                    retirement.SafeToReplace, SafeReason(retirement.ReasonCode,
                        "CameraRetirementUnknown")));
                if (!retirement.SafeToReplace)
                    _cleanupSafe = false;
                return retirement.SafeToReplace;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _cleanupSafe = false;
                _context.Append("retirements", new RetirementFact(label, false,
                    "CameraRetirementFailed"));
                return false;
            }
        }

        private void AdoptLateOpenedDeviceIfAvailable()
        {
            var task = OpenTask;
            if (task is null || !task.IsCompletedSuccessfully || Device is not null)
                return;
            try
            {
                var result = task.GetAwaiter().GetResult();
                if (result.Succeeded && result.Device is not null)
                {
                    Device = result.Device;
                    Controlled = result.Device as IControlledCameraDevice;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { _cleanupSafe = false; }
        }

        private async Task<bool> DisposeResourceAsync(Func<ValueTask> operation)
        {
            try
            {
                var task = Call(operation);
                await task.ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { _cleanupSafe = false; return false; }
        }

        private async Task DrainActualAsync()
        {
            while (true)
            {
                Task[] tasks;
                lock (_sync) tasks = _actualTasks.Where(task => !task.IsCompleted).ToArray();
                if (tasks.Length == 0) return;
                foreach (var task in tasks)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { _cleanupSafe = false; }
                }
            }
        }
    }

    private readonly record struct ObservationDecision(bool IsBlocked, bool IsMismatch,
        string ReasonCode)
    {
        internal static ObservationDecision Satisfied => new(false, false, "");
        internal static ObservationDecision Blocked(string reason) =>
            new(true, false, SafeReason(reason, "CameraConformanceUnavailable"));
        internal static ObservationDecision Mismatch(string reason) =>
            new(false, true, SafeReason(reason, "CameraContractMismatch"));
    }

    private sealed class BlockedObservationException : Exception
    {
        internal BlockedObservationException(string reasonCode) =>
            ReasonCode = SafeReason(reasonCode, "CameraConformanceUnavailable");
        internal string ReasonCode { get; }
    }

    private sealed class ProductMismatchException : Exception
    {
        internal ProductMismatchException(string reasonCode) =>
            ReasonCode = SafeReason(reasonCode, "CameraContractMismatch");
        internal string ReasonCode { get; }
    }

    private sealed record ClockFact(string Label, long Frequency,
        long MonotonicTimestamp, string HostObservedAtUtc);
    private sealed record StimulusFact(string Kind, long ElapsedTicks,
        bool CorrelationSupplied, bool Delivered, string ReasonCode,
        long ObservedMonotonicTimestamp);
    private sealed record DiscoveryFact(bool Succeeded, string ReasonCode,
        int DeviceCount, bool ExactTargetFound, bool ProviderIdentityExact);
    private sealed record OpenFact(bool Succeeded, bool ExactBinding,
        bool ControlledSurface, bool ReportedModelPresent);
    private sealed record OperationFact(bool Succeeded, string ReasonCode);
    private sealed record RetirementFact(string Owner, bool SafeToReplace,
        string ReasonCode);
    private sealed record ServiceFact(bool Ready, bool EffectiveCaptured);
    private sealed record ConfigurationFact(int Index, string InputHash,
        bool ValidationSucceeded, bool AppliedSucceeded, bool EffectivePresent,
        bool ReadBackSucceeded, string ReasonCode);
    private sealed record HealthFact(string Label, string ProviderAvailability,
        string Connection, string Configuration, string Acquisition,
        string? FaultClassification, string? FaultReasonCode,
        long MonotonicTimestamp)
    {
        internal HealthFact(string label, CameraHealthSnapshot health)
            : this(label, health.ProviderAvailability.ToString(), health.Connection.ToString(),
                health.Configuration.ToString(), health.Acquisition.ToString(),
                health.LastFault?.Classification.ToString(),
                health.LastFault is null ? null : SafeReason(health.LastFault.ReasonCode,
                    "CameraFault"), health.ObservedAt.MonotonicTimestamp) { }

        internal static HealthFact From(CameraHealthSnapshot health) => new("snapshot", health);
    }
    private sealed record OutcomeFact(bool Accepted, bool CorrelationPresent,
        string ReasonCode, string? ExecutionStatus, string? FailureKind,
        bool LeasePresent);
    private sealed record LeaseFact(string Label, bool LeasePresent,
        bool OutcomeDisposed);
    private sealed record FrameObservation(string Label, bool LeasePresent,
        bool CorrelationMatches, bool RoleMatches, bool EffectiveMatches,
        bool ProviderIdentityMatches, bool StableIdentityMatches, int Width,
        int Height, int StrideBytes, int ValidRowBytes, string PixelFormat,
        int? ValidBits, bool PixelFormatMatches, bool ValidBitsMatches,
        string Hash, bool HashMatches, bool LayoutValid,
        bool ValidBitsValid, bool ValidBitsUpperBoundValid, bool NormalizationAllocated,
        bool NormalizationTransformed, bool PaddingZeroed, bool IdentityPixelCopy,
        bool ContractSatisfied);
    private sealed record AcquisitionObservation(CameraAcquisitionAttempt Attempt,
        IFrameBufferLease? Lease, CameraAcquisitionBusySnapshot? Busy)
    {
        internal ExecutionCorrelationId? Correlation => Attempt.Correlation;
    }
    private sealed record ProtocolFact(long ThroughSequence, int ObservationCount,
        bool Overflowed);
    private sealed record RecoveryFact(string State, bool SourceHealthy,
        bool HealthPresent, long Revision, int AttemptCount,
        int MaximumAttempts, long? NextAttemptTimestamp, string ReasonCode)
    {
        internal static RecoveryFact From(CameraRecoverySnapshot snapshot) => new(
            snapshot.State.ToString(), snapshot.SourceHealthy, snapshot.Health is not null,
            snapshot.Revision, snapshot.AttemptCount, snapshot.MaximumAttempts,
            snapshot.NextAttemptTimestamp, SafeReason(snapshot.ReasonCode,
                "CameraRecoveryUnknown"));
    }
    private sealed record LeaseLifetimeFact(bool Returned, bool AccessRejected,
        bool FirstFrameStable);
    private sealed record SessionCleanupFact(bool Safe);
}
