using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using RecipeLifecycleStoredRow = SharpInspect.Runtime.Storage.SqliteCommandStore.RecipeLifecycleStoredRow;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T48 storage checks for the schema-33 Recipe lifecycle ledger. The codec cases
/// use complete immutable records in memory; the storage cases open a real SQLite
/// database, initialize only the declared tables, and tamper with that isolated database
/// offline. No Runtime, device, PLC, authorization or audit writer is started, no
/// lifecycle transition is claimed, and no authoritative row is written outside the
/// declared lifecycle table.
/// </summary>
public sealed class RecipeLifecycleStorageCodecTests
{
    private static readonly Guid TransitionId = Guid.Parse("6a000000-0000-0000-0000-000000000001");
    private static readonly Guid OperationId = Guid.Parse("7b000000-0000-0000-0000-000000000002");
    private static readonly Guid RuntimeEpoch = Guid.Parse("8c000000-0000-0000-0000-000000000003");
    private static readonly Guid DraftId = Guid.Parse("5d000000-0000-0000-0000-000000000004");
    private static readonly Guid ReleaseId = Guid.Parse("9e000000-0000-0000-0000-000000000005");
    private static readonly Guid ActivationId = Guid.Parse("1f000000-0000-0000-0000-000000000006");
    private static readonly Guid ActorPrincipalId = Guid.Parse("2a000000-0000-0000-0000-000000000007");
    private static readonly Guid ActorSessionId = Guid.Parse("3b000000-0000-0000-0000-000000000008");
    private static readonly Guid StepUpGrantId = Guid.Parse("4c000000-0000-0000-0000-000000000009");
    private static readonly Guid AuthorA = Guid.Parse("a1100000-0000-0000-0000-000000000001");
    private static readonly Guid AuthorB = Guid.Parse("b2200000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 12, 3, 30, 0, TimeSpan.Zero);
    private static readonly RecipeDraftContent LifecycleContent = Content(6);

    // The canonical payload layout is pinned so a tamper case can address one exact field:
    // magic(4) + format(1) + position(8) + transition/operation/epoch(48) => kind.
    private const int KindOffset = 61;
    // kind(1) + previous-hash presence(1) + draft id(16) + source revision(8) => first string length.
    private const int SourceRevisionHashLengthOffset = 87;

    [Fact]
    [Trait("VerificationId", "V148_S01")]
    public void V148_S01_LifecycleCodecRoundTripsBothKindsByteIdentically()
    {
        var abandonment = Abandonment();
        var selectionA = Guid.NewGuid();
        var selectionB = Guid.NewGuid();
        var retirement = Retirement(2, abandonment.ContentHash, observedActive: Activation(),
            clearedActive: Activation(), mapImpacts: new[]
            {
                Impact(300, 5, selectionB), Impact(5, 3, selectionA)
            });

        var abandonmentPayload = RecipeLifecycleStorageCodec.Encode(abandonment);
        var retirementPayload = RecipeLifecycleStorageCodec.Encode(retirement);
        var decodedAbandonment = RecipeLifecycleStorageCodec.Decode(abandonmentPayload);
        var decodedRetirement = RecipeLifecycleStorageCodec.Decode(retirementPayload);

        Assert.Equal(abandonmentPayload, RecipeLifecycleStorageCodec.Encode(decodedAbandonment));
        Assert.Equal(retirementPayload, RecipeLifecycleStorageCodec.Encode(decodedRetirement));
        Assert.Equal(abandonment.ContentHash, decodedAbandonment.ContentHash);
        Assert.Equal(abandonment.Reference, decodedAbandonment.Reference);
        Assert.Equal(RecipeLifecycleKind.DraftAbandoned, decodedAbandonment.Kind);
        Assert.Null(decodedAbandonment.PreviousHash);
        Assert.Null(decodedAbandonment.Recipe);
        Assert.Null(decodedAbandonment.ReleaseId);
        Assert.Null(decodedAbandonment.ReleaseRecordContentHash);
        Assert.Null(decodedAbandonment.ObservedActive);
        Assert.Empty(decodedAbandonment.MapImpacts);
        Assert.Equal(DraftReference(), decodedAbandonment.SourceDraft);
        Assert.Equal(Hash("source-content"), decodedAbandonment.SourceContentHash);
        Assert.Equal(RecordedAt, decodedAbandonment.RecordedAtUtc);

        Assert.Equal(retirement.ContentHash, decodedRetirement.ContentHash);
        Assert.Equal(retirement.Reference, decodedRetirement.Reference);
        Assert.Equal(RecipeLifecycleKind.ReleasedRetired, decodedRetirement.Kind);
        Assert.Equal(abandonment.ContentHash, decodedRetirement.PreviousHash);
        Assert.Equal(Recipe(), decodedRetirement.Recipe);
        Assert.Equal(ReleaseId, decodedRetirement.ReleaseId);
        Assert.Equal(Hash("release-record"), decodedRetirement.ReleaseRecordContentHash);
        Assert.Equal(Activation(), decodedRetirement.ObservedActive);
        Assert.Equal(decodedRetirement.ObservedActive, decodedRetirement.ClearedActive);
        Assert.Equal(2, decodedRetirement.MapImpacts.Count);
        Assert.Equal(new uint[] { 5 }, decodedRetirement.MapImpacts[0].AffectedCodes);
        Assert.Equal(new uint[] { 300 }, decodedRetirement.MapImpacts[1].AffectedCodes);
        Assert.Equal(selectionA, decodedRetirement.MapImpacts[0].Selection.RevisionId);
        Assert.Equal(retirement.MapImpacts.Select(value => value.ContentHash),
            decodedRetirement.MapImpacts.Select(value => value.ContentHash));
        Assert.Equal(TimeSpan.Zero, decodedRetirement.RecordedAtUtc.Offset);
        Assert.Equal(retirement.RecordedAtUtc, decodedRetirement.RecordedAtUtc);

        // A caller list never becomes ledger state and no partial evidence is accepted.
        Assert.Throws<ArgumentNullException>(() => RecipeLifecycleStorageCodec.Encode(null!));
        Assert.Throws<ArgumentException>(() => Retirement(2, abandonment.ContentHash,
            mapImpacts: new[] { Impact(5, 3, selectionA), Impact(6, 4, selectionA) }));
    }

    [Fact]
    [Trait("VerificationId", "V148_S02")]
    public void V148_S02_LifecycleCodecRejectsTrailingContentHashAndCapacityTamper()
    {
        var payload = RecipeLifecycleStorageCodec.Encode(Retirement(2, Hash("previous")));
        AssertPayloadRejected(payload[..^1]);
        AssertPayloadRejected(payload.Concat(new byte[] { 0x2A }).ToArray());

        // The embedded record content hash is never trusted on its own.
        var wrongHash = payload.ToArray();
        wrongHash[^1] = wrongHash[^1] == (byte)'A' ? (byte)'B' : (byte)'A';
        AssertPayloadRejected(wrongHash);

        // Empty, undersized and over-bound payloads fail before any field is projected.
        AssertReason("RecipeLifecyclePayloadCapacityExceeded",
            () => RecipeLifecycleStorageCodec.Decode(Array.Empty<byte>()));
        AssertReason("RecipeLifecyclePayloadCapacityExceeded", () => RecipeLifecycleStorageCodec.Decode(
            new byte[RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit + 1]));
        AssertReason("RecipeLifecyclePayloadCanonicalMismatch", () => RecipeLifecycleStorageCodec.Decode(
            NonCanonicalMapPayload()));
    }

    [Fact]
    [Trait("VerificationId", "V148_S03")]
    public void V148_S03_LifecycleCodecRejectsEveryTruncatedLength()
    {
        var payload = RecipeLifecycleStorageCodec.Encode(Retirement(2, Hash("previous"),
            observedActive: Activation(), clearedActive: Activation(), mapImpacts: new[] { Impact(5) }));
        for (var length = 0; length < payload.Length; length++)
            Assert.Throws<InvalidOperationException>(() =>
                RecipeLifecycleStorageCodec.Decode(payload.AsMemory(0, length)));
    }

    [Fact]
    [Trait("VerificationId", "V148_S04")]
    public void V148_S04_LifecycleCodecRejectsEverySingleByteTamper()
    {
        var payload = RecipeLifecycleStorageCodec.Encode(Abandonment());
        for (var index = 0; index < payload.Length; index++)
        {
            var tampered = payload.ToArray();
            tampered[index] ^= 0x01;
            Assert.Throws<InvalidOperationException>(() => RecipeLifecycleStorageCodec.Decode(tampered));
        }
    }

    [Fact]
    [Trait("VerificationId", "V148_S05")]
    public void V148_S05_LifecycleCodecRejectsUnknownKindFormatVersionAndOverlongString()
    {
        var payload = RecipeLifecycleStorageCodec.Encode(Abandonment());
        Assert.Equal((byte)RecipeLifecycleKind.DraftAbandoned, payload[KindOffset]);

        var unknownKind = payload.ToArray();
        unknownKind[KindOffset] = 3;
        AssertPayloadRejected(unknownKind);
        var undefinedKind = payload.ToArray();
        undefinedKind[KindOffset] = 0;
        AssertPayloadRejected(undefinedKind);

        // A foreign format version is never silently projected as version 1.
        var wrongFormat = payload.ToArray();
        wrongFormat[4] = 2;
        AssertPayloadRejected(wrongFormat);

        // One string field cannot exceed its canonical bound.
        var overlongString = payload.ToArray();
        BitConverter.GetBytes(5000).CopyTo(overlongString, SourceRevisionHashLengthOffset);
        AssertPayloadRejected(overlongString);
    }

    [Fact]
    [Trait("VerificationId", "V148_S06")]
    public void V148_S06_LifecycleCodecBindsEveryMapActorAndHashField()
    {
        var selectionA = Guid.NewGuid();
        var selectionB = Guid.NewGuid();
        var selectionC = Guid.NewGuid();
        var record = Retirement(2, Hash("previous"), observedActive: Activation(4), clearedActive: Activation(4),
            mapImpacts: new[] { Impact(5, 3, selectionA), Impact(20, 5, selectionB), Impact(300, 7, selectionC) });
        var decoded = RecipeLifecycleStorageCodec.Decode(RecipeLifecycleStorageCodec.Encode(record));

        Assert.Equal(3, decoded.MapImpacts.Count);
        Assert.Equal(new[] { selectionA, selectionB, selectionC },
            decoded.MapImpacts.Select(value => value.Selection.RevisionId));
        Assert.Equal(record.MapImpacts.Select(value => value.Map.ContentHash),
            decoded.MapImpacts.Select(value => value.Map.ContentHash));
        Assert.Equal(record.MapImpacts.SelectMany(value => value.AffectedCodes),
            decoded.MapImpacts.SelectMany(value => value.AffectedCodes));
        Assert.Equal(ActorPrincipalId, decoded.ActorPrincipalId);
        Assert.Equal(ActorSessionId, decoded.ActorSessionId);
        Assert.Equal(7, decoded.ActorAuthorizationRevision);
        Assert.Equal(StepUpGrantId, decoded.StepUpGrantId);
        Assert.Equal(AuthorizationPolicy(), decoded.AuthorizationPolicy);
        Assert.Equal(AuthorizationTarget(), decoded.AuthorizationTarget);
        Assert.All(new[] { decoded.ContentHash, decoded.SourceContentHash,
            decoded.SourceDraft.RevisionContentHash, decoded.ReleaseRecordContentHash!,
            decoded.AuthorizationPolicy.ContentHash }, value => Assert.Equal(64, value.Length));

        // Every attribution, evidence, identity and map field participates in the one
        // canonical record hash, so no field can be substituted without detection.
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: new[] { Impact(5, 3, selectionA),
                Impact(20, 5, selectionB), Impact(301, 7, selectionC) }).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("other-previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: record.MapImpacts).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(5),
            clearedActive: Activation(5), mapImpacts: record.MapImpacts).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: null, mapImpacts: record.MapImpacts).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: record.MapImpacts,
            actorPrincipalId: Guid.NewGuid()).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: record.MapImpacts,
            actorAuthorizationRevision: 8).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: record.MapImpacts,
            stepUpGrantId: Guid.NewGuid()).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), observedActive: Activation(4),
            clearedActive: Activation(4), mapImpacts: record.MapImpacts,
            authorizationTarget: Hash("other-target")).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), releaseId: Guid.NewGuid(),
            observedActive: Activation(4), clearedActive: Activation(4),
            mapImpacts: record.MapImpacts).ContentHash);
        Assert.NotEqual(record.ContentHash, Retirement(2, Hash("previous"), releaseRecordContentHash:
            Hash("other-release"), observedActive: Activation(4), clearedActive: Activation(4),
            mapImpacts: record.MapImpacts).ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V148_S07")]
    public void V148_S07_LifecycleValidationRequiresExactSourceReleaseActiveAndMapEvidence()
    {
        var draft = DraftRevision();
        var release = ReleaseRecord();
        var abandonment = Abandonment(sourceDraft: DraftReference(), sourceContentHash:
            LifecycleContent.ContentHash, recordedAtUtc: release.ReleasedAtUtc.AddSeconds(2));
        var retirement = Retirement(2, abandonment.ContentHash, recipe: release.Recipe,
            releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
            sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
            recordedAtUtc: release.ReleasedAtUtc.AddSeconds(3));
        var rows = new[] { Row(abandonment, 1), Row(retirement, 2) };
        var noActivations = Array.Empty<RecipeActivationRecord>();
        var noSelections = Array.Empty<RecipeSelectionRevision>();

        // The exact preserved source and release evidence validates.
        SqliteCommandStore.ValidateRecipeLifecycleHistory(rows, new[] { draft }, new[] { release },
            noActivations, noSelections);

        // Missing evidence is never silently skipped.
        AssertReason("RecipeLifecycleSourceDraftRevisionMissing", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(rows, Array.Empty<RecipeDraftRevision>(),
                new[] { release }, noActivations, noSelections));
        AssertReason("RecipeLifecycleReleaseRecordMissing", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(rows, new[] { draft },
                Array.Empty<RecipeReleaseRecord>(), noActivations, noSelections));

        // A retirement that observed and cleared an Active selection requires the
        // exact effective activation record, and a map impact requires the exact
        // immutable Selection revision and its exact remaining codes.
        var active = Retirement(2, abandonment.ContentHash, recipe: release.Recipe,
            releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
            sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
            observedActive: Activation(), clearedActive: Activation(),
            recordedAtUtc: release.ReleasedAtUtc.AddSeconds(3));
        var activeRows = new[] { Row(abandonment, 1), Row(active, 2) };
        AssertReason("RecipeLifecycleActiveActivationMissing", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(activeRows, new[] { draft }, new[] { release },
                noActivations, noSelections));

        var impact = new RecipeRetirementMapImpact(new RecipeSelectionReference(1, Guid.NewGuid(),
            Hash("selection-revision")), new RecipeContractReference("Lifecycle.Map", "1", Hash("map")),
            new uint[] { 5 });
        var mapped = Retirement(2, abandonment.ContentHash, recipe: release.Recipe,
            releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
            sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
            mapImpacts: new[] { impact }, recordedAtUtc: release.ReleasedAtUtc.AddSeconds(3));
        var mappedRows = new[] { Row(abandonment, 1), Row(mapped, 2) };
        AssertReason("RecipeLifecycleSelectionRevisionMissing", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(mappedRows, new[] { draft }, new[] { release },
                noActivations, noSelections));

        // The chain and audit-time invariants are re-derived from the exact records.
        AssertReason("RecipeLifecycleHistorySequenceInvalid", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(
                new[] { Row(abandonment, 1), Row(Retirement(3, abandonment.ContentHash, recipe: release.Recipe,
                    releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
                    sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
                    recordedAtUtc: release.ReleasedAtUtc.AddSeconds(3)), 3) },
                new[] { draft }, new[] { release }, noActivations, noSelections));
        AssertReason("RecipeLifecycleHistoryTimeReversed", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(
                new[]
                {
                    Row(abandonment, 1),
                    Row(Retirement(2, abandonment.ContentHash, recipe: release.Recipe,
                        releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
                        sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
                        recordedAtUtc: release.ReleasedAtUtc.AddSeconds(1)), 2)
                }, new[] { draft }, new[] { release }, noActivations, noSelections));
        var duplicateRetirement = Retirement(3, retirement.ContentHash, recipe: release.Recipe,
            releaseId: release.ReleaseId, releaseRecordContentHash: release.ContentHash,
            sourceDraft: DraftReference(), sourceContentHash: LifecycleContent.ContentHash,
            recordedAtUtc: release.ReleasedAtUtc.AddSeconds(4));
        AssertReason("RecipeLifecycleReleaseAlreadyRetired", () =>
            SqliteCommandStore.ValidateRecipeLifecycleHistory(rows.Concat(new[] { Row(duplicateRetirement, 3) })
                .ToArray(), new[] { draft }, new[] { release }, noActivations, noSelections));
    }

    [Fact]
    [Trait("VerificationId", "V148_S08")]
    public async Task V148_S08_LifecycleSchemaConfiguresImmutablyAndReadsVerifiedRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        var options = new RecipeLifecycleStoreOptions { MaximumPayloadBytes = 4096, MaxTotalBytes = 8192 };
        SqliteCommandStore.InitializeRecipeLifecycleTables(database, options, deadline);
        SqliteCommandStore.RequireConfiguredRecipeLifecycle(database, options, deadline);

        Assert.Equal(2, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'recipe_lifecycle_%';"));
        Assert.Empty(SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));

        // The configuration row and every lifecycle row are update- and delete-proof.
        await AssertImmutableAsync(connection,
            "UPDATE recipe_lifecycle_store_config SET MaxEvents=3 WHERE Id=1;",
            "ImmutableRecipeLifecycleConfiguration");
        await AssertImmutableAsync(connection, "DELETE FROM recipe_lifecycle_store_config WHERE Id=1;",
            "ImmutableRecipeLifecycleConfiguration");

        var abandonment = Abandonment();
        var payload = RecipeLifecycleStorageCodec.Encode(abandonment);
        await InsertAsync(connection, abandonment, payload);
        await AssertImmutableAsync(connection, "UPDATE recipe_lifecycle_events SET PayloadHash=$value;",
            "ImmutableRecipeLifecycleEvent", ("$value", Hash("changed-payload")));
        await AssertImmutableAsync(connection, "DELETE FROM recipe_lifecycle_events;",
            "ImmutableRecipeLifecycleEvent");

        var rows = SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline);
        var row = Assert.Single(rows);
        Assert.Equal(abandonment.Position, row.Record.Position);
        Assert.Equal(abandonment.ContentHash, row.Record.ContentHash);
        Assert.Equal(abandonment.PreviousHash, row.Record.PreviousHash);
        Assert.Equal(abandonment.SourceDraft, row.Record.SourceDraft);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), row.PayloadHash);
        Assert.Equal(payload, row.Payload);
        Assert.Equal(4L, row.CommandAuditSequence);
        Assert.Equal(3L, row.AuthorizationAuditSequence);
        Assert.Equal(5L, row.AuditSequence);
        Assert.NotEqual(row.CommandEventId, row.AuthorizationEventId);
        Assert.Equal(Hash("command-1"), row.CommandAuditHash);
        Assert.Equal(Hash("authorization-1"), row.AuthorizationAuditHash);
        Assert.Equal(Hash("audit-1"), row.AuditHash);

        // The canonical audit binding is deterministic, covers the exact payload hash and
        // predecessor, and deliberately excludes the metadata entry's own sequence and
        // hash, so a metadata append can never recurse into the row binding.
        Assert.Equal(SqliteCommandStore.EncodeAuditBinding(row), SqliteCommandStore.EncodeAuditBinding(row));
        Assert.NotEqual(SqliteCommandStore.EncodeAuditBinding(row), SqliteCommandStore.EncodeAuditBinding(
            CopyRow(row, payloadHash: Hash("other-payload"))));
        Assert.NotEqual(SqliteCommandStore.EncodeAuditBinding(row), SqliteCommandStore.EncodeAuditBinding(
            CopyRow(row, record: Retirement(1, null))));
        Assert.Equal(SqliteCommandStore.EncodeAuditBinding(row), SqliteCommandStore.EncodeAuditBinding(
            CopyRow(row, auditSequence: 99, auditHash: Hash("other-audit"))));

        // A foreign or edited configuration fails closed without touching the ledger.
        AssertReason("RecipeLifecycleConfigurationMismatch", () => SqliteCommandStore
            .RequireConfiguredRecipeLifecycle(database, new RecipeLifecycleStoreOptions
            {
                MaximumPayloadBytes = 4096, MaxTotalBytes = 12288
            }, deadline));
    }

    [Fact]
    [Trait("VerificationId", "V148_S09")]
    public async Task V148_S09_LifecycleReadRejectsOfflineIndexChainAndKindTamper()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        var options = new RecipeLifecycleStoreOptions { MaximumPayloadBytes = 4096, MaxTotalBytes = 16384 };
        SqliteCommandStore.InitializeRecipeLifecycleTables(database, options, deadline);

        var abandonment = Abandonment();
        await InsertAsync(connection, abandonment, RecipeLifecycleStorageCodec.Encode(abandonment));
        Assert.Single(SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));

        // An offline editor that removes the immutability trigger is still detected,
        // because the payload is the only source of lifecycle truth.
        await ExecuteAsync(connection, "DROP TRIGGER recipe_lifecycle_event_immutable_update;");
        await ExecuteAsync(connection, "UPDATE recipe_lifecycle_events SET DraftId=$value WHERE Position=1;",
            ("$value", Guid.NewGuid().ToString("D")));
        AssertReason("RecipeLifecycleIndexedPayloadMismatch", () =>
            SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));
        await ExecuteAsync(connection, "UPDATE recipe_lifecycle_events SET DraftId=$value WHERE Position=1;",
            ("$value", DraftId.ToString("D")));
        await ExecuteAsync(connection, "UPDATE recipe_lifecycle_events SET PreviousHash=$value WHERE Position=1;",
            ("$value", Hash("other-previous")));
        AssertReason("RecipeLifecyclePreviousHashMismatch", () =>
            SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));
        await ExecuteAsync(connection, "UPDATE recipe_lifecycle_events SET PreviousHash=NULL WHERE Position=1;");
        Assert.Single(SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));

        // A predecessor that is not the previous record's own content hash (for example
        // the previous audit hash, or any other 64-character value) breaks the chain.
        var chained = Retirement(2, Hash("wrong-previous"),
            transitionId: Guid.NewGuid(), operationId: Guid.NewGuid());
        await InsertAsync(connection, chained, RecipeLifecycleStorageCodec.Encode(chained));
        AssertReason("RecipeLifecycleHistorySequenceInvalid", () =>
            SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));

        // The kind/draft and kind/release identities stay unique and consistent.
        var duplicate = Abandonment(transitionId: Guid.NewGuid(), operationId: Guid.NewGuid());
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(connection, duplicate,
            RecipeLifecycleStorageCodec.Encode(duplicate)));
        await Assert.ThrowsAsync<SqliteException>(() => InsertRowAsync(connection,
            position: "3", previousHash: null, transitionId: Guid.NewGuid().ToString("D"),
            operationId: Guid.NewGuid().ToString("D"), kind: "2", draftId: DraftId.ToString("D"),
            sourceRevision: "2", releaseId: null, recordHash: Hash("record-3"),
            payloadHash: Hash("payload-3"), payload: Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            commandEventId: Guid.NewGuid().ToString("D"), commandSequence: "11",
            commandHash: Hash("command-3"), authorizationEventId: Guid.NewGuid().ToString("D"),
            authorizationSequence: "10", authorizationHash: Hash("authorization-3"),
            auditSequence: "12", auditHash: Hash("audit-3")));
    }

    [Fact]
    [Trait("VerificationId", "V148_S10")]
    public void V148_S10_LifecycleCapacityAndStoredPayloadEncodingAreBounded()
    {
        var payload = RecipeLifecycleStorageCodec.Encode(Abandonment());
        var encoded = Convert.ToBase64String(payload);
        Assert.Equal(payload, SqliteCommandStore.DecodeRecipeLifecyclePayload(encoded));
        AssertReason("RecipeLifecyclePayloadEncodingInvalid", () =>
            SqliteCommandStore.DecodeRecipeLifecyclePayload(encoded + "\n"));
        AssertReason("RecipeLifecyclePayloadEncodingInvalid", () =>
            SqliteCommandStore.DecodeRecipeLifecyclePayload("A"));
        AssertReason("RecipeLifecyclePayloadCapacityExceeded", () =>
            SqliteCommandStore.DecodeRecipeLifecyclePayload(string.Empty));
        AssertReason("RecipeLifecyclePayloadCapacityExceeded", () => SqliteCommandStore
            .DecodeRecipeLifecyclePayload(new string('A', 8 * 1024 * 1024)));
        AssertReason("RecipeLifecyclePayloadCapacityExceeded", () => SqliteCommandStore
            .DecodeRecipeLifecyclePayload(Convert.ToBase64String(
                new byte[RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit + 1])));
    }

    [Fact]
    [Trait("VerificationId", "V148_S11")]
    public async Task V148_S11_LifecycleEntryCapacityIsEnforcedBeforeProjection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        var options = new RecipeLifecycleStoreOptions
        {
            MaxEvents = 1, MaximumPayloadBytes = 4096, MaxTotalBytes = 8192
        };
        SqliteCommandStore.InitializeRecipeLifecycleTables(database, options, deadline);
        var first = Abandonment();
        await InsertAsync(connection, first, RecipeLifecycleStorageCodec.Encode(first));
        var second = Abandonment(position: 2, previousHash: first.ContentHash,
            sourceDraft: new RecipeDraftRevisionReference(Guid.NewGuid(), 1, Hash("other-draft-revision")),
            transitionId: Guid.NewGuid(), operationId: Guid.NewGuid());
        await InsertAsync(connection, second, RecipeLifecycleStorageCodec.Encode(second));

        AssertReason("RecipeLifecycleEntryCapacityExceeded", () =>
            SqliteCommandStore.ReadRecipeLifecycleRows(database, options, deadline));
    }

    private static RecipeLifecycleStoredRow Row(RecipeLifecycleRecord record, long position)
    {
        var auditSequence = position * 3 + 2;
        return new RecipeLifecycleStoredRow(record, Hash("payload-" + position),
            RecipeLifecycleStorageCodec.Encode(record), Guid.NewGuid(), auditSequence - 1,
            Hash("command-" + position), Guid.NewGuid(), auditSequence - 2, Hash("authorization-" + position),
            auditSequence, Hash("audit-" + position));
    }

    private static RecipeLifecycleStoredRow CopyRow(RecipeLifecycleStoredRow row,
        RecipeLifecycleRecord? record = null, string? payloadHash = null, long? auditSequence = null,
        string? auditHash = null) => new(record ?? row.Record, payloadHash ?? row.PayloadHash, row.Payload,
        row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash, row.AuthorizationEventId,
        row.AuthorizationAuditSequence, row.AuthorizationAuditHash, auditSequence ?? row.AuditSequence,
        auditHash ?? row.AuditHash);

    /// <summary>Builds one payload whose map codes repeat the original record outside canonical order.</summary>
    private static byte[] NonCanonicalMapPayload()
    {
        var selection = new RecipeSelectionReference(3, Guid.NewGuid(), Hash("selection-revision"));
        var map = new RecipeContractReference("Lifecycle.Map", "1", Hash("map"));
        var record = Retirement(2, Hash("previous"), mapImpacts: new[]
        {
            new RecipeRetirementMapImpact(selection, map, new uint[] { 5, 20, 300 })
        });
        var payload = RecipeLifecycleStorageCodec.Encode(record);
        var offset = 5; // magic + format
        offset += 8 + 16 + 16 + 16; // position + transition + operation + runtime epoch
        offset += 1; // kind
        SkipOptionalString(payload, ref offset); // previous hash
        offset += 16 + 8; // source draft id + source revision
        SkipString(payload, ref offset); // source revision content hash
        SkipString(payload, ref offset); // source content hash
        var hasRecipe = payload[offset++] != 0;
        Assert.True(hasRecipe);
        SkipString(payload, ref offset);
        SkipString(payload, ref offset);
        SkipString(payload, ref offset); // recipe identity
        var hasRelease = payload[offset++] != 0;
        Assert.True(hasRelease);
        offset += 16; // release id
        var hasReleaseHash = payload[offset++] != 0;
        Assert.True(hasReleaseHash);
        SkipString(payload, ref offset); // release record content hash
        Assert.Equal((byte)0, payload[offset++]); // observed active
        Assert.Equal((byte)0, payload[offset++]); // cleared active
        Assert.Equal(1, ReadInt32(payload, ref offset)); // map impact count
        offset += 8 + 16; // selection position + selection revision id
        SkipString(payload, ref offset); // selection content hash
        SkipString(payload, ref offset);
        SkipString(payload, ref offset);
        SkipString(payload, ref offset); // map contract
        Assert.Equal(3, ReadInt32(payload, ref offset)); // affected code count
        var codesStart = offset;
        var reordered = payload.ToArray();
        payload.AsSpan(codesStart + 8, 4).CopyTo(reordered.AsSpan(codesStart, 4));
        payload.AsSpan(codesStart, 4).CopyTo(reordered.AsSpan(codesStart + 8, 4));
        return reordered;
    }

    private static void SkipOptionalString(byte[] payload, ref int offset)
    {
        var present = payload[offset++] != 0;
        if (present) SkipString(payload, ref offset);
    }

    private static void SkipString(byte[] payload, ref int offset)
    {
        var length = ReadInt32(payload, ref offset);
        Assert.True(length >= 0 && offset + length <= payload.Length);
        offset += length;
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        Assert.True(offset >= 0 && offset + 4 <= payload.Length);
        var result = BitConverter.ToInt32(payload, offset);
        offset += 4;
        return result;
    }

    private static RecipeLifecycleRecord Abandonment(long position = 1, string? previousHash = null,
        RecipeDraftRevisionReference? sourceDraft = null, string? sourceContentHash = null,
        Guid? actorPrincipalId = null, long actorAuthorizationRevision = 7, Guid? stepUpGrantId = null,
        string? authorizationTarget = null, string? reason = null, DateTimeOffset? recordedAtUtc = null,
        Guid? transitionId = null, Guid? operationId = null) => new(position, transitionId ?? TransitionId,
        operationId ?? OperationId, RuntimeEpoch, RecipeLifecycleKind.DraftAbandoned, previousHash,
        sourceDraft ?? DraftReference(), sourceContentHash ?? Hash("source-content"), null, null, null, null,
        null, null, actorPrincipalId ?? ActorPrincipalId, ActorSessionId, actorAuthorizationRevision,
        stepUpGrantId ?? StepUpGrantId, AuthorizationPolicy(), authorizationTarget ?? AuthorizationTarget(),
        reason ?? "draft abandoned", recordedAtUtc ?? RecordedAt);

    private static RecipeLifecycleRecord Retirement(long position, string? previousHash,
        RecipeReference? recipe = null, Guid? releaseId = null, string? releaseRecordContentHash = null,
        RecipeDraftRevisionReference? sourceDraft = null, string? sourceContentHash = null,
        RecipeActivationReference? observedActive = null, RecipeActivationReference? clearedActive = null,
        IEnumerable<RecipeRetirementMapImpact>? mapImpacts = null, Guid? actorPrincipalId = null,
        long actorAuthorizationRevision = 7, Guid? stepUpGrantId = null, string? authorizationTarget = null,
        string? reason = null, DateTimeOffset? recordedAtUtc = null, Guid? transitionId = null,
        Guid? operationId = null, string? contentHash = null) => new(position, transitionId ?? TransitionId,
        operationId ?? OperationId, RuntimeEpoch, RecipeLifecycleKind.ReleasedRetired, previousHash,
        sourceDraft ?? DraftReference(), sourceContentHash ?? Hash("source-content"), recipe ?? Recipe(),
        releaseId ?? ReleaseId, releaseRecordContentHash ?? Hash("release-record"), observedActive, clearedActive,
        mapImpacts, actorPrincipalId ?? ActorPrincipalId, ActorSessionId, actorAuthorizationRevision,
        stepUpGrantId ?? StepUpGrantId, AuthorizationPolicy(), authorizationTarget ?? AuthorizationTarget(),
        reason ?? "release retired", recordedAtUtc ?? RecordedAt.AddMinutes(1), contentHash);

    private static RecipeDraftRevisionReference DraftReference(long revision = 2) =>
        new(DraftId, revision, Hash("draft-revision"));

    private static RecipeReference Recipe() => new("Storage.Example", "1", LifecycleContent.ContentHash);

    private static RecipeContractReference AuthorizationPolicy() =>
        new("Lifecycle.Authorization", "1", Hash("authorization-policy"));

    private static string AuthorizationTarget() => Hash("authorization-target");

    private static RecipeActivationReference Activation(long position = 4) =>
        new(position, ActivationId, Hash("activation"));

    private static RecipeRetirementMapImpact Impact(uint code, long selectionPosition = 3,
        Guid? revisionId = null) => new(
        new RecipeSelectionReference(selectionPosition, revisionId ?? Guid.NewGuid(), Hash("selection-revision")),
        new RecipeContractReference("Lifecycle.Map", "1", Hash("map")), new[] { code });

    private static RecipeDraftRevision DraftRevision() => new(1, DraftId, 2, Guid.NewGuid(),
        Hash("previous-revision"), Hash("draft-revision"), LifecycleContent, AuthorA, Guid.NewGuid(), 1,
        "lifecycle source", DateTimeOffset.Parse("2026-09-09T00:00:00Z"));

    /// <summary>
    /// One real immutable release record over the exact preserved source revision above,
    /// built with the same governance evidence the schema-16 release ledger stores.
    /// </summary>
    private static RecipeReleaseRecord ReleaseRecord()
    {
        var source = DraftRevision();
        var policy = new RecipeGovernancePolicy("Storage.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var checks = new[]
        {
            new RecipeReleaseValidationCheck("Structure", "Recipe", true, "Passed"),
            new RecipeReleaseValidationCheck("Configuration", "count", true, "Passed",
                new RecipeContractReference("Count.Config", "1", Hash("schema")))
        };
        var changes = new[]
        {
            new RecipeReleaseChange("Configuration/count", "5", "6", AuthorA, source.DraftId, source.Revision)
        };
        return new RecipeReleaseRecord(1, ReleaseId, Guid.Parse("f6600000-0000-0000-0000-000000000006"), 1,
            source, policy, checks, changes, AuthorB, Guid.Parse("a7700000-0000-0000-0000-000000000007"), 1,
            StepUpGrantId, new RecipeContractReference("Authorization", "1", Hash("authorization")),
            "lifecycle storage test", Hash("target"),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddSeconds(1));
    }

    private static RecipeDraftContent Content(long count)
    {
        var schema = new AlgorithmConfigurationSchema("Count.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
                authoringDefault: AlgorithmScalarValue.FromInt64(5)),
            new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", false)
        });
        var entries = new List<AlgorithmConfigurationEntry>
        {
            new("count", "items", AlgorithmScalarValue.FromInt64(count))
        };
        var contract = new RecipeContractReference("Result", "1", Hash("result"));
        return new(null, "Storage.Example", "Storage recipe",
            new RecipeAlgorithmBinding(new AlgorithmIdentity("Count.Algorithm", "1"), schema, contract, contract),
            AlgorithmConfigurationSnapshot.Create(schema, entries), "TopCamera",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, null,
            entries.Select(value => new RecipeDraftFieldOrigin(value.Key, RecipeDraftValueOrigin.Explicit)));
    }

    private static Task InsertAsync(SqliteConnection connection, RecipeLifecycleRecord record, byte[] payload) =>
        InsertRowAsync(connection, record.Position.ToString(CultureInfo.InvariantCulture), record.PreviousHash,
            record.TransitionId.ToString("D"), record.OperationId.ToString("D"),
            ((int)record.Kind).ToString(CultureInfo.InvariantCulture),
            record.SourceDraft.DraftId.ToString("D"),
            record.SourceDraft.Revision.ToString(CultureInfo.InvariantCulture), record.ReleaseId?.ToString("D"),
            record.ContentHash, Convert.ToHexString(SHA256.HashData(payload)), Convert.ToBase64String(payload),
            Guid.NewGuid().ToString("D"), (record.Position * 3 + 1).ToString(CultureInfo.InvariantCulture),
            Hash("command-" + record.Position), Guid.NewGuid().ToString("D"),
            (record.Position * 3).ToString(CultureInfo.InvariantCulture), Hash("authorization-" + record.Position),
            (record.Position * 3 + 2).ToString(CultureInfo.InvariantCulture), Hash("audit-" + record.Position));

    private static Task InsertRowAsync(SqliteConnection connection, string position, string? previousHash,
        string transitionId, string operationId, string kind, string draftId, string sourceRevision,
        string? releaseId, string recordHash, string payloadHash, string payload, string commandEventId,
        string commandSequence, string commandHash, string authorizationEventId, string authorizationSequence,
        string authorizationHash, string auditSequence, string auditHash)
    {
        object previous = previousHash is null ? DBNull.Value : previousHash;
        object release = releaseId is null ? DBNull.Value : releaseId;
        return ExecuteAsync(connection, @"
            INSERT INTO recipe_lifecycle_events
                (Position,PreviousHash,TransitionId,OperationId,Kind,DraftId,SourceRevision,ReleaseId,
                 RecordContentHash,PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,
                 AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES($position,$previousHash,$transitionId,$operationId,$kind,$draftId,$sourceRevision,$releaseId,
                   $recordHash,$payloadHash,$payload,$commandEventId,$commandSequence,$commandHash,
                   $authorizationEventId,$authorizationSequence,$authorizationHash,$auditSequence,$auditHash);",
            ("$position", position), ("$previousHash", previous), ("$transitionId", transitionId),
            ("$operationId", operationId), ("$kind", kind), ("$draftId", draftId),
            ("$sourceRevision", sourceRevision), ("$releaseId", release), ("$recordHash", recordHash),
            ("$payloadHash", payloadHash), ("$payload", payload), ("$commandEventId", commandEventId),
            ("$commandSequence", commandSequence), ("$commandHash", commandHash),
            ("$authorizationEventId", authorizationEventId),
            ("$authorizationSequence", authorizationSequence), ("$authorizationHash", authorizationHash),
            ("$auditSequence", auditSequence), ("$auditHash", auditHash));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertImmutableAsync(SqliteConnection connection, string sql,
        string expected, params (string Name, object Value)[] parameters)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql, parameters));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AssertPayloadRejected(byte[] payload) =>
        Assert.Throws<InvalidOperationException>(() => RecipeLifecycleStorageCodec.Decode(payload));

    private static void AssertReason(string expected, Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
