using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T46 PLC-requested activation authorization over the real identity writer and the
/// real governed selection. The runtime lease is a bounded internal fixture: it carries
/// no camera, no algorithm and no hardware callback, so it proves the durable
/// authorization, admission, terminal and identity binding only. It never claims a
/// physical Runtime acceptance, an armed station or a full activation pipeline, and its
/// runtime epoch is a fixture constant rather than the live station projection.
/// </summary>
public sealed class RecipeSelectionPlcAuthorizationTests
{
    private const string ChangeReason = "plc-recipe-change";
    private static readonly Guid RuntimeEpoch = Guid.Parse("0f146010-0000-4000-8000-000000000001");

    [Fact]
    public async Task V146_P01_ObservedRequestAdmitsAndTerminalFailureKeepsTheTruePlcAdapterActor()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var released = harness.Released;

        var change = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("1", RecipeSelectionIntegrationSupport.Entry(1, released)), null,
            "V146 enable the mapped PLC request for the released recipe");
        Assert.True(change.Outcome.Disposition == CommandDisposition.Accepted, change.Outcome.ReasonCode);
        var selection = Assert.IsType<RecipeSelectionRevision>(change.Revision);

        // The request identity derives from the observed endpoint/controller/sequence and
        // is bound to the exact persisted governed selection.
        var request = Request(selection);
        var observation = await harness.Store.AppendRecipeChangeEventAsync(request,
            RecipeChangeEventKind.RequestObserved, null, null, "RecipeChangeRequestObserved", null,
            new StoreDeadline(TimeSpan.FromSeconds(8)), CancellationToken.None);
        Assert.True(observation.Committed, observation.ReasonCode);
        await harness.WaitForVerifiedAsync();
        var observedHistory = await harness.RecipeChangeHistory.QueryAsync(new RecipeChangeHistoryFilter(
            RequestIdentityHash: request.RequestIdentityHash));
        Assert.True(observedHistory.Available, observedHistory.ReasonCode);
        var observedEvent = Assert.Single(observedHistory.Events);
        Assert.Equal(RecipeChangeEventKind.RequestObserved, observedEvent.Kind);
        Assert.Equal(request.ContentHash, observedEvent.Request.ContentHash);
        Assert.Equal(selection.Reference, observedEvent.Request.SelectionRevision);

        var terminalPublications = 0;
        var committedRecords = 0;
        var releases = 0;
        var lease = new RecipeActivationRuntimeLease(RuntimeEpoch, null, CancellationToken.None,
            publishTerminal: (_, _) => terminalPublications++,
            release: () => releases++,
            committed: _ => committedRecords++);
        var capability = new PlcRecipeActivationCapability(request, selection, lease, expectedActive: null);
        var command = Command(request);
        Assert.True(capability.TryConsume(command), "an observed exact request must be consumable exactly once");
        Assert.False(capability.TryConsume(command), "the same capability never authorizes a second attempt");

        var admission = await harness.Authorization.AdmitRecipeActivationAsync(command, RuntimeEpoch,
            Guid.NewGuid(), RecipeActivationEvidenceKind.LocalAuthority, Array.Empty<RecipeActivationCheck>(),
            null, () => null, new StoreDeadline(TimeSpan.FromSeconds(8)), CancellationToken.None, plc: capability);

        // The intended contract is an accepted admission; a rejection carrying
        // AuditPersistence.Unavailable means the PLC identity envelope never reached the
        // schema-31 identity writer.
        Assert.True(admission.Outcome.Disposition == CommandDisposition.Accepted,
            "PLC admission: " + admission.Outcome.ReasonCode);
        Assert.Equal("RecipeActivationAdmitted", admission.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, admission.Outcome.Audit);
        var admitted = Assert.IsType<RecipeActivationRecord>(admission.Record);
        AssertPlcActor(admitted, request);
        Assert.NotNull(admitted.Admission);
        Assert.Equal(RecipeActivationEvidenceKind.LocalAuthority, admitted.EvidenceKind);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, admitted.Outcome.State);
        Assert.Equal(request.OperationId, admitted.OperationId);
        Assert.NotEqual(Guid.Empty, admitted.AttemptId);
        Assert.Equal(released.Reference, admitted.Candidate);
        Assert.Equal(released.Record.ReleaseId, admitted.ReleaseId);
        Assert.Equal(released.Record.ContentHash, admitted.ReleaseRecordContentHash);

        // A restart-style terminal failure must keep the admitted PLC actor and never
        // invent a human principal, a session or a Step-Up grant.
        var restoration = new RecipeActivationRestoration(RecipeActivationRestorationState.NotRequired,
            "RecipeActivationHardwareUntouched");
        var terminal = await harness.Authorization.FinalizeRecipeActivationFailureAsync(command, RuntimeEpoch,
            admitted, admitted.Checks, restoration, RecipeActivationOutcomeState.Failed,
            "RecipeActivationInterruptedByRestart", new StoreDeadline(TimeSpan.FromSeconds(8)));
        Assert.True(terminal.Outcome.Disposition == CommandDisposition.Rejected, terminal.Outcome.ReasonCode);
        Assert.Equal("RecipeActivationInterruptedByRestart", terminal.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, terminal.Outcome.Audit);
        var failed = Assert.IsType<RecipeActivationRecord>(terminal.Record);
        AssertPlcActor(failed, request);
        Assert.Equal(RecipeActivationOutcomeState.Failed, failed.Outcome.State);
        Assert.Equal(admitted.Reference, failed.AdmissionReference);
        await harness.WaitForVerifiedAsync();

        // An independent read of the persisted schema-31 rows reconstructs the same
        // actor, request context and admission link from disk only.
        var history = new SqliteRecipeActivationQuery(harness.Options);
        var rereadAdmission = await history.ReadAsync(admitted.Reference);
        Assert.True(rereadAdmission.Available, rereadAdmission.ReasonCode);
        AssertPlcActor(rereadAdmission.Record!, request);
        var rereadTerminal = await history.ReadAsync(failed.Reference);
        Assert.True(rereadTerminal.Available, rereadTerminal.ReasonCode);
        AssertPlcActor(rereadTerminal.Record!, request);
        Assert.Equal(RecipeActivationOutcomeState.Failed, rereadTerminal.Record!.Outcome.State);
        Assert.Equal(admitted.Reference, rereadTerminal.Record.AdmissionReference);
        var current = await history.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.Null(current.PendingAdmission);
        Assert.False(current.RecoveryRequired);

        // The persisted identity rows are the closed 50-field PLC envelope bound to the
        // exact request context, with every human field empty.
        var stationId = RecipeSelectionIntegrationSupport.StationId(harness.Options);
        var rows = (await RecipeSelectionIntegrationSupport.ReadIdentityRowsAsync(harness.Options)).Where(value =>
            IdentityAuditEvent.TryReadEventKind(value.Payload, out var kind) &&
            kind is IdentityEventKind.RecipeActivationAdmitted or IdentityEventKind.RecipeActivationFailed).ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            var fields = RecipeSelectionIntegrationSupport.ReadIdentityPayloadFields(row.Payload);
            Assert.Equal(50, fields.Length);
            Assert.Null(fields[5]);
            Assert.Null(fields[25]);
            Assert.Null(fields[30]);
            Assert.Null(fields[32]);
            Assert.Null(fields[33]);
            Assert.Null(fields[34]);
            Assert.Equal("0", fields[35]);
            Assert.Equal("ActivateRecipe", fields[39]);
            Assert.True(IdentityAuditEvent.VerifyPayload(row.Payload, row.Ordinal, stationId,
                RecipeSelectionStoreOptions.SchemaVersion) > 0);
            var context = PlcRecipeActivationIdentityCodec.Decode(fields[49]!);
            Assert.Equal(request.RequestIdentityHash, context.RequestIdentityHash);
            var record = IdentityAuditEvent.TryReadEventKind(row.Payload, out var rowKind) &&
                rowKind == IdentityEventKind.RecipeActivationAdmitted ? admitted : failed;
            Assert.True(IdentityAuditEvent.MatchesRecipeActivationAuthorization(row.Payload, row.Ordinal,
                stationId, record, RecipeSelectionStoreOptions.SchemaVersion));
        }

        // The fixture reservation never touched a physical phase, published a terminal
        // state or committed a record; only the durable writer ran.
        Assert.Equal(0, terminalPublications);
        Assert.Equal(0, committedRecords);
        Assert.Equal(0, releases);
        lease.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task V146_P02_UnconsumedSupersededRevokedAndUnobservedRequestsWriteNothing()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var released = harness.Released;
        var entry = RecipeSelectionIntegrationSupport.Entry(1, released);

        var first = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("1", entry), null,
            "V146 first mapped PLC selection");
        Assert.True(first.Outcome.Disposition == CommandDisposition.Accepted, first.Outcome.ReasonCode);
        var firstRevision = Assert.IsType<RecipeSelectionRevision>(first.Revision);

        var observed = Request(firstRevision, sequence: 12);
        var observation = await harness.Store.AppendRecipeChangeEventAsync(observed,
            RecipeChangeEventKind.RequestObserved, null, null, "RecipeChangeRequestObserved", null,
            new StoreDeadline(TimeSpan.FromSeconds(8)), CancellationToken.None);
        Assert.True(observation.Committed, observation.ReasonCode);

        var second = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("2", entry), firstRevision.Reference,
            "V146 republish the same mapping as a new immutable revision");
        Assert.True(second.Outcome.Disposition == CommandDisposition.Accepted, second.Outcome.ReasonCode);
        var secondRevision = Assert.IsType<RecipeSelectionRevision>(second.Revision);
        await harness.WaitForVerifiedAsync();

        var unobserved = Request(secondRevision, sequence: 13);
        var terminalPublications = 0;
        var releases = 0;
        var committedRecords = 0;
        var lease = new RecipeActivationRuntimeLease(RuntimeEpoch, null, CancellationToken.None,
            publishTerminal: (_, _) => terminalPublications++,
            release: () => releases++,
            committed: _ => committedRecords++);
        var activationRows = await RecipeSelectionIntegrationSupport.ScalarAsync(harness.Options,
            "SELECT COUNT(*) FROM recipe_activation_events;");
        var identityRows = (await RecipeSelectionIntegrationSupport.ReadIdentityRowsAsync(harness.Options)).Count;

        // (1) An unconsumed capability is never an activation authority.
        var unconsumed = new PlcRecipeActivationCapability(observed, firstRevision, lease, null);
        var rejected = await AdmitAsync(harness, Command(observed), unconsumed);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeChangeCapabilityInvalid", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Record);

        // (2) A selection superseded after observation is no longer the current one.
        var superseded = new PlcRecipeActivationCapability(observed, firstRevision, lease, null);
        Assert.True(superseded.TryConsume(Command(observed)));
        rejected = await AdmitAsync(harness, Command(observed), superseded);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeChangeSelectionChanged", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Record);

        // (3) A reservation cancelled between observation and commit is cancelled, and
        // the cancelled lease is still released exactly once.
        using var cancelledLifetime = new CancellationTokenSource();
        var cancelledReleases = 0;
        var cancelledLease = new RecipeActivationRuntimeLease(RuntimeEpoch, null, cancelledLifetime.Token,
            release: () => cancelledReleases++);
        var cancelled = new PlcRecipeActivationCapability(observed, firstRevision, cancelledLease, null);
        Assert.True(cancelled.TryConsume(Command(observed)));
        cancelledLifetime.Cancel();
        rejected = await AdmitAsync(harness, Command(observed), cancelled);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeActivationCancelled", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Record);
        cancelledLease.Dispose();
        Assert.Equal(1, cancelledReleases);

        // (4) An explicitly revoked capability is invalid, not an admitted activation.
        var revoked = new PlcRecipeActivationCapability(observed, firstRevision, lease, null);
        Assert.True(revoked.TryConsume(Command(observed)));
        revoked.Revoke();
        rejected = await AdmitAsync(harness, Command(observed), revoked);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeActivationCancelled", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Record);

        // (5) A request without its persisted RequestObserved fact is not admitted.
        var unobservedHistory = await harness.RecipeChangeHistory.QueryAsync(new RecipeChangeHistoryFilter(
            RequestIdentityHash: unobserved.RequestIdentityHash));
        Assert.True(unobservedHistory.Available, unobservedHistory.ReasonCode);
        Assert.Empty(unobservedHistory.Events);
        var unobservedCapability = new PlcRecipeActivationCapability(unobserved, secondRevision, lease, null);
        Assert.True(unobservedCapability.TryConsume(Command(unobserved)));
        rejected = await AdmitAsync(harness, Command(unobserved), unobservedCapability);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeChangeRequestNotAdmitted", rejected.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.NotAttempted, rejected.Outcome.Audit);
        Assert.Null(rejected.Record);

        // (6) The default Local Operator Only deployment cannot mint PLC capability
        // evidence at all, and a default request has no mapped activation context.
        var defaultRequest = new RecipeChangeRequestEvidence(RuntimeEpoch, new string('E', 64),
            new RecipeContractReference("V146.Protocol", "1", new string('F', 64)), 3, 14, 1, null,
            RecipeSelectionPolicy.Default.Reference, null, null, DateTimeOffset.UtcNow);
        Assert.Equal("RecipeChangeCapabilityBindingInvalid", Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new PlcRecipeActivationCapability(defaultRequest, firstRevision, lease, null);
        }).Message);
        Assert.Equal("RecipeChangeMappedTargetRequired",
            Assert.Throws<InvalidOperationException>(() => defaultRequest.ActivationContext()).Message);

        // No rejected attempt wrote an activation record, an identity row or a physical
        // terminal state, and the fixture reservation was never released by the writer.
        Assert.Equal(activationRows, await RecipeSelectionIntegrationSupport.ScalarAsync(harness.Options,
            "SELECT COUNT(*) FROM recipe_activation_events;"));
        Assert.Equal(identityRows, (await RecipeSelectionIntegrationSupport.ReadIdentityRowsAsync(harness.Options)).Count);
        Assert.Equal(0, terminalPublications);
        Assert.Equal(0, committedRecords);
        Assert.Equal(0, releases);
        lease.Dispose();
        Assert.Equal(1, releases);
        var current = await harness.RecipeSelections.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(secondRevision.ContentHash, current.Revision!.ContentHash);
    }

    private static ValueTask<RecipeActivationAdmissionDecision> AdmitAsync(
        RecipeActivationServiceTests.ActivationHarness harness, ActivateRecipeCommand command,
        PlcRecipeActivationCapability capability) =>
        harness.Authorization.AdmitRecipeActivationAsync(command, RuntimeEpoch, Guid.NewGuid(),
            RecipeActivationEvidenceKind.LocalAuthority, Array.Empty<RecipeActivationCheck>(), null,
            () => null, new StoreDeadline(TimeSpan.FromSeconds(8)), CancellationToken.None, plc: capability);

    private static RecipeChangeRequestEvidence Request(RecipeSelectionRevision selection, uint sequence = 11)
    {
        var target = Assert.Single(selection.Map!.Entries);
        return new RecipeChangeRequestEvidence(RuntimeEpoch, new string('E', 64),
            new RecipeContractReference("V146.Protocol", "1", new string('F', 64)), controllerEpoch: 3,
            requestSequence: sequence, selectionCode: target.SelectionCode,
            selectionRevision: selection.Reference, selectionPolicy: selection.Policy.Reference,
            selectionMap: selection.Map.Reference, target: target, observedAtUtc: DateTimeOffset.UtcNow);
    }

    private static ActivateRecipeCommand Command(RecipeChangeRequestEvidence request) =>
        new(request.OperationId,
            new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, null, null),
            request.ActivationContext(), null, ChangeReason, request.OperationId);

    private static void AssertPlcActor(RecipeActivationRecord record, RecipeChangeRequestEvidence request)
    {
        Assert.True(record.Actor is { IsPlcAdapter: true },
            "the durable record actor must be the fixed PLC adapter");
        Assert.Equal(SystemPrincipalId.PlcAdapter, record.Actor!.SystemPrincipalId);
        Assert.Null(record.Actor.HumanPrincipalId);
        Assert.Null(record.Actor.HumanSessionId);
        Assert.Null(record.Actor.HumanAuthorizationRevision);
        Assert.Null(record.ActorPrincipalId);
        Assert.Null(record.ActorSessionId);
        Assert.Null(record.ActorAuthorizationRevision);
        var context = record.Actor.PlcRequestContext;
        Assert.NotNull(context);
        Assert.Equal(request.RequestIdentityHash, context!.RequestIdentityHash);
        Assert.Equal(request.OperationId, record.OperationId);
        Assert.Equal(request.Target!.Recipe, context.Candidate);
        Assert.Equal(request.Target.ReleaseId, context.ReleaseId);
        Assert.Equal(request.Target.ReleaseRecordContentHash, context.ReleaseRecordContentHash);
    }
}
