using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>Real schema-22 transactions with test-only qualifying evidence. Passing
/// gate logic never substitutes for the separately checked human authorization.</summary>
public sealed partial class ProductionAdmissionArmIntegrationTests
{
    [Theory]
    [InlineData("StepUpRequired")]
    [InlineData("PhysicalConsoleRequired")]
    [InlineData("ProductionAdmissionCancelled")]
    public async Task V136_I01_AllPassedGatesCannotBypassCurrentAuthorization(string rejection)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(requireStepUp: true);
        using var qualification = new ProductionAdmissionTestFixture();
        var virtualFacts = qualification.Facts();
        // This deliberately carries no successful durable-head claim. These three
        // attempts must be rejected by authorization before admission CAS is relevant.
        var facts = new ProductionAdmissionFacts(virtualFacts.Configuration,
            virtualFacts.Qualifications, virtualFacts.RuntimeGates.Values.ToArray(),
            new Dictionary<string, string>());
        var report = qualification.Evaluate(facts);
        Assert.True(report.CanArm);
        var command = station.Command();
        if (rejection == "PhysicalConsoleRequired")
            command = command with { Invocation = command.Invocation with { Source = CommandSource.Integration } };
        using var cancellation = new CancellationTokenSource();
        if (rejection == "ProductionAdmissionCancelled") cancellation.Cancel();

        var outcome = await station.Authorization.HandleProductionArmAsync(command,
            report.RuntimeEpoch, Guid.NewGuid(), report.AdmissionGeneration, facts, report,
            null, new StoreDeadline(TimeSpan.FromSeconds(3)), cancellation.Token);

        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal(rejection, outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        Assert.Equal(report.ContentHash, outcome.ProductionAdmission!.ContentHash);
        await station.WaitVerifiedAsync();
        await AssertColdRejectionAsync(station, command, outcome, report);
    }

    [Fact]
    public async Task V136_I02_AuthorizedReportIsAtomicallyAdmittedAndFinalizedWithImmutableBindings()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(requireStepUp: true);
        var materialChanges = 0;
        station.Store.ProductionAdmissionMaterialChanging += () => Interlocked.Increment(ref materialChanges);
        using var qualification = new ProductionAdmissionTestFixture();
        var command = await station.WithStepUpAsync(station.Command());
        var facts = await CaptureFactsAsync(station, qualification);
        var report = qualification.Evaluate(facts);
        var outcome = await AuthorizeAsync(station, command, facts, report);
        Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        await station.WaitVerifiedAsync();

        var pending = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(pending.Available, pending.ReasonCode);
        var admitted = Assert.IsType<ProductionAdmissionHistoryEvent>(pending.Latest);
        Assert.Equal(ProductionAdmissionEventKind.Admitted, admitted.Kind);
        Assert.True(pending.RecoveryRequired);
        Assert.Equal(command.Invocation.StepUpGrantId, admitted.StepUpGrantId);
        Assert.Equal(facts.DurableHeads.OrderBy(pair => pair.Key),
            admitted.CurrentDurableHeads.OrderBy(pair => pair.Key));

        var writer = Assert.IsAssignableFrom<IProductionAdmissionTerminalWriter>(station.Store);
        var completed = await writer.CompleteProductionAdmissionAsync(command.CorrelationId,
            outcome.AttemptId!.Value, report.RuntimeEpoch, report.AdmissionGeneration,
            "ProductionAdmissionFinalized", new StoreDeadline(TimeSpan.FromSeconds(3)));
        Assert.True(completed.Committed, completed.ReasonCode);
        await station.WaitVerifiedAsync();
        var page = await new SqliteProductionAdmissionHistoryQuery(station.Options)
            .QueryAsync(new ProductionAdmissionHistoryFilter(CorrelationId: command.CorrelationId));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(admitted.ContentHash, page.Events[0].ContentHash);
        var terminal = page.Events[1];
        Assert.Equal(ProductionAdmissionEventKind.Completed, terminal.Kind);
        Assert.Equal("ProductionAdmissionFinalized", terminal.ReasonCode);
        Assert.Equal(admitted.Report.ContentHash, terminal.Report.ContentHash);
        Assert.Equal(admitted.AuthorizationTarget, terminal.AuthorizationTarget);
        Assert.Equal(admitted.ActorPrincipalId, terminal.ActorPrincipalId);
        Assert.Equal(admitted.ActorSessionId, terminal.ActorSessionId);
        Assert.Equal(admitted.StepUpGrantId, terminal.StepUpGrantId);
        Assert.Null(page.Pending);
        Assert.False(page.RecoveryRequired);

