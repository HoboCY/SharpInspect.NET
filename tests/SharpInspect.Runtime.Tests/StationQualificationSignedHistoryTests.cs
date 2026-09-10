using System.Reflection;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V137_S15_SignedButInvalidTransitionIsRejectedByCommonWriteGuard()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, allowDisposeFailure: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "signed semantic history start");
        await harness.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification was not ready for signed semantic fixture");
        var store = harness.Fixture.Store;
        var options = harness.Fixture.Options;
        var policy = options.AuditIntegrityPolicy!;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var key = Assert.IsAssignableFrom<IAuditSigningKey>(typeof(SqliteCommandStore)
            .GetField("_signingKey", instance)!.GetValue(store));
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: false);
        var db = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        SqliteNative.Execute(db, "BEGIN IMMEDIATE;", deadline);
        try
        {
            var rows = SqliteCommandStore.ReadStationQualificationRows(db, options.StationQualifications!, deadline);
            var last = rows[^1].Event;
            Assert.Equal(StationQualificationSessionPhase.ReadyForStimulus, last.Phase);
            Assert.Equal(StationQualificationSessionPhase.Isolating, rows[^2].Event.Phase);
            Assert.Equal(last.AuditSequence, AuditChainDatabase.Tail(db, deadline).Sequence);
            Assert.Equal(0, AuditChainDatabase.Scalar(db, "SELECT COUNT(*) FROM audit_anchor_receipts;", deadline));

            // This fixture represents a trusted writer generating invalid history.
            // It deliberately uses only its temporary test key; it is not a claim
            // that an unsigned disk edit could replace a production signature.
            var triggers = AuditChainDatabase.Read(db, "SELECT name,sql FROM sqlite_master WHERE type='trigger' " +
                "AND tbl_name IN ('station_qualification_events','audit_entries','audit_checkpoints');", deadline,
                statement => (Name: SqliteNative.ColumnText(statement, 0)!, Sql: SqliteNative.ColumnText(statement, 1)!));
            foreach (var trigger in triggers)
                SqliteNative.Execute(db, "DROP TRIGGER \"" + trigger.Name.Replace("\"", "\"\"") + "\";", deadline);
            AuditChainDatabase.Execute(db, "DELETE FROM audit_checkpoints WHERE Sequence>=?;", deadline,
                last.AuditSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AuditChainDatabase.Execute(db, "DELETE FROM audit_entries WHERE Sequence=?;", deadline,
                last.AuditSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AuditChainDatabase.Execute(db, "DELETE FROM station_qualification_events WHERE Position=?;", deadline,
                last.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));

            StationQualificationSessionEvent InvalidEvent(long auditSequence, string? auditHash) => new(
                last.Position, last.PreviousHash, last.Header, last.CommandCorrelationId, last.AttemptId,
                last.CommandKind, StationQualificationSessionPhase.Admitted, last.Restoration,
                last.ReasonCode, false, last.RecordedAtUtc, last.Observation, last.Run,
                last.CommandAuditSequence, last.CommandAuditHash, last.AuthorizationAuditSequence,
                last.AuthorizationAuditHash, auditSequence, auditHash, last.RecoveryAttempt, last.CommandAuthorizationTarget);
            var auditPayload = SqliteCommandStore.EncodeStationQualificationAuditPayload(InvalidEvent(0, null));
            var sequence = AuditChainDatabase.AppendStationQualificationLedgerEntry(db, policy, key,
                last.Position, auditPayload, options.StationQualifications!, deadline);
            var tail = AuditChainDatabase.Tail(db, deadline);
            Assert.Equal(last.AuditSequence, sequence);
            var malformed = InvalidEvent(sequence, tail.Hash);
            typeof(SqliteCommandStore).GetMethod("InsertStationQualificationEvent", statics)!.Invoke(null,
                new object[] { db, malformed, StationQualificationStorageCodec.Encode(malformed), auditPayload,
                    options.StationQualifications!, deadline });
            if (AuditChainDatabase.Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) != sequence)
            {
                var checkpoint = AuditCheckpointCrypto.Create(policy, key, sequence, tail.Hash, DateTimeOffset.UtcNow);
                AuditChainDatabase.Execute(db, "INSERT INTO audit_checkpoints VALUES(?,?,?);", deadline,
                    sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), checkpoint.CheckpointId.ToString("D"),
                    JsonSerializer.Serialize(checkpoint));
            }
            foreach (var trigger in triggers) SqliteNative.Execute(db, trigger.Sql, deadline);

            var verified = AuditChainDatabase.Verify(db, policy, key.KeyId, key.PublicKeyBase64,
                new(0, policy.MaximumVerificationEntries), false, deadline, validateAnchorReceipt: false,
                archiveOptions: options.AlgorithmResultArchive, recipeDraftOptions: options.RecipeDrafts,
                cameraSetupOptions: options.CameraSetup, cameraRecoveryOptions: options.CameraRecovery,
                cameraNetworkOptions: options.CameraNetwork, imagingSetupOptions: options.ImagingSetup,
                calibrationSessionOptions: options.CalibrationSessions, governanceOptions: options.CalibrationGovernance,
                releaseOptions: options.RecipeReleases, contractOptions: options.PlcResultContracts,
                activationOptions: options.RecipeActivations, previewOptions: options.PreviewSessions,
                importOptions: options.CalibrationImports, manualOptions: options.ManualInspections,
                productionAdmissionOptions: options.ProductionAdmission, stationQualificationOptions: options.StationQualifications);
            Assert.Equal(AuditIntegrityState.Verified, verified.State);
            Assert.Equal(sequence, verified.VerifiedThroughSequence);
            var failure = Assert.Throws<TargetInvocationException>(() => typeof(SqliteCommandStore)
                .GetMethod("RequireStationQualificationWriteSnapshot", instance)!.Invoke(store,
                    new object[] { db, verified, deadline }));
            var semantic = Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal("StationQualificationPhaseTransitionInvalid", semantic.Message);
            Assert.Equal(tail, AuditChainDatabase.Tail(db, deadline));
            SqliteNative.Execute(db, "COMMIT;", deadline);
        }
        catch
        {
            SqliteNative.Execute(db, "ROLLBACK;", new StoreDeadline(TimeSpan.FromSeconds(2)));
            throw;
        }
        // Keep the invalid, correctly signed fixture intact. The ordinary append
        // must not extend it even if its background integrity observation is stale.
        var before = AuditChainDatabase.Tail(db, new StoreDeadline(TimeSpan.FromSeconds(2)));
        var rejected = await store.AppendAsync(new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), DateTimeOffset.UtcNow, AuditedCommandKind.Unsupported, CommandSource.PhysicalConsole,
            null, null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "UnsupportedCommand"),
            new StoreDeadline(TimeSpan.FromSeconds(5)));
        Assert.False(rejected.Committed, rejected.ReasonCode);
        Assert.Equal(before, AuditChainDatabase.Tail(db, new StoreDeadline(TimeSpan.FromSeconds(2))));
    }
}
