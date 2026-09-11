using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Harness = SharpInspect.Runtime.Tests.RecipeActivationServiceTests.ActivationHarness;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V148 negative checks for the exact signed lifecycle authority and the as-of
/// lifecycle authority replay. Every case first writes one real transition through the
/// production lifecycle service, then rewrites only that transition's signed evidence
/// with the fixture's real machine audit key, exactly as a compromised or erroneous
/// trusted writer could. The cold read must then reject the history for the semantic
/// reason, never for a merely stale outer hash. The rewrite helper is private to this
/// test assembly and adds no production API; these checks claim no physical acceptance.
/// The file is a partial of the existing manual-inspection fixture only to reuse its
/// production harness and arming helpers instead of duplicating them.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V148_S18")]
    public async Task V148_S18_ForeignCommandFactIsRejectedByExactCommandBinding()
    {
        await using var harness = await Harness.CreateAsync(enableRecipeLifecycle: true);
        var record = await AuthorityReplayRetireNonactiveAsync(harness);
        var foreign = AuthorityReplayReadForeignCommandFact(harness.Options);

        AuthorityReplayRewriteEvidence(harness.Options, record.Position, rebind: row => row with
        {
            CommandEventId = foreign.EventId,
            CommandAuditSequence = foreign.Sequence,
            CommandAuditHash = foreign.Hash
        });

        // The rewritten transition stays fully signed and reshaped to the foreign fact;
        // only the exact command binding can reject it.
        var stored = Assert.Single(AuthorityReplayReadStoredRows(harness.Options));
        Assert.Equal(foreign.EventId, stored.CommandEventId);
        Assert.Equal(foreign.Sequence, stored.CommandAuditSequence);
        var page = await new SqliteRecipeLifecycleQuery(harness.Options).QueryAsync(new());
        Assert.False(page.Available, page.ReasonCode);
        Assert.Equal("RecipeLifecycleCommandBindingMismatch", page.ReasonCode);
        Assert.Empty(page.Records);
    }

    [Fact]
    [Trait("VerificationId", "V148_S19")]
    public async Task V148_S19_ForeignSignedAuthorizationEventIsRejectedByExactIdentityBinding()
    {
        await using var harness = await Harness.CreateAsync(enableRecipeLifecycle: true);
        var record = await AuthorityReplayRetireNonactiveAsync(harness);
        var before = Assert.Single(AuthorityReplayReadStoredRows(harness.Options));
        var foreign = AuthorityReplayReadIdentityEvent(harness.Options, IdentityEventKind.RecipeReleased);
        Assert.NotEqual(before.AuthorizationEventId, foreign.EventId);

        AuthorityReplayRewriteEvidence(harness.Options, record.Position, rebind: row => row with
        {
            AuthorizationEventId = foreign.EventId,
            AuthorizationAuditSequence = foreign.Sequence,
            AuthorizationAuditHash = foreign.Hash
        });

        // The foreign event is a genuinely signed transition of this station, so the
        // rejection must come from re-deriving the exact authorization fields.
        var stored = Assert.Single(AuthorityReplayReadStoredRows(harness.Options));
        Assert.Equal(foreign.EventId, stored.AuthorizationEventId);
        var read = await new SqliteRecipeLifecycleQuery(harness.Options).ReadReleaseAsync(record.Recipe!,
            record.ReleaseId!.Value, record.ReleaseRecordContentHash!);
        Assert.False(read.Available, read.ReasonCode);
        Assert.Equal("RecipeLifecycleAuthorizationBindingMismatch", read.ReasonCode);
        Assert.Null(read.Transition);
    }

    [Fact]
    [Trait("VerificationId", "V148_S20")]
    public async Task V148_S20_AuthorizationReferenceMustResolveAtItsRecordedAuditSequence()
    {
        await using var harness = await Harness.CreateAsync(enableRecipeLifecycle: true);
        var record = await AuthorityReplayRetireNonactiveAsync(harness);
        var foreign = AuthorityReplayReadIdentityEvent(harness.Options, IdentityEventKind.RecipeReleased);

        // The event identity stays the recorded one; only the sequence/hash columns move
        // to another signed position. Replay must resolve the recorded sequence instead
        // of falling back to the latest identity event.
        AuthorityReplayRewriteEvidence(harness.Options, record.Position, rebind: row => row with
        {
            AuthorizationAuditSequence = foreign.Sequence,
            AuthorizationAuditHash = foreign.Hash
        });

        var stored = Assert.Single(AuthorityReplayReadStoredRows(harness.Options));
        Assert.Equal(foreign.Sequence, stored.AuthorizationAuditSequence);
        Assert.Equal(foreign.Hash, stored.AuthorizationAuditHash);
        var page = await new SqliteRecipeLifecycleQuery(harness.Options).QueryAsync(new());
        Assert.False(page.Available, page.ReasonCode);
        Assert.Equal("RecipeLifecycleAuthorizationAuditMissing", page.ReasonCode);
        Assert.Empty(page.Records);
    }

    [Fact]
    [Trait("VerificationId", "V148_S21")]
    public async Task V148_S21_OmittedMapImpactIsRejectedByAsOfSelectionReplay()
    {
        await using var harness = await Harness.CreateAsync(enableRecipeSelections: true,
            enableRecipeLifecycle: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var released = harness.Released;
        var change = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"),
            RecipeSelectionIntegrationSupport.Map("1", RecipeSelectionIntegrationSupport.Entry(1, released)),
            null, "V148 map the exact release before retirement");
        Assert.Equal(CommandDisposition.Accepted, change.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, change.Outcome.Audit);
        await harness.WaitForVerifiedAsync();
        var record = await AuthorityReplayRetireNonactiveAsync(harness);
        Assert.Equal(new uint[] { 1 }, Assert.Single(record.MapImpacts).AffectedCodes);

        // The signed transition keeps the exact release identity but drops the map
        // impact evidence that the as-of selection revision still requires.
        AuthorityReplayRewriteEvidence(harness.Options, record.Position,
            mutateRecord: AuthorityReplayWithoutMapImpacts);

        var stored = Assert.Single(AuthorityReplayReadStoredRows(harness.Options));
        Assert.Empty(stored.Record.MapImpacts);
        var read = await new SqliteRecipeLifecycleQuery(harness.Options).ReadReleaseAsync(released.Reference,
            released.Record.ReleaseId, released.Record.ContentHash);
        Assert.False(read.Available, read.ReasonCode);
        Assert.Equal("RecipeLifecycleMapImpactIncomplete", read.ReasonCode);
        Assert.Null(read.Transition);
    }

    [Fact]
    [Trait("VerificationId", "V148_S22")]
    public async Task V148_S22_OmittedObservedActiveIsRejectedByAsOfEffectiveCurrentReplay()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var retirement = await harness.Service<IRecipeLifecycleService>().RetireAsync(
            await RetirementCommandAsync(harness, active));
        AssertAccepted(retirement.Outcome, "V148 signed active retirement before authority replay");
        var record = Assert.IsType<RecipeLifecycleRecord>(retirement.Record);
        Assert.Equal(active.Reference, record.ObservedActive);
        Assert.Equal(active.Reference, record.ClearedActive);
        await harness.Fixture.WaitForVerifiedAsync();

        // The signed transition is complete but omits the effective Active it cleared;
        // only the as-of effective-current replay may reject it.
        AuthorityReplayRewriteEvidence(harness.Fixture.Options, record.Position,
            mutateRecord: AuthorityReplayWithoutObservedActive);

        var stored = Assert.Single(AuthorityReplayReadStoredRows(harness.Fixture.Options));
        Assert.Null(stored.Record.ObservedActive);
        Assert.Null(stored.Record.ClearedActive);
        var page = await new SqliteRecipeLifecycleQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.False(page.Available, page.ReasonCode);
        Assert.Equal("RecipeLifecycleObservedActiveMismatch", page.ReasonCode);
        Assert.Empty(page.Records);
    }

    /// <summary>Writes one real, fully signed non-active retirement through the production service.</summary>
    private static async Task<RecipeLifecycleRecord> AuthorityReplayRetireNonactiveAsync(Harness harness)
    {
        var release = harness.Released.Record;
        var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            release.Recipe, release.ReleaseId, release.ContentHash, null, "V148 authority replay fixture");
        var grant = await harness.IssueGrantAsync(Permission.RetireRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RetireReleasedRecipe);
        var result = await harness.RecipeLifecycle.RetireAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.False(result.RuntimeRecoveryRequired, result.CleanupReasonCode);
        await harness.WaitForVerifiedAsync();
        return Assert.IsType<RecipeLifecycleRecord>(result.Record);
    }

    private static RecipeLifecycleRecord AuthorityReplayWithoutObservedActive(RecipeLifecycleRecord value) =>
        new(value.Position, value.TransitionId, value.OperationId, value.RuntimeEpoch, value.Kind, value.PreviousHash,
            value.SourceDraft, value.SourceContentHash, value.Recipe, value.ReleaseId, value.ReleaseRecordContentHash,
            null, null, value.MapImpacts, value.ActorPrincipalId, value.ActorSessionId,
            value.ActorAuthorizationRevision, value.StepUpGrantId, value.AuthorizationPolicy,
            value.AuthorizationTarget, value.Reason, value.RecordedAtUtc);

    private static RecipeLifecycleRecord AuthorityReplayWithoutMapImpacts(RecipeLifecycleRecord value) =>
        new(value.Position, value.TransitionId, value.OperationId, value.RuntimeEpoch, value.Kind, value.PreviousHash,
            value.SourceDraft, value.SourceContentHash, value.Recipe, value.ReleaseId, value.ReleaseRecordContentHash,
            value.ObservedActive, value.ClearedActive, Array.Empty<RecipeRetirementMapImpact>(),
            value.ActorPrincipalId, value.ActorSessionId, value.ActorAuthorizationRevision, value.StepUpGrantId,
            value.AuthorizationPolicy, value.AuthorizationTarget, value.Reason, value.RecordedAtUtc);

    /// <summary>Reads every stored lifecycle row through the ordinary verified row reader.</summary>
    private static List<SqliteCommandStore.RecipeLifecycleStoredRow> AuthorityReplayReadStoredRows(
        ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadRecipeLifecycleRows(connection.Handle!, options.RecipeLifecycle!,
            AuthorityReplayDeadline);
    }

    private static (Guid EventId, long Sequence, string Hash) AuthorityReplayReadForeignCommandFact(
        ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var database = connection.Handle!;
        var facts = AuditChainDatabase.Read(database, @"SELECT EventId,Position,CommandKind FROM command_facts
            ORDER BY Position;", AuthorityReplayDeadline,
            value => (EventId: Guid.Parse(SqliteNative.ColumnText(value, 0)!),
                Position: SqliteNative.ColumnInt64(value, 1), Kind: SqliteNative.ColumnInt64(value, 2)));
        var fact = facts.First(value => value.Kind != (long)AuditedCommandKind.RetireReleasedRecipe);
        var audits = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", AuthorityReplayDeadline,
            value => (Sequence: SqliteNative.ColumnInt64(value, 0), Hash: SqliteNative.ColumnText(value, 1)!),
            fact.Position.ToString(CultureInfo.InvariantCulture));
        var audit = Assert.Single(audits);
        return (fact.EventId, audit.Sequence, audit.Hash);
    }

    private static (Guid EventId, long Sequence, string Hash) AuthorityReplayReadIdentityEvent(
        ProductionStoreOptions options, IdentityEventKind expectedKind)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var database = connection.Handle!;
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' ORDER BY Sequence;", AuthorityReplayDeadline,
            value => (Sequence: SqliteNative.ColumnInt64(value, 0),
                Payload: SqliteNative.ColumnText(value, 1)!, Hash: SqliteNative.ColumnText(value, 2)!));
        foreach (var row in rows)
        {
            var payload = Convert.FromBase64String(row.Payload);
            if (IdentityAuditEvent.TryReadEventKind(payload, out var kind) && kind == expectedKind &&
                IdentityAuditEvent.DecodeEventId(payload) is { } text &&
                Guid.TryParseExact(text, "D", out var eventId))
                return (eventId, row.Sequence, row.Hash);
        }
        throw new InvalidOperationException("V148AuthorityReplayFixtureIdentityEventMissing:" + expectedKind);
    }

    private static StoreDeadline AuthorityReplayDeadline => new(TimeSpan.FromSeconds(15));

    /// <summary>
    /// Test-only trusted-writer simulation. It rewrites one stored lifecycle transition
    /// and re-appends its signed central-audit metadata entry with the fixture's real
    /// machine audit key, so the resulting history is internally signed yet semantically
    /// invalid. This is never a production capability and never replaces a production
    /// signer: the helper lives in the test assembly, is private, and only re-signs the
    /// tail transition it just edited. The cold read's guard re-verifies the complete
    /// signed central chain before any business projection, so the asserted semantic
    /// reason also proves the rewritten evidence still verifies as signed.
    /// </summary>
    private static void AuthorityReplayRewriteEvidence(ProductionStoreOptions options, long position,
        Func<RecipeLifecycleRecord, RecipeLifecycleRecord>? mutateRecord = null,
        Func<SqliteCommandStore.RecipeLifecycleStoredRow, SqliteCommandStore.RecipeLifecycleStoredRow>? rebind = null)
    {
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var lifecycle = options.RecipeLifecycle ??
            throw new InvalidOperationException("RecipeLifecycleConfigurationRequired");
        var deadline = AuthorityReplayDeadline;
        using var key = WindowsMachineAuditKey.Open(policy, allowCreation: false, out _);
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: false);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        var committed = false;
        try
        {
            var triggers = AuditChainDatabase.Read(database, @"SELECT name,sql FROM sqlite_master WHERE
                type='trigger' AND tbl_name IN ('recipe_lifecycle_events','audit_entries','audit_checkpoints');",
                deadline, value => (Name: SqliteNative.ColumnText(value, 0)!,
                    Sql: SqliteNative.ColumnText(value, 1)!));
            foreach (var trigger in triggers)
                SqliteNative.Execute(database, "DROP TRIGGER \"" + trigger.Name.Replace("\"", "\"\"") + "\";", deadline);
            try
            {
                var original = SqliteCommandStore.ReadRecipeLifecycleRows(database, lifecycle, deadline)
                    .Single(value => value.Record.Position == position);
                var tail = AuditChainDatabase.Tail(database, deadline);
                if (tail.Sequence != original.AuditSequence)
                    throw new InvalidOperationException("V148AuthorityReplayFixtureSignedTailRequired:" +
                        original.AuditSequence.ToString(CultureInfo.InvariantCulture) + "/" +
                        tail.Sequence.ToString(CultureInfo.InvariantCulture));
                var record = mutateRecord is null ? original.Record : mutateRecord(original.Record);
                var payload = RecipeLifecycleStorageCodec.Encode(record);
                var row = original with
                {
                    Record = record,
                    Payload = payload,
                    PayloadHash = Convert.ToHexString(SHA256.HashData(payload))
                };
                if (rebind is not null) row = rebind(row);
                AuditChainDatabase.Execute(database, "DELETE FROM audit_entries WHERE Sequence=?;", deadline,
                    original.AuditSequence.ToString(CultureInfo.InvariantCulture));
                AuditChainDatabase.Execute(database, "DELETE FROM audit_checkpoints WHERE Sequence>=?;", deadline,
                    original.AuditSequence.ToString(CultureInfo.InvariantCulture));
                AuditChainDatabase.Execute(database, @"UPDATE recipe_lifecycle_events SET
                    RecordContentHash=?,PayloadHash=?,Payload=?,CommandEventId=?,CommandAuditSequence=?,
                    CommandAuditHash=?,AuthorizationEventId=?,AuthorizationAuditSequence=?,AuthorizationAuditHash=?
                    WHERE Position=?;", deadline,
                    row.Record.ContentHash, row.PayloadHash, Convert.ToBase64String(row.Payload),
                    row.CommandEventId.ToString("D"), row.CommandAuditSequence.ToString(CultureInfo.InvariantCulture),
                    row.CommandAuditHash, row.AuthorizationEventId.ToString("D"),
                    row.AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture), row.AuthorizationAuditHash,
                    position.ToString(CultureInfo.InvariantCulture));
                var metadata = AuditChainDatabase.AppendRecipeLifecycleMetadata(database, policy, key,
                    SqliteCommandStore.RecipeLifecycleEventAuditKind, SqliteCommandStore.EncodeAuditBinding(row),
                    lifecycle, deadline);
                AuditChainDatabase.Execute(database,
                    "UPDATE recipe_lifecycle_events SET AuditSequence=?,AuditHash=? WHERE Position=?;", deadline,
                    metadata.Sequence.ToString(CultureInfo.InvariantCulture), metadata.Hash,
                    position.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                foreach (var trigger in triggers) SqliteNative.Execute(database, trigger.Sql, deadline);
            }
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                try { SqliteNative.Execute(database, "ROLLBACK;", new StoreDeadline(TimeSpan.FromSeconds(2))); }
                catch (Exception) { /* The original failure is the meaningful one. */ }
            }
        }
    }
}