        var trace = await station.Trace.QueryAsync(new CommandTraceFilter(CorrelationId: command.CorrelationId));
        Assert.Equal(2, trace.Records.Count);
        Assert.All(trace.Records, row => Assert.Equal(CommandSource.PhysicalConsole, row.Source));
        Assert.Contains(trace.Records, row => row.Phase == CommandAuditPhase.Outcome &&
            row.Disposition == CommandDisposition.Accepted);
        Assert.Contains(trace.Records, row => row.Phase == CommandAuditPhase.Completed &&
            row.ReasonCode == "ProductionAdmissionFinalized");
        var integrity = await new SqliteAuditIntegrityQuery(station.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 100));
        Assert.True(integrity.State == AuditIntegrityState.Verified, integrity.ReasonCode);
        Assert.Equal(0, materialChanges);
    }

    [Fact]
    public async Task V136_I03_StaleDurableHeadsRollbackTheAttemptAndReleaseItsStepUpGrant()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(requireStepUp: true);
        using var qualification = new ProductionAdmissionTestFixture();
        var command = await station.WithStepUpAsync(station.Command());
        var original = await CaptureFactsAsync(station, qualification);
        var staleHeads = original.DurableHeads.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.NotEmpty(staleHeads);
        staleHeads[staleHeads.Keys.First()] = ProductionAdmissionTestFixture.Hash("obsolete durable head");
        var stale = new ProductionAdmissionFacts(original.Configuration, original.Qualifications,
            original.RuntimeGates.Values.ToArray(), staleHeads);
        var before = await station.Store.ReadIdentityAsync(CancellationToken.None);

        var denied = await AuthorizeAsync(station, command, stale, qualification.Evaluate(stale));

        Assert.Equal(CommandDisposition.Rejected, denied.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, denied.Audit);
        Assert.StartsWith("ProductionAdmission", denied.ReasonCode);
        var after = await station.Store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.LastIdentityAuditHash, after.LastIdentityAuditHash);
        var rejectedHistory = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(rejectedHistory.Available, rejectedHistory.ReasonCode);
        Assert.Null(rejectedHistory.Latest);
        var failedTrace = await station.Trace.QueryAsync(new CommandTraceFilter(CorrelationId: command.CorrelationId));
        Assert.Empty(failedTrace.Records);

        // The same bound grant remains usable only because the entire first
        // transaction rolled back. No reauthentication or new correlation is used.
        var current = await CaptureFactsAsync(station, qualification);
        var report = qualification.Evaluate(current);
        var accepted = await AuthorizeAsync(station, command, current, report);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        Assert.Equal(AuditPersistence.Persisted, accepted.Audit);
        await station.WaitVerifiedAsync();
        var terminal = await ((IProductionAdmissionTerminalWriter)station.Store)
            .CompleteProductionAdmissionAsync(command.CorrelationId, accepted.AttemptId!.Value,
                report.RuntimeEpoch, report.AdmissionGeneration, "ProductionAdmissionFinalized",
                new StoreDeadline(TimeSpan.FromSeconds(3)));
        Assert.True(terminal.Committed, terminal.ReasonCode);
    }

    [Fact]
    public async Task V136_I04_AdmissionReservesItsTerminalBeforeConsumingIdentityOrCommandCapacity()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(
            requireStepUp: true, maximumEntries: 1);
        using var qualification = new ProductionAdmissionTestFixture();
        var command = await station.WithStepUpAsync(station.Command());
        var before = await station.Store.ReadIdentityAsync(CancellationToken.None);
        var facts = await CaptureFactsAsync(station, qualification);
        var report = qualification.Evaluate(facts);

        var outcome = await AuthorizeAsync(station, command, facts, report);

        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("ProductionAdmissionEntryCapacityExceeded", outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        var after = await station.Store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.LastIdentityAuditHash, after.LastIdentityAuditHash);
        var page = await station.History.QueryAsync(new ProductionAdmissionHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
        var trace = await station.Trace.QueryAsync(new CommandTraceFilter(CorrelationId: command.CorrelationId));
        Assert.Empty(trace.Records);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("generation")]
    [InlineData("attempt")]
    public async Task V136_I05_TerminalCannotCompleteAnotherAdmissionContext(string changed)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        using var qualification = new ProductionAdmissionTestFixture();
        var command = station.Command();
        var facts = await CaptureFactsAsync(station, qualification);
        var report = qualification.Evaluate(facts);
        var admitted = await AuthorizeAsync(station, command, facts, report);
        Assert.Equal(CommandDisposition.Accepted, admitted.Disposition);
        await station.WaitVerifiedAsync();
        var writer = Assert.IsAssignableFrom<IProductionAdmissionTerminalWriter>(station.Store);

        var changedResult = await writer.CompleteProductionAdmissionAsync(command.CorrelationId,
            changed == "attempt" ? Guid.NewGuid() : admitted.AttemptId!.Value,
            changed == "epoch" ? Guid.NewGuid() : report.RuntimeEpoch,
            changed == "generation" ? report.AdmissionGeneration + 1 : report.AdmissionGeneration,
            "ProductionAdmissionFinalized", new StoreDeadline(TimeSpan.FromSeconds(3)));

        Assert.False(changedResult.Committed);
        var pending = await station.History.QueryAsync(
            new ProductionAdmissionHistoryFilter(CorrelationId: command.CorrelationId));
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.Single(pending.Events);
        Assert.Equal(ProductionAdmissionEventKind.Admitted, pending.Pending!.Kind);
        var completed = await writer.CompleteProductionAdmissionAsync(command.CorrelationId,
            admitted.AttemptId!.Value, report.RuntimeEpoch, report.AdmissionGeneration,
            "ProductionAdmissionChanged", new StoreDeadline(TimeSpan.FromSeconds(3)));
        Assert.True(completed.Committed, completed.ReasonCode);
        await station.WaitVerifiedAsync();
        var failed = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(failed.Available, failed.ReasonCode);
        Assert.Equal(ProductionAdmissionEventKind.Failed, failed.Latest!.Kind);
        Assert.False(failed.RecoveryRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V136_I06_ColdHistoryRejectsCursorsBeyondTheDurableTail(bool futureThrough)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        var query = new SqliteProductionAdmissionHistoryQuery(station.Options);
        var filter = futureThrough
            ? new ProductionAdmissionHistoryFilter(ThroughPosition: 1)
            : new ProductionAdmissionHistoryFilter(AfterPosition: 1);

        var page = await query.QueryAsync(filter);

        Assert.False(page.Available);
        Assert.Equal("ProductionAdmissionCursorInvalid", page.ReasonCode);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task V136_I07_DefaultFactsSourceKeepsReadFailureBlockedUntilANewVerifiedCapture()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        var runtime = station.Runtime;
        var initial = await runtime.RefreshProductionAdmissionAsync();
        Assert.DoesNotContain(initial!.Gates, gate => gate.ReasonCode == "ProductionAdmissionFactsUnavailable");
        // Deliberate corruption of this isolated test database's schema makes the
        // actual verified reader fail. Restoring the exact schema permits a new
        // observation; this does not reset a fault latched by the audit monitor.
        using var connection = SqliteNative.Open(station.Options.DatabasePath!, false);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA user_version=999;", new StoreDeadline(TimeSpan.FromSeconds(3)));
        ProductionAdmissionReport? failed;
        try
        {
            failed = await runtime.RefreshProductionAdmissionAsync();
            Assert.All(failed!.Gates.Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate.Gate)),
                gate => Assert.Equal("ProductionAdmissionFactsUnavailable", gate.ReasonCode));
        }
        finally
        {
            SqliteNative.Execute(database, "PRAGMA user_version=22;", new StoreDeadline(TimeSpan.FromSeconds(3)));
        }
        // Merely restoring availability and publishing heartbeats cannot clear
        // the failed observation; only another successful verified read may do so.
        await Task.Delay(70);
        var blocked = await runtime.GetSnapshotAsync();
        Assert.All(blocked.ProductionAdmission!.Gates.Where(gate =>
                !ProductionAdmissionEngine.IsQualificationGate(gate.Gate)),
            gate => Assert.Equal("ProductionAdmissionFactsUnavailable", gate.ReasonCode));
        var recovered = await runtime.RefreshProductionAdmissionAsync();
        Assert.DoesNotContain(recovered!.Gates, gate => gate.ReasonCode == "ProductionAdmissionFactsUnavailable");
        Assert.False(recovered.CanArm);
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
    }

    private static async Task<ProductionAdmissionFacts> CaptureFactsAsync(
        ProductionAdmissionArmFixture station, ProductionAdmissionTestFixture qualification)
    {
        var source = qualification.Facts();
        var heads = await station.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        return new ProductionAdmissionFacts(source.Configuration, source.Qualifications,
            source.RuntimeGates.Values.ToArray(), heads);
    }

    private static ValueTask<RuntimeCommandOutcome> AuthorizeAsync(ProductionAdmissionArmFixture station,
        ArmProductionCommand command, ProductionAdmissionFacts facts, ProductionAdmissionReport report) =>
        station.Authorization.HandleProductionArmAsync(command, report.RuntimeEpoch, Guid.NewGuid(),
            report.AdmissionGeneration, facts, report, null,
            new StoreDeadline(TimeSpan.FromSeconds(3)), CancellationToken.None);

    private static async Task AssertColdRejectionAsync(ProductionAdmissionArmFixture station,
        ArmProductionCommand command, RuntimeCommandOutcome outcome, ProductionAdmissionReport report)
    {
        // This reader opens its own read-only SQLite connection and key handle.
        var history = new SqliteProductionAdmissionHistoryQuery(station.Options);
        var read = await history.ReadAsync(command.CorrelationId);
        Assert.True(read.Available, read.ReasonCode);
        var entry = Assert.IsType<ProductionAdmissionHistoryEvent>(read.Latest);
        Assert.Equal(ProductionAdmissionEventKind.Rejected, entry.Kind);
        Assert.Equal(outcome.ReasonCode, entry.ReasonCode);
        Assert.Equal(outcome.AttemptId, entry.AttemptId);
        Assert.Equal(station.PrincipalId, entry.ActorPrincipalId);
        Assert.Equal(station.SessionId, entry.ActorSessionId);
        Assert.Equal(report.ContentHash, entry.Report.ContentHash);
        Assert.Equal(24, entry.Report.Gates.Count);
        Assert.False(read.RecoveryRequired);
        Assert.NotNull(entry.CommandAuditHash);
        Assert.NotNull(entry.AuthorizationAuditHash);
        Assert.NotNull(entry.AuditHash);
        var registered = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(registered.Available, registered.ReasonCode);
        Assert.Equal(entry.ContentHash, registered.Latest!.ContentHash);

        var trace = await station.Trace.QueryAsync(new CommandTraceFilter(CorrelationId: command.CorrelationId));
        var fact = Assert.Single(trace.Records);
        Assert.Equal(AuditedCommandKind.ArmProduction, fact.CommandKind);
        Assert.Equal(CommandAuditPhase.Outcome, fact.Phase);
        Assert.Equal(CommandDisposition.Rejected, fact.Disposition);
        Assert.Equal(entry.AttemptId, fact.AttemptId);
        Assert.Equal(station.PrincipalId.ToString("D"), fact.AuthenticatedHumanPrincipalId);
        var verified = await new SqliteAuditIntegrityQuery(station.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 100));
        Assert.True(verified.State == AuditIntegrityState.Verified, verified.ReasonCode);
    }
}
