using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T53 governed-operation integration checks. Every case drives the real writer transaction
/// through the actual StationRuntime recovery surface (or the real worker/store boundary) on a
/// fresh schema-37 store: identity, session, fresh Step-Up, state compare-and-swap, the immutable
/// recovery/correction rows and the cold re-verified projections are all exercised together.
/// These tests use the Windows machine-key store profile and therefore run in the real-user
/// ticket validation context; they contain no dynamic environment skip.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V153_T04")]
    public async Task V153_T04_GovernedRecoveryKeepsIdentityBytesAndBoundsCumulativeAttempts()
    {
        using var diagnostics = new OutboxPersistenceFailureCapture();
        var recovery = new ProductionOutboxRecoveryOptions
        {
            MaximumGrantedAttempts = 2,
            MaximumCumulativeGrantedAttempts = 8,
            MaximumRecoveryOperations = 4,
            MaximumCorrections = 4
        };
        await using var fixture = await CreateRecoveryFixtureAsync(recovery, maximumAttempts: 2);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        var originalBytes = delivery.Payload!.CopyBytes();
        var originalContentHash = delivery.ContentHash;
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var blocked = await PendingItemAsync(fixture, delivery.DeliveryId);
        Assert.True(blocked.PermanentBlock);
        Assert.False(blocked.RetryEligible);
        Assert.Equal(1, blocked.AttemptCount);
        Assert.Equal(0L, OutboxFutureReserve(fixture));

        // A full-width Chinese reason survives the command, the store CHECK and the read model.
        var reason = new string('修', 250) + "恢复原投递";
        var command = new RecoverOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(),
            delivery.DeliveryId, blocked.Delivery.ContentHash, blocked.StateRevisionHash, 2, reason);
        Assert.Equal(reason, command.Reason);
        var authorized = await AuthorizeAsync(fixture, command);
        var commandFacts = AuditKindCount(fixture, "CommandFact");
        var identityEvents = AuditKindCount(fixture, "IdentityEvent");
        var createdFacts = AuditKindCount(fixture, "ProductionOutboxCreated");
        await using (var runtime = CreateGovernedRuntime(fixture))
        {
            var outcome = await runtime.SubmitAsync(authorized);
            Assert.True(outcome.Disposition == CommandDisposition.Accepted,
                outcome.ReasonCode + "\n" + diagnostics.Read());
            Assert.Equal("OutboxRecoveryAuthorized", outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        }
        // Recovery appends exactly the command fact and the signed authorization event; it
        // never creates a delivery fact and never touches the original bytes.
        Assert.Equal(commandFacts + 1, AuditKindCount(fixture, "CommandFact"));
        Assert.Equal(identityEvents + 1, AuditKindCount(fixture, "IdentityEvent"));
        Assert.Equal(createdFacts, AuditKindCount(fixture, "ProductionOutboxCreated"));

        var recovered = await PendingItemAsync(fixture, delivery.DeliveryId);
        Assert.False(recovered.PermanentBlock);
        Assert.True(recovered.RetryEligible);
        Assert.Equal(delivery.DeliveryId, recovered.Delivery.DeliveryId);
        Assert.Equal(originalContentHash, recovered.Delivery.ContentHash);
        Assert.Equal(originalBytes, recovered.Delivery.Payload!.CopyBytes());
        Assert.Equal(1, recovered.AttemptCount);
        Assert.NotEqual(blocked.StateRevisionHash, recovered.StateRevisionHash);
        Assert.Equal(2L * 2, OutboxFutureReserve(fixture));

        // Two fresh attempts are allowed on the same identity; the third is refused by the
        // cumulative allowance, and every attempt fact keeps the original frozen budget.
        var attempt2 = Guid.NewGuid();
        var epoch2 = Guid.NewGuid();
        var started2 = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt2, 2,
            epoch2, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V153 recovery attempt 2")), fixture.Deadline());
        Assert.True(started2.Committed, started2.ReasonCode);
        var failed2At = DateTimeOffset.UtcNow;
        var failed2 = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId,
            attempt2, epoch2, failed2At, "V153RecoveryAttemptTransient",
            OutboxFailureCategory.Transient, failed2At.AddMilliseconds(1)), fixture.Deadline());
        Assert.True(failed2.Committed, failed2.ReasonCode);
        var attempt3 = Guid.NewGuid();
        var epoch3 = Guid.NewGuid();
        var started3 = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt3, 3,
            epoch3, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V153 recovery attempt 3")), fixture.Deadline());
        Assert.True(started3.Committed, started3.ReasonCode);
        var failed3At = DateTimeOffset.UtcNow;
        var failed3 = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId,
            attempt3, epoch3, failed3At, "V153RecoveryAttemptTransient",
            OutboxFailureCategory.Transient, failed3At.AddMilliseconds(1)), fixture.Deadline());
        Assert.True(failed3.Committed, failed3.ReasonCode);
        Assert.Equal(0L, OutboxFutureReserve(fixture));
        var attempt4 = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, Guid.NewGuid(), 4,
            Guid.NewGuid(), DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V153 recovery attempt 4")),
            fixture.Deadline());
        Assert.False(attempt4.Committed);
        Assert.Equal("ProductionOutboxAttemptBudgetExhausted", attempt4.ReasonCode);

        var history = await fixture.Query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(new[] { 1, 2, 3 }, history.Events
            .Where(entry => entry.Kind == OutboxEventKind.AttemptStarted)
            .Select(entry => entry.AttemptNumber!.Value).ToArray());
        Assert.All(history.Events.Where(entry => entry.Kind != OutboxEventKind.Created),
            entry => Assert.Equal(2, entry.AttemptBudget!.Value));
        Assert.All(history.Events, entry => Assert.Equal(delivery.Payload!.ContentHash, entry.PayloadHash!));
        Assert.Equal(originalBytes, delivery.Payload!.CopyBytes());

        var governance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(delivery.DeliveryId);
        Assert.True(governance.Available, governance.Reason);
        var record = Assert.Single(governance.Recoveries);
        Assert.Equal(2, record.GrantedAttempts);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(Guid.Parse(fixture.Sessions.Current.PrincipalId!), record.ActorPrincipalId);
        Assert.Equal(64, record.ContentHash.Length);
        Assert.Empty(governance.Corrections);

        // Cold reopen re-verifies the exact schema-37 ledger, not an in-memory projection.
        await fixture.Store.DisposeAsync();
        await using var reopened = new SqliteCommandStore(fixture.Options);
        Assert.True((await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var coldPending = await new SqliteProductionOutboxQuery(fixture.Options).ReadPendingAsync();
        Assert.True(coldPending.Available, coldPending.ReasonCode);
        var coldItem = Assert.Single(coldPending.Items, item => item.Delivery.DeliveryId == delivery.DeliveryId);
        Assert.False(coldItem.PermanentBlock);
        Assert.Equal(3, coldItem.AttemptCount);
        Assert.Equal(originalBytes, coldItem.Delivery.Payload!.CopyBytes());
        var latestRevision = (await PendingItemAsync(fixture, delivery.DeliveryId)).StateRevisionHash;
        Assert.Equal(latestRevision, coldItem.StateRevisionHash);
        var coldHistory = await new SqliteProductionOutboxQuery(fixture.Options)
            .ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.Equal(history.Events.Select(entry => entry.ContentHash),
            coldHistory.Events.Select(entry => entry.ContentHash));
        var coldGovernance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(delivery.DeliveryId);
        Assert.True(coldGovernance.Available, coldGovernance.Reason);
        Assert.Equal(reason, Assert.Single(coldGovernance.Recoveries).Reason);
    }

    [Fact]
    [Trait("VerificationId", "V153_T05")]
    public async Task V153_T05_GovernanceRequiresFreshSingleUseStepUpBoundToTheExactCommand()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 2);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var blocked = await PendingItemAsync(fixture, delivery.DeliveryId);
        await using var runtime = CreateGovernedRuntime(fixture);

        RecoverOutboxDeliveryCommand NewCommand() => new(Guid.NewGuid(), fixture.Invocation(),
            delivery.DeliveryId, blocked.Delivery.ContentHash, blocked.StateRevisionHash, 1,
            "V153 fresh step-up binding");

        // Missing Step-Up is durably rejected and writes no operation row.
        var missing = await runtime.SubmitAsync(NewCommand());
        Assert.Equal(CommandDisposition.Rejected, missing.Disposition);
        Assert.Equal("StepUpRequired", missing.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, missing.Audit);
        Assert.Equal(0L, RecoveryRowCount(fixture));

        // A fresh grant for a different command binding never transfers to this command.
        var foreignGrant = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
            fixture.Invocation(), new StepUpBinding(Permission.RecoverOutboxDelivery, Guid.NewGuid(),
                delivery.DeliveryId.ToString("D"), AuditedCommandKind.RecoverOutboxDelivery),
            fixture.Password));
        Assert.True(foreignGrant.Succeeded, foreignGrant.ReasonCode);
        var foreign = await runtime.SubmitAsync(NewCommand() with
        {
            Invocation = fixture.Invocation() with { StepUpGrantId = foreignGrant.GrantId }
        });
        Assert.Equal(CommandDisposition.Rejected, foreign.Disposition);
        Assert.Equal("StepUpInvalid", foreign.ReasonCode);
        Assert.Equal(0L, RecoveryRowCount(fixture));

        // The exact fresh grant authorizes exactly one operation.
        var authorized = await AuthorizeAsync(fixture, NewCommand());
        var accepted = await runtime.SubmitAsync(authorized);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        Assert.Equal("OutboxRecoveryAuthorized", accepted.ReasonCode);
        Assert.Equal(1L, RecoveryRowCount(fixture));

        // The consumed grant cannot authorize a new correlation or a stale selection.
        var replay = await runtime.SubmitAsync(NewCommand() with
        {
            Invocation = authorized.Invocation
        });
        Assert.Equal(CommandDisposition.Rejected, replay.Disposition);
        Assert.Equal("StepUpInvalid", replay.ReasonCode);
        Assert.Equal(1L, RecoveryRowCount(fixture));
    }

    [Fact]
    [Trait("VerificationId", "V153_T06")]
    public async Task V153_T06_StaleStateRevisionIsRejectedAgainstTheFreshWriterState()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 2);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var stale = await PendingItemAsync(fixture, delivery.DeliveryId);
        await using var runtime = CreateGovernedRuntime(fixture);
        var first = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
            fixture.Invocation(), delivery.DeliveryId, stale.Delivery.ContentHash, stale.StateRevisionHash,
            1, "V153 first governed recovery"));
        Assert.Equal(CommandDisposition.Accepted, (await runtime.SubmitAsync(first)).Disposition);
        var fresh = await PendingItemAsync(fixture, delivery.DeliveryId);
        Assert.NotEqual(stale.StateRevisionHash, fresh.StateRevisionHash);

        // The fresh Step-Up of a stale selection cannot beat the transaction-local CAS.
        var staleCommand = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
            fixture.Invocation(), delivery.DeliveryId, stale.Delivery.ContentHash, stale.StateRevisionHash,
            1, "V153 stale governed recovery"));
        var rejected = await runtime.SubmitAsync(staleCommand);
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("OutboxRecoveryStateChanged", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal(1L, RecoveryRowCount(fixture));
        Assert.Equal(fresh.StateRevisionHash,
            (await PendingItemAsync(fixture, delivery.DeliveryId)).StateRevisionHash);
    }

    [Fact]
    [Trait("VerificationId", "V153_T07")]
    public async Task V153_T07_CorrectiveDeliveryLinksItsSourceAndLeavesTheOriginalTerminalStateUntouched()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 1);
        await fixture.CommitAsync();
        var source = fixture.Batch.Deliveries[0];
        var sourceBytes = source.Payload!.CopyBytes();
        await PermanentlyBlockAsync(fixture, source.DeliveryId);
        var blocked = await PendingItemAsync(fixture, source.DeliveryId);
        var sourceHistory = await fixture.Query.ReadHistoryAsync(deliveryId: source.DeliveryId);
        var corrected = Encoding.UTF8.GetBytes("{\"kind\":\"corrected\",\"revision\":2}");
        var command = new CreateCorrectiveOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(),
            source.DeliveryId, blocked.Delivery.ContentHash, blocked.StateRevisionHash,
            source.Route.ContentType, corrected, "更正报文以反映已修复的接收端约束");
        var authorized = await AuthorizeAsync(fixture, command);
        var commandFacts = AuditKindCount(fixture, "CommandFact");
        var identityEvents = AuditKindCount(fixture, "IdentityEvent");
        var createdFacts = AuditKindCount(fixture, "ProductionOutboxCreated");
        await using (var runtime = CreateGovernedRuntime(fixture))
        {
            var outcome = await runtime.SubmitAsync(authorized);
            Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
            Assert.Equal("OutboxCorrectionCreated", outcome.ReasonCode);
        }
        // The correction transaction is one command fact, one signed authorization event and
        // exactly one new frozen delivery fact.
        Assert.Equal(commandFacts + 1, AuditKindCount(fixture, "CommandFact"));
        Assert.Equal(identityEvents + 1, AuditKindCount(fixture, "IdentityEvent"));
        Assert.Equal(createdFacts + 1, AuditKindCount(fixture, "ProductionOutboxCreated"));

        var governance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(source.DeliveryId);
        Assert.True(governance.Available, governance.Reason);
        var link = Assert.Single(governance.Corrections);
        Assert.Equal(source.DeliveryId, link.SourceDeliveryId);
        Assert.NotEqual(source.DeliveryId, link.DeliveryId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(corrected)), link.PayloadHash);
        Assert.Empty(governance.Recoveries);

        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(2, page.Items.Count);
        var corrective = Assert.Single(page.Items, item => item.Delivery.DeliveryId == link.DeliveryId);
        Assert.Equal(corrected, corrective.Delivery.Payload!.CopyBytes());
        Assert.Equal(source.InspectionId, corrective.Delivery.InspectionId);
        Assert.Equal(fixture.Core.ContentHash, corrective.Delivery.CoreHash);
        Assert.Equal(source.Route.ContentHash, corrective.Delivery.Route.ContentHash);
        Assert.False(corrective.PermanentBlock);
        Assert.True(corrective.RetryEligible);

        // The original terminal state, bytes and attempt history are untouched.
        var original = Assert.Single(page.Items, item => item.Delivery.DeliveryId == source.DeliveryId);
        Assert.True(original.PermanentBlock);
        Assert.False(original.RetryEligible);
        Assert.Equal(1, original.AttemptCount);
        Assert.Equal(sourceBytes, original.Delivery.Payload!.CopyBytes());
        Assert.Equal(source.ContentHash, original.Delivery.ContentHash);
        var sourceAfter = await fixture.Query.ReadHistoryAsync(deliveryId: source.DeliveryId);
        Assert.Equal(sourceHistory.Events.Select(entry => entry.ContentHash),
            sourceAfter.Events.Select(entry => entry.ContentHash));

        // Cold reopen keeps the explicit final bytes and the immutable source link.
        await fixture.Store.DisposeAsync();
        await using var reopened = new SqliteCommandStore(fixture.Options);
        Assert.True((await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var coldGovernance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(source.DeliveryId);
        Assert.True(coldGovernance.Available, coldGovernance.Reason);
        Assert.Equal(link, Assert.Single(coldGovernance.Corrections));
        var coldPage = await new SqliteProductionOutboxQuery(fixture.Options).ReadPendingAsync();
        Assert.True(coldPage.Available, coldPage.ReasonCode);
        var coldCorrective = Assert.Single(coldPage.Items, item => item.Delivery.DeliveryId == link.DeliveryId);
        Assert.Equal(corrected, coldCorrective.Delivery.Payload!.CopyBytes());
    }

    [Fact]
    [Trait("VerificationId", "V153_T08")]
    public async Task V153_T08_UnsendablePreparationFailureCannotRecoverButCorrectsWithExplicitBytes()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 5,
            criticality: OutboxRouteCriticality.BestEffort, routeMaximumPayload: 16);
        await fixture.CommitAsync();
        var source = fixture.Batch.Deliveries[0];
        Assert.Null(source.Payload);
        var pending = await PendingItemAsync(fixture, source.DeliveryId);
        Assert.True(pending.PermanentBlock);
        Assert.False(pending.RetryEligible);

        await using (var runtime = CreateGovernedRuntime(fixture))
        {
            // No payload may ever be recovered: the frozen obligation has no bytes to send.
            var recovery = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
                fixture.Invocation(), source.DeliveryId, pending.Delivery.ContentHash,
                pending.StateRevisionHash, 1, "V153 unsendable recovery"));
            var rejected = await runtime.SubmitAsync(recovery);
            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal("OutboxRecoveryUnsendableObligation", rejected.ReasonCode);
            Assert.Equal(0L, RecoveryRowCount(fixture));

            // The corrective route may freeze explicit final bytes for a new linked delivery.
            var corrected = Encoding.UTF8.GetBytes("{\"v\":2}");
            var corrective = await AuthorizeAsync(fixture, new CreateCorrectiveOutboxDeliveryCommand(
                Guid.NewGuid(), fixture.Invocation(), source.DeliveryId, pending.Delivery.ContentHash,
                pending.StateRevisionHash, source.Route.ContentType, corrected,
                "V153 explicit final bytes for an unsendable source"));
            var accepted = await runtime.SubmitAsync(corrective);
            Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
            Assert.Equal("OutboxCorrectionCreated", accepted.ReasonCode);
        }

        var governance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(source.DeliveryId);
        Assert.True(governance.Available, governance.Reason);
        var link = Assert.Single(governance.Corrections);
        Assert.Empty(governance.Recoveries);
        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        var correctiveItem = Assert.Single(page.Items, item => item.Delivery.DeliveryId == link.DeliveryId);
        Assert.NotNull(correctiveItem.Delivery.Payload);
        Assert.Equal(Encoding.UTF8.GetBytes("{\"v\":2}"), correctiveItem.Delivery.Payload!.CopyBytes());
        var original = Assert.Single(page.Items, item => item.Delivery.DeliveryId == source.DeliveryId);
        Assert.Null(original.Delivery.Payload);
        Assert.True(original.PermanentBlock);
        Assert.Equal(source.ContentHash, original.Delivery.ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V153_T09")]
    public async Task V153_T09_RecoveryIsRefusedWhileTheObligationIsFreshActiveOrSucceeded()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 2);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await using var runtime = CreateGovernedRuntime(fixture);

        // A fresh retry-eligible obligation has nothing to recover.
        var fresh = await PendingItemAsync(fixture, delivery.DeliveryId);
        var notBlocked = await runtime.SubmitAsync(await AuthorizeAsync(fixture,
            new RecoverOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(), delivery.DeliveryId,
                fresh.Delivery.ContentHash, fresh.StateRevisionHash, 1, "V153 fresh obligation")));
        Assert.Equal(CommandDisposition.Rejected, notBlocked.Disposition);
        Assert.Equal("OutboxRecoveryNotBlocked", notBlocked.ReasonCode);

        // An unresolved attempt must be resolved before any allowance is added.
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1, epoch,
            DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V153 active obligation")), fixture.Deadline());
        Assert.True(started.Committed, started.ReasonCode);
        var active = await PendingItemAsync(fixture, delivery.DeliveryId);
        Assert.Equal(attempt, active.ActiveAttemptId);
        var whileActive = await runtime.SubmitAsync(await AuthorizeAsync(fixture,
            new RecoverOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(), delivery.DeliveryId,
                active.Delivery.ContentHash, active.StateRevisionHash, 1, "V153 active obligation")));
        Assert.Equal(CommandDisposition.Rejected, whileActive.Disposition);
        Assert.Equal("OutboxRecoveryAttemptActive", whileActive.ReasonCode);

        // The authenticated receiver acceptance closes the attempt as the one terminal success.
        using var authority = new OutboxAttemptAuthority(TimeSpan.FromMinutes(1), CancellationToken.None);
        var claim = OutboxAcceptanceVerifier.CreateClaim(delivery, attempt, epoch,
            fixture.Receiver.Accept(delivery), authority);
        var succeeded = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxSuccessRequest(
            delivery.DeliveryId, epoch, DateTimeOffset.UtcNow, claim), fixture.Deadline());
        Assert.True(succeeded.Committed, succeeded.ReasonCode);
        var history = await fixture.Query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.Single(history.Events, entry => entry.Kind == OutboxEventKind.Succeeded);
        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        Assert.DoesNotContain(page.Items, item => item.Delivery.DeliveryId == delivery.DeliveryId);

        var lastHash = history.Events[^1].ContentHash;
        var succeededRevision = SqliteCommandStore.ProductionOutboxStateRevision(delivery, lastHash,
            Array.Empty<SqliteCommandStore.ProductionOutboxRecoveryStoredRow>(),
            Array.Empty<SqliteCommandStore.ProductionOutboxCorrectionStoredRow>());
        var refused = await runtime.SubmitAsync(await AuthorizeAsync(fixture,
            new RecoverOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(), delivery.DeliveryId,
                delivery.ContentHash, succeededRevision, 1, "V153 succeeded obligation")));
        Assert.Equal(CommandDisposition.Rejected, refused.Disposition);
        Assert.Equal("OutboxRecoveryAlreadySucceeded", refused.ReasonCode);
        Assert.Equal(0L, RecoveryRowCount(fixture));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("granted")]
    [Trait("VerificationId", "V153_T10")]
    public async Task V153_T10_GovernanceFactsAreTamperEvidentAgainstDeletionAndEdit(string tamper)
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 2);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var blocked = await PendingItemAsync(fixture, delivery.DeliveryId);
        await using (var runtime = CreateGovernedRuntime(fixture))
        {
            var accepted = await runtime.SubmitAsync(await AuthorizeAsync(fixture,
                new RecoverOutboxDeliveryCommand(Guid.NewGuid(), fixture.Invocation(), delivery.DeliveryId,
                    blocked.Delivery.ContentHash, blocked.StateRevisionHash, 1, "V153 tamper baseline")));
            Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        }
        Assert.True((await fixture.Query.ReadPendingAsync()).Available);
        Assert.True((await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(delivery.DeliveryId)).Available);

        var statement = tamper == "delete"
            ? "DELETE FROM production_outbox_recovery;"
            : "UPDATE production_outbox_recovery SET GrantedAttempts=GrantedAttempts+1;";
        var trigger = "production_outbox_recovery_immutable_" + (tamper == "delete" ? "delete" : "update");
        await TamperGovernanceRowAsync(fixture.Options.DatabasePath, trigger, statement);

        var page = await fixture.Query.ReadPendingAsync();
        Assert.False(page.Available);
        Assert.Empty(page.Items);
        var governance = await new SqliteProductionOutboxGovernanceQuery(fixture.Options)
            .ReadAsync(delivery.DeliveryId);
        Assert.False(governance.Available);
    }

    [Fact]
    [Trait("VerificationId", "V153_T11")]
    public async Task V153_T11_MissingHandlerRefusesRecoveryWithoutConsumingTheBoundGrant()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 1);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var blocked = await PendingItemAsync(fixture, delivery.DeliveryId);
        var command = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
            fixture.Invocation(), delivery.DeliveryId, delivery.ContentHash, blocked.StateRevisionHash,
            1, "V153 restore the historical handler"));
        await using (var missingRuntime = CreateGovernedRuntime(fixture, registerHandler: false))
        {
            var rejected = await missingRuntime.SubmitAsync(command);
            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal("OutboxRequiredHistoricalHandlerMissing", rejected.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        }
        Assert.Equal(0L, RecoveryRowCount(fixture));
        Assert.Equal(0L, OutboxFutureReserve(fixture));
        Assert.Equal(blocked.StateRevisionHash,
            (await PendingItemAsync(fixture, delivery.DeliveryId)).StateRevisionHash);
        // Restoring the registry alone leaves the persistent block; the same still-unused,
        // exact-target grant now authorizes the bounded recovery against unchanged state.
        await using var restoredRuntime = CreateGovernedRuntime(fixture);
        Assert.True((await PendingItemAsync(fixture, delivery.DeliveryId)).PermanentBlock);
        var accepted = await restoredRuntime.SubmitAsync(command);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        Assert.Equal(1L, RecoveryRowCount(fixture));
        Assert.Equal(delivery.Payload!.CopyBytes(),
            (await PendingItemAsync(fixture, delivery.DeliveryId)).Delivery.Payload!.CopyBytes());
    }

    [Fact]
    [Trait("VerificationId", "V153_T12")]
    public async Task V153_T12_RecoveryReservesEveryFutureFactAndRejectsCapacityWithoutMutation()
    {
        await using var fixture = await CreateRecoveryFixtureAsync(maximumAttempts: 1, maximumEvents: 6);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        var blocked = await PendingItemAsync(fixture, delivery.DeliveryId);
        await using var runtime = CreateGovernedRuntime(fixture);
        var tooLarge = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
            fixture.Invocation(), delivery.DeliveryId, delivery.ContentHash, blocked.StateRevisionHash,
            2, "V153 insufficient future event capacity"));
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var rejected = await runtime.SubmitAsync(tooLarge);
            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal("ProductionOutboxEntryCapacityExceeded", rejected.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
            Assert.Equal(0L, RecoveryRowCount(fixture));
            Assert.Equal(0L, OutboxFutureReserve(fixture));
            Assert.Equal(blocked.StateRevisionHash,
                (await PendingItemAsync(fixture, delivery.DeliveryId)).StateRevisionHash);
        }
        var exact = await AuthorizeAsync(fixture, new RecoverOutboxDeliveryCommand(Guid.NewGuid(),
            fixture.Invocation(), delivery.DeliveryId, delivery.ContentHash, blocked.StateRevisionHash,
            1, "V153 exact future event capacity"));
        var accepted = await runtime.SubmitAsync(exact);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        var storedRows = fixture.Scalar("SELECT (SELECT COUNT(*) FROM production_outbox_deliveries) + " +
            "(SELECT COUNT(*) FROM production_outbox_events);");
        Assert.Equal(6L, storedRows + OutboxFutureReserve(fixture));
    }

    private static async Task<OutboxCoreFixture> CreateRecoveryFixtureAsync(
        ProductionOutboxRecoveryOptions? recovery = null, int maximumAttempts = 5,
        OutboxRouteCriticality criticality = OutboxRouteCriticality.Required,
        int routeMaximumPayload = 1024 * 1024, int maximumEvents = 20_000) =>
        await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
        {
            MaximumAttempts = maximumAttempts,
            MaximumEvents = maximumEvents,
            ManualRecovery = recovery ?? new ProductionOutboxRecoveryOptions()
        }, criticality: criticality, routeMaximumPayload: routeMaximumPayload,
            grantOutboxGovernance: true);

    /// <summary>
    /// The real public command surface: a StationRuntime whose recovery service reads its own
    /// fresh station snapshot, so the writer transaction receives the exact runtime epoch.
    /// </summary>
    private static StationRuntime CreateGovernedRuntime(OutboxCoreFixture fixture, bool registerHandler = true)
    {
        var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20),
            fixture.Sessions, fixture.Authorization);
        runtime.ConfigureOutboxRecoveryService(new ProductionOutboxRecoveryService(fixture.Store,
            fixture.Authorization, fixture.Options, () => runtime.GetSnapshotAsync(),
            registerHandler
                ? new ProductionOutboxOptions(new[] { fixture.Receiver.Binding(new RuntimeRejectedOutboxTransport()) })
                : null));
        return runtime;
    }

    private static async Task<StepUpResult> GrantAsync(OutboxCoreFixture fixture, Permission permission,
        Guid correlation, string target, AuditedCommandKind kind)
    {
        var result = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
            fixture.Invocation(), new StepUpBinding(permission, correlation, target, kind),
            fixture.Password));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.NotNull(result.GrantId);
        return result;
    }

    private static async Task<RecoverOutboxDeliveryCommand> AuthorizeAsync(OutboxCoreFixture fixture,
        RecoverOutboxDeliveryCommand command)
    {
        var grant = await GrantAsync(fixture, Permission.RecoverOutboxDelivery, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RecoverOutboxDelivery);
        return command with { Invocation = fixture.Invocation() with { StepUpGrantId = grant.GrantId } };
    }

    private static async Task<CreateCorrectiveOutboxDeliveryCommand> AuthorizeAsync(OutboxCoreFixture fixture,
        CreateCorrectiveOutboxDeliveryCommand command)
    {
        var grant = await GrantAsync(fixture, Permission.CreateCorrectiveOutboxDelivery,
            command.CorrelationId, command.AuthorizationTarget,
            AuditedCommandKind.CreateCorrectiveOutboxDelivery);
        return command with { Invocation = fixture.Invocation() with { StepUpGrantId = grant.GrantId } };
    }

    private static async Task PermanentlyBlockAsync(OutboxCoreFixture fixture, Guid deliveryId)
    {
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(deliveryId, attempt, 1, epoch,
            DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V153 permanent block")), fixture.Deadline());
        Assert.True(started.Committed, started.ReasonCode);
        var failed = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(deliveryId,
            attempt, epoch, DateTimeOffset.UtcNow, "V153PermanentReceiverRejection",
            OutboxFailureCategory.Permanent, null), fixture.Deadline());
        Assert.True(failed.Committed, failed.ReasonCode);
    }

    private static async Task<OutboxPendingItem> PendingItemAsync(OutboxCoreFixture fixture, Guid deliveryId)
    {
        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        return Assert.Single(page.Items, item => item.Delivery.DeliveryId == deliveryId);
    }

    private static long RecoveryRowCount(OutboxCoreFixture fixture) =>
        fixture.Scalar("SELECT COUNT(*) FROM production_outbox_recovery;");

    private static long AuditKindCount(OutboxCoreFixture fixture, string kind) =>
        fixture.Scalar("SELECT COUNT(*) FROM audit_entries WHERE Kind='" + kind + "';");

    /// <summary>The exact future fact reserve the recovery grant adds (2N) and consumes.</summary>
    private static long OutboxFutureReserve(OutboxCoreFixture fixture)
    {
        using var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadProductionOutboxReserveRows(connection.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(5))).Events;
    }

    /// <summary>
    /// One bounded direct tamper: the immutability trigger is dropped only inside the same
    /// transaction that applies the edit and restores the exact stored trigger text.
    /// </summary>
    private static async Task TamperGovernanceRowAsync(string databasePath, string trigger, string statement)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name;";
        lookup.Parameters.AddWithValue("$name", trigger);
        var triggerSql = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE; DROP TRIGGER " + trigger + "; " + statement + " " +
            triggerSql + "; COMMIT;";
        await command.ExecuteNonQueryAsync();
    }
}
