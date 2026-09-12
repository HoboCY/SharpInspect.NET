using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V152_R01")]
    public async Task V152_R01_RequiredCoreAndPlcResultCommitBeforeNetworkCompletes(bool reachBacklogLimit)
    {
        using var diagnostic = new OutboxPersistenceFailureCapture();
        using var productionExceptions = new ProductionExceptionObservation();
        using var receiver = new OutboxReceiverFixture();
        var transport = new RuntimeHeldOutboxTransport(receiver);
        var outbox = new ProductionOutboxStoreOptions(new[] { receiver.Route })
            { AttemptTimeout = TimeSpan.FromMinutes(1) };
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            outbox: outbox, outboxTransports: new(new[] { receiver.Binding(transport) }),
            tracePolicy: OutboxPolicy(outbox, reachBacklogLimit ? 1 : 100));
        using var issuer = new ProductionTestIssuer();
        try
        {
            await PrepareProductionAsync(harness, issuer);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, value => value.Ready, "Outbox isolated station Ready");
            peer.RaiseTrigger(61, 1);
            try { await WaitForProductionResultAsync(harness, peer, 1); }
            catch (XunitException)
            {
                var failedState = await harness.Runtime.GetSnapshotAsync();
                var failedHistory = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
                    .QueryAsync(new(PageSize: 128));
                throw new XunitException($"Outbox production publication: {failedState.ArmState}/{failedState.Recovery}/" +
                    $"{failedState.CurrentExecution}; store={harness.Fixture.Store.Integrity?.ReasonCode}; " +
                    $"history={failedHistory.ReasonCode}:" + string.Join(";", failedHistory.Events.Select(entry =>
                        entry.Kind + ":" + entry.ReasonCode)) + ";gates=" + string.Join(";",
                        failedState.ProductionAdmission!.Gates.Where(gate => gate.Status != ProductionAdmissionGateStatus.Passed)
                            .Select(gate => gate.Gate + ":" + gate.ReasonCode)) + ";exceptions=" + productionExceptions.Read() +
                    ";persistence=" + diagnostic.Read());
            }
            peer.SetTrigger(false);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitConditionAsync(() => peer.AckLowCount == 1,
                "The accepted PLC cycle finishes while external sending is blocked");
            Assert.False(transport.Release.Task.IsCompleted);
            Assert.Equal(0L, receiver.EffectCount());
            var coreRead = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(coreRead.Available, coreRead.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(coreRead.Latest!.Core);
            Assert.Null(core.ImageEvidence); // Fresh schema36 requires no image feature or finalizer.
            var query = harness.Service<IProductionOutboxQuery>();
            var page = await query.ReadPendingAsync();
            Assert.True(page.Available, page.ReasonCode);
            var delivery = Assert.Single(page.Items).Delivery;
            Assert.Equal(core.ContentHash, delivery.CoreHash);
            Assert.Equal(core.Admission.InspectionId, delivery.InspectionId);
            Assert.Equal(delivery.DeliveryId, transport.Delivery!.DeliveryId);
            Assert.Equal(delivery.Payload!.CopyBytes(), transport.Delivery.Payload!.CopyBytes());
            if (reachBacklogLimit)
                await WaitProductionAsync(harness, state => !state.Ready && state.AlarmState?.Instances.Any(alarm =>
                    alarm.Code == "OutboxRequiredDeliveryBlocked" &&
                    alarm.ProductionImpact == ProductionImpact.BlockNewTriggers && !alarm.SourceHealthy) == true,
                    "Required count limit blocks the next trigger after the accepted PLC result");
            else
                await WaitProductionAsync(harness, state => state.Ready,
                    "Pending Required delivery below its policy budget permits the next trigger");
            transport.Release.TrySetResult(true);
            var success = await WaitOutboxEventAsync(query, delivery.DeliveryId, OutboxEventKind.Succeeded);
            Assert.Equal(SystemPrincipalId.Outbox, success.PrincipalId);
            Assert.Equal(delivery.Payload.ContentHash, success.PayloadHash);
            Assert.Equal(1L, receiver.EffectCount());
            Assert.Equal(core.ContentHash,
                (await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync()).Latest!.Core!.ContentHash);
        }
        finally { transport.Release.TrySetResult(true); }
    }

    [Theory]
    [InlineData(OutboxRouteCriticality.Required)]
    [InlineData(OutboxRouteCriticality.BestEffort)]
    [Trait("VerificationId", "V152_R02")]
    public async Task V152_R02_PermanentFailureBlocksOnlyRequiredAndNeverRewritesPublishedCore(OutboxRouteCriticality criticality)
    {
        using var diagnostic = new OutboxPersistenceFailureCapture();
        using var productionExceptions = new ProductionExceptionObservation();
        using var receiver = new OutboxReceiverFixture(criticality);
        var transport = new RuntimeRejectedOutboxTransport();
        var outbox = new ProductionOutboxStoreOptions(new[] { receiver.Route });
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            outbox: outbox, outboxTransports: new(new[] { receiver.Binding(transport) }), tracePolicy: OutboxPolicy(outbox));
        await using var stateObservation = new ProductionStateObservation(harness.Runtime);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Outbox failure fixture Ready");
        peer.RaiseTrigger(61, 1);
        try { await WaitForProductionResultAsync(harness, peer, 1); }
        catch (XunitException error)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            var facts = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
            throw new XunitException(error.Message + $";state={state.ArmState}/{state.Ready}/{state.Recovery}/{state.CurrentExecution};" +
                string.Join(";", state.AdmissionBlockers) + ";facts=" + string.Join(";", facts.Events.Select(fact =>
                    fact.Kind + ":" + fact.ReasonCode)) + ";persistence=" + diagnostic.Read() +
                ";exceptions=" + productionExceptions.Read() + ";changes=" + stateObservation.Read());
        }
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Failure does not abort the accepted PLC cycle");
        var query = harness.Service<IProductionOutboxQuery>();
        var page = await query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        var delivery = Assert.Single(page.Items).Delivery;
        var failed = await WaitOutboxEventAsync(query, delivery.DeliveryId, OutboxEventKind.AttemptFailed);
        Assert.Equal(OutboxFailureCategory.Permanent, failed.FailureCategory);
        Assert.Equal(SystemPrincipalId.Outbox, failed.PrincipalId);
        var required = criticality == OutboxRouteCriticality.Required;
        var code = required ? "OutboxRequiredDeliveryBlocked" : "OutboxBestEffortDeliveryFailed";
        try { await WaitProductionAsync(harness, state => state.Ready != required && state.AlarmState?.Instances.Any(alarm =>
            alarm.Code == code && !alarm.SourceHealthy && alarm.ProductionImpact ==
                (required ? ProductionImpact.BlockNewTriggers : ProductionImpact.None)) == true,
            "Permanent failure has the route's exact production impact"); }
        catch (XunitException error) { throw new XunitException(error.Message + ";changes=" + stateObservation.Read()); }
        var final = await query.ReadPendingAsync();
        Assert.True(Assert.Single(final.Items).PermanentBlock);
        Assert.False(Assert.Single(final.Items).RetryEligible);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(1, peer.ResultValidHighCount);
        var core = (await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync()).Latest!.Core!;
        Assert.Equal(delivery.CoreHash, core.ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V152_R03")]
    public async Task V152_R03_LostAcknowledgementPersistsTwoAttemptsWithOneFrozenDeliveryAndOneReceiverEffect()
    {
        using var diagnostic = new OutboxPersistenceFailureCapture();
        using var receiver = new OutboxReceiverFixture();
        var transport = new ProductionOutboxProtocolTests.ReceiverTransport(receiver) { LoseFirstAcknowledgement = true };
        var outbox = new ProductionOutboxStoreOptions(new[] { receiver.Route });
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            outbox: outbox, outboxTransports: new(new[] { receiver.Binding(transport) }), tracePolicy: OutboxPolicy(outbox));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Lost acknowledgement fixture Ready");
        peer.RaiseTrigger(61, 1);
        try { await WaitForProductionResultAsync(harness, peer, 1); }
        catch (XunitException error)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            var facts = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
            throw new XunitException(error.Message + $";state={state.ArmState}/{state.Ready}/{state.Recovery}/{state.CurrentExecution};" +
                string.Join(";", state.AdmissionBlockers) + ";facts=" + string.Join(";", facts.Events.Select(fact =>
                    fact.Kind + ":" + fact.ReasonCode)) + ";persistence=" + diagnostic.Read());
        }
        peer.SetTrigger(false);
        var core = (await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync()).Latest!.Core!;
        var query = harness.Service<IProductionOutboxQuery>();
        IReadOnlyList<ProductionOutboxEvent> history;
        try { history = await WaitOutboxHistoryAsync(query, core.Admission.InspectionId,
            events => events.Any(entry => entry.Kind == OutboxEventKind.Succeeded)); }
        catch (XunitException error)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            var pending = await query.ReadPendingAsync();
            throw new XunitException(error.Message + $";state={state.ArmState}/{state.Evidence};" +
                string.Join(";", state.AdmissionBlockers) + ";pending=" + string.Join(";", pending.Items.Select(item =>
                    $"{item.State}/{item.ActiveAttemptId}/{item.RetryEligible}/{item.RetryAfterUtc:O}/{item.LastFailureReasonCode}")) +
                ";persistence=" + diagnostic.Read());
        }
        Assert.Equal(2, history.Count(entry => entry.Kind == OutboxEventKind.AttemptStarted));
        Assert.Single(history, entry => entry.Kind == OutboxEventKind.Created);
        Assert.Single(history, entry => entry.Kind == OutboxEventKind.Succeeded);
        Assert.Single(history, entry => entry.Kind == OutboxEventKind.AttemptFailed &&
            entry.FailureCategory == OutboxFailureCategory.UnknownOutcome);
        Assert.Single(history.Select(entry => entry.DeliveryId).Distinct());
        Assert.Single(history.Select(entry => entry.PayloadHash).Distinct());
        Assert.Equal(1L, receiver.EffectCount());
        Assert.Equal(transport.Deliveries[0].Bytes, transport.Deliveries[1].Bytes);
    }

    [Fact]
    [Trait("VerificationId", "V152_R04")]
    public async Task V152_R04_PngFinalizationAndOutboxCompleteTheSameCoreWithoutBlockingPlcPublication()
    {
        using var diagnostic = new OutboxPersistenceFailureCapture();
        using var root = new ProductionImageTestRoot();
        using var final = new ProductionImageFinalizationTestRoot(root.Options);
        using var receiver = new OutboxReceiverFixture();
        var transport = new RuntimeHeldOutboxTransport(receiver);
        var outbox = new ProductionOutboxStoreOptions(new[] { receiver.Route },
            new RecipeLifecycleStoreOptions(), root.Options, final.Options)
            { AttemptTimeout = TimeSpan.FromMinutes(1) };
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            imageEvidence: root.Options, imageFinalization: final.Options,
            capturePolicy: new("V152.Capture", "1", EvidenceCaptureMode.All),
            outbox: outbox, outboxTransports: new(new[] { receiver.Binding(transport) }), tracePolicy: OutboxPolicy(outbox));
        using var issuer = new ProductionTestIssuer();
        try
        {
            await PrepareProductionAsync(harness, issuer);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, state => state.Ready, "Combined image and Outbox station Ready");
            peer.RaiseTrigger(61, 1);
            await WaitForProductionResultAsync(harness, peer, 1);
            peer.SetTrigger(false);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var coreRead = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(coreRead.Available, coreRead.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(coreRead.Latest!.Core);
            var work = Assert.IsType<PendingImageFinalizationWork>(core.ImageEvidence!.Work);
            var images = harness.Service<IProductionImageEvidenceQuery>();
            var image = await WaitForFinalizationAsync(images, work.WorkId,
                item => item.State.State == ProductionImageFinalizationState.Succeeded &&
                    item.State.CleanupState == ProductionImageCleanupState.Released,
                "The schema36 image worker must finalize and release the same Core's image");
            Assert.Equal(work.Manifest.CanonicalPixelHash, image.State.Success!.CanonicalPixelHash);
            var finalPath = System.IO.Path.Combine(final.Path, work.Manifest.ManifestId.ToString("N") + ".png");
            Assert.Equal(work.Manifest.CanonicalPixelHash, CanonicalHashOf(finalPath, work.Manifest,
                CodecLimits(root.Options, final.Root)));
            Assert.False(transport.Release.Task.IsCompleted);
            Assert.Equal(0L, receiver.EffectCount());
            await WaitConditionAsync(() => peer.AckLowCount == 1, "Combined cycle completes PLC while network is held");
            await WaitProductionAsync(harness, state => state.Ready && state.Evidence.PendingRequiredImages == 0,
                "Finalized image and in-budget Outbox permit the next admission");
            transport.Release.TrySetResult(true);
            var delivered = await WaitOutboxEventAsync(harness.Service<IProductionOutboxQuery>(),
                transport.Delivery!.DeliveryId, OutboxEventKind.Succeeded);
            Assert.Equal(core.ContentHash, transport.Delivery.CoreHash);
            Assert.Equal(core.Admission.InspectionId, delivered.InspectionId);
            Assert.Equal(1L, receiver.EffectCount());
            Assert.Equal(0, (await images.ReadBacklogAsync()).Count);
        }
        catch (XunitException error)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            throw new XunitException(error.Message + ";blockers=" + string.Join(";", state.AdmissionBlockers) +
                ";persistence=" + diagnostic.Read());
        }
        finally { transport.Release.TrySetResult(true); peer.SetTrigger(false); }
    }

    private static TraceStoragePolicyDefinition OutboxPolicy(ProductionOutboxStoreOptions options, long maxItems = 100,
        long maxBytes = 64 * 1024 * 1024, TimeSpan? maxAge = null)
    {
        var original = TraceStoragePolicyRuntimeTests.Policy();
        return new(original.PolicyId, original.Version, original.ApprovalReference, original.ApprovalVersion,
            original.Rationale, original.RetentionRules, original.MinimumReserveBytes, original.MinimumReservePercent,
            options.Routes.Where(route => route.Criticality == OutboxRouteCriticality.Required).Select(route =>
                new TraceStorageRouteLimit(route.RouteId, route.Version, route.ContentHash,
                    new(maxItems, maxBytes, maxAge ?? TimeSpan.FromDays(1)))), original.ImageBacklog,
            original.EvidenceStageTimeout, original.TraceCommitTimeout, original.Scrubber, original.Checkpoint,
            original.MaximumWalBytes);
    }

    private static async Task<ProductionOutboxEvent> WaitOutboxEventAsync(IProductionOutboxQuery query,
        Guid deliveryId, OutboxEventKind kind, Func<string?>? unexpectedFailure = null)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        do
        {
            var page = await query.ReadHistoryAsync(deliveryId: deliveryId);
            Assert.True(page.Available, page.ReasonCode);
            Assert.Null(unexpectedFailure?.Invoke());
            if (page.Events.FirstOrDefault(entry => entry.Kind == kind) is { } found) return found;
            await Task.Delay(30);
        } while (DateTimeOffset.UtcNow < until);
        throw new XunitException($"Outbox {deliveryId} did not reach {kind}");
    }

    private static async Task<IReadOnlyList<ProductionOutboxEvent>> WaitOutboxHistoryAsync(IProductionOutboxQuery query,
        Guid inspectionId, Func<IReadOnlyList<ProductionOutboxEvent>, bool> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<ProductionOutboxEvent> last = Array.Empty<ProductionOutboxEvent>();
        do
        {
            var page = await query.ReadHistoryAsync(inspectionId: inspectionId);
            Assert.True(page.Available, page.ReasonCode);
            last = page.Events;
            if (predicate(page.Events)) return page.Events;
            await Task.Delay(30);
        } while (DateTimeOffset.UtcNow < until);
        throw new XunitException($"Outbox {inspectionId} did not meet its lifecycle predicate: " +
            string.Join(";", last.Select(entry => $"{entry.Kind}/{entry.ReasonCode}/{entry.AttemptNumber}")));
    }

    private sealed class OutboxPersistenceFailureCapture : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _failures = new();
        internal OutboxPersistenceFailureCapture() => AppDomain.CurrentDomain.FirstChanceException += Observe;
        private void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception.Message is not ("AuditAuthorizationPayloadInvalid" or "AuditIdentityPayloadInvalid") &&
                args.Exception.StackTrace is { } stack &&
                (stack.Contains("SharpInspect.Runtime.Integrity", StringComparison.Ordinal) ||
                 stack.Contains("SharpInspect.Runtime.Storage", StringComparison.Ordinal) ||
                 stack.Contains("SharpInspect.Runtime.Outbox", StringComparison.Ordinal)))
            {
                _failures.Enqueue(args.Exception.ToString());
                while (_failures.Count > 32) _failures.TryDequeue(out _);
            }
        }
        internal string Read() => string.Join("\n\n", _failures);
        public void Dispose()
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            if (_failures.IsEmpty) return;
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.OutboxDiagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"), string.Join("\n\n", _failures));
        }
    }

    private sealed class RuntimeHeldOutboxTransport : IOutboxRouteTransport
    {
        private readonly OutboxReceiverFixture _receiver;
        internal RuntimeHeldOutboxTransport(OutboxReceiverFixture receiver) => _receiver = receiver;
        public OutboxContractReference Contract => OutboxReceiverFixture.AdapterContract;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OutboxDelivery? Delivery { get; private set; }
        public async ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
            CancellationToken cancellationToken = default)
        {
            Delivery = delivery; Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(_receiver.Accept(delivery));
        }
    }
    private sealed class RuntimeRejectedOutboxTransport : IOutboxRouteTransport
    {
        internal int SendCount;
        public OutboxContractReference Contract => OutboxReceiverFixture.AdapterContract;
        public ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SendCount);
            return ValueTask.FromResult(new OutboxTransportObservation(OutboxFailureCategory.Permanent,
                "OutboxReceiverConfigurationRejected"));
        }
    }
}
