using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V143_R10_RuntimeCorrectionsAppendAChainWithoutChangingOriginalEvidence(bool optionalMissing)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64));
        var provider = new StagedPartIdentityProvider(binding);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(optionalMissing
                ? PartIdentityRequirementMode.Optional : PartIdentityRequirementMode.Required, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, value => value.Ready, "Correction original Ready");
        var snapshot = await harness.Runtime.GetSnapshotAsync();
        if (!optionalMissing)
            Assert.True(provider.Stage(new(Guid.NewGuid(), new(snapshot.RuntimeEpoch,
                harness.Service<ProductionInspectionOptions>().Profile.EndpointBindingHash,
                snapshot.PlcCommunication!.ConnectionGeneration, 61, 1), "A-001")).Accepted);
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Correction original ACK reset");
        var productionQuery = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var original = Assert.IsType<ProductionInspectionCore>((await productionQuery.ReadCurrentAsync()).Latest!.Core);
        var evidence = Assert.IsType<PartIdentityEvidence>(original.Admission.PartIdentityEvidence);
        var query = harness.Service<IPartIdentityHistoryQuery>();

        var firstCommand = await AuthorizeRuntimeIdentityCorrectionAsync(harness, original, null, evidence.Value, "B-002");
        AssertAccepted(await harness.Runtime.SubmitAsync(firstCommand), "First historical correction");
        var first = Assert.IsType<PartIdentityHistoryEvent>((await query.ReadAsync(original.Admission.InspectionId)).Latest);
        Assert.Equal(evidence.Value, first.OldValue);
        Assert.Equal("B-002", first.NewValue);
        Assert.Equal(evidence.ContentHash, first.Evidence!.ContentHash);
        Assert.True(first.CommandAuditSequence > 0);
        Assert.True(first.AuthorizationAuditSequence > 0);
        Assert.NotNull(first.CommandAuditHash);
        Assert.NotNull(first.AuthorizationAuditHash);

        var secondCommand = await AuthorizeRuntimeIdentityCorrectionAsync(harness, original, first.ContentHash, "B-002", "C-003");
        AssertAccepted(await harness.Runtime.SubmitAsync(secondCommand), "Second historical correction");
        var history = await query.QueryAsync(new(InspectionId: original.Admission.InspectionId));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(2, history.Events.Count);
        var second = Assert.Single(history.Events, value => value.ExpectedPreviousCorrectionHash == first.ContentHash);
        Assert.Equal("B-002", second.OldValue);
        Assert.Equal("C-003", second.NewValue);
        Assert.Equal(evidence.ContentHash, second.Evidence!.ContentHash);
        var unchanged = Assert.IsType<ProductionInspectionCore>((await productionQuery.ReadAsync(original.Admission.InspectionId)).Latest!.Core);
        Assert.Equal(original.ContentHash, unchanged.ContentHash);
        Assert.Equal(original.PartIdentity, unchanged.PartIdentity);
        Assert.Equal(evidence.ContentHash, unchanged.Admission.PartIdentityEvidence!.ContentHash);
        Assert.Equal(1, peer.ResultValidHighCount);

        var stale = await AuthorizeRuntimeIdentityCorrectionAsync(harness, original, first.ContentHash, "B-002", "D-004");
        var rejected = await harness.Runtime.SubmitAsync(stale);
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal("PartIdentityCorrectionConflict", rejected.ReasonCode);
        Assert.Equal(2, (await query.QueryAsync(new(InspectionId: original.Admission.InspectionId))).Events.Count);

        await harness.StopRuntimePreservingFixtureAsync();
        await using var restarted = new StationRuntime(harness.Fixture.Store, TimeSpan.FromMilliseconds(20),
            harness.Fixture.Sessions, harness.Fixture.Authorization, productionStoreOptions: harness.Fixture.Options);
        var restartedEpoch = (await restarted.GetSnapshotAsync()).RuntimeEpoch;
        Assert.NotEqual(evidence.Cycle.RuntimeEpoch, restartedEpoch);
        var nextRuntimeCommand = await AuthorizeRuntimeIdentityCorrectionAsync(harness, original,
            second.ContentHash, "C-003", "D-004");
        AssertAccepted(await restarted.SubmitAsync(nextRuntimeCommand), "New Runtime corrects historical evidence");
        var nextRuntimeEvent = Assert.IsType<PartIdentityHistoryEvent>(
            (await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options).ReadAsync(original.Admission.InspectionId)).Latest);
        Assert.Equal(restartedEpoch, nextRuntimeEvent.RuntimeEpoch);
        Assert.Equal(evidence.Cycle.RuntimeEpoch, nextRuntimeEvent.Evidence!.Cycle.RuntimeEpoch);
        Assert.Equal(evidence.ContentHash, nextRuntimeEvent.Evidence.ContentHash);
        Assert.Equal(second.ContentHash, nextRuntimeEvent.ExpectedPreviousCorrectionHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V143_R11_RuntimeCorrectionRequiresStepUpAndALivePhysicalSession(bool permissionGranted)
    {
        await using var harness = await ManualHarness.CreateAsync(partIdentityStore: new(),
            allowPartIdentityCorrection: permissionGranted);
        var command = new CorrectProductionPartIdentityCommand(Guid.NewGuid(), harness.Invocation(),
            Guid.NewGuid(), new string('A', 64), null, "A-001", "B-002", "PartIdentityCorrection");
        var denied = await harness.Runtime.SubmitAsync(command);
        Assert.Equal(CommandDisposition.Rejected, denied.Disposition);
        Assert.Equal(AuditPersistence.Persisted, denied.Audit);
        Assert.Equal(permissionGranted ? "StepUpRequired" : "PermissionDenied", denied.ReasonCode);
        var integration = command with { CorrelationId = Guid.NewGuid(),
            Invocation = harness.Invocation() with { Source = CommandSource.Integration } };
        var remote = await harness.Runtime.SubmitAsync(integration);
        Assert.Equal(CommandDisposition.Rejected, remote.Disposition);
        Assert.Equal(AuditPersistence.Persisted, remote.Audit);
        Assert.Equal("PhysicalConsoleRequired", remote.ReasonCode);
        var session = harness.Invocation();
        Assert.True((await harness.Fixture.Sessions.LockAsync(session.SessionId, SessionLockReason.UserRequested)).Succeeded);
        var locked = await harness.Runtime.SubmitAsync(command with { CorrelationId = Guid.NewGuid(), Invocation = session });
        Assert.Equal(CommandDisposition.Rejected, locked.Disposition);
        Assert.Equal(AuditPersistence.Persisted, locked.Audit);
        Assert.Contains(locked.ReasonCode, new[] { "SessionLocked", "SessionMismatch", "AuthenticationRequired" });
        var history = await harness.Service<IPartIdentityHistoryQuery>().QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
    }

    private static async Task<CorrectProductionPartIdentityCommand> AuthorizeRuntimeIdentityCorrectionAsync(
        ManualHarness harness, ProductionInspectionCore original, string? previousHash, string? oldValue, string newValue)
    {
        var command = new CorrectProductionPartIdentityCommand(Guid.NewGuid(), harness.Invocation(),
            original.Admission.InspectionId, original.Admission.ContentHash, previousHash, oldValue, newValue,
            "PartIdentityCorrection");
        var grant = await ProductionGrantAsync(harness, Permission.CorrectHistoricalFact, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.CorrectHistoricalFact);
        return command with { Invocation = command.Invocation with { StepUpGrantId = grant } };
    }
}
