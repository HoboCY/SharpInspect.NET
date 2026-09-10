#pragma warning disable CA1416
using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    internal static async Task<StationQualificationSessionEvent> CreateQualificationCodecTemplateAsync()
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "qualification codec template");
        await harness.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.Closed,
            "qualification codec template did not close");
        var page = await harness.History.QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        return page.Events[0];
    }
}

public sealed partial class CalibrationGovernanceRuntimeTests
{
    [Fact]
    public async Task V137_S20_QualificationRowsResolveNonemptyProfileFromSameSqliteSnapshot()
    {
        var template = await ManualInspectionRuntimeTests.CreateQualificationCodecTemplateAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(5);
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            utcNow: () => now, withGovernanceEvidence: true, withStationQualification: true);
        await fixture.WaitForHealthySourceAsync();
        var policy = fixture.GovernancePolicy!;
        var publication = await PublishPolicyAsync(fixture, policy);
        Assert.True(publication.Outcome.Disposition == CommandDisposition.Accepted, publication.Outcome.ReasonCode);
        var retained = await CaptureCandidateAsync(fixture);
        var evaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
        Assert.True(evaluation.Evaluation?.Passed, evaluation.Outcome.ReasonCode);
        var published = await PublishProfileAsync(fixture, retained.Candidate, evaluation.Evaluation!.Reference);
        var profile = Assert.IsType<PublishedCalibrationProfileVersion>(published.Profile);

        // This is a decoding-layer fixture. The profile and its governance
        // command/identity/audit rows are real; the copied activation is not
        // presented as a new authorized activation or qualification admission.
        var value = QualificationEventWithProfile(template, profile);
        var encoded = StationQualificationStorageCodec.Encode(value);
        Assert.Throws<InvalidOperationException>(() => StationQualificationStorageCodec.Decode(encoded));
        var direct = StationQualificationStorageCodec.Decode(encoded,
            reference => reference == profile.Reference ? profile : null);
        Assert.Equal(value.ContentHash, direct.ContentHash);

        var path = Path.Combine(Path.GetDirectoryName(fixture.Options.DatabasePath)!, "qualification-codec.sqlite");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            source.Open(); destination.Open(); source.BackupDatabase(destination);
            InsertQualificationCodecEvent(destination, value);
        }
        using (var connection = SqliteNative.Open(path, readOnly: true))
        {
            var rows = SqliteCommandStore.ReadStationQualificationRows(connection.Handle!,
                new StationQualificationStoreOptions(), new StoreDeadline(TimeSpan.FromSeconds(10)));
            var decoded = Assert.Single(rows).Event;
            Assert.Equal(value.ContentHash, decoded.ContentHash);
            var binding = Assert.Single(decoded.Header.TargetBaseline.SuccessfulSnapshot!.CalibrationBindings);
            Assert.Equal(profile.Reference, binding.Profile);
        }
        // A different exact profile cannot silently select an existing profile.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var clear = connection.CreateCommand();
            clear.CommandText = "DROP TRIGGER station_qualification_event_immutable_delete; DELETE FROM station_qualification_events;";
            clear.ExecuteNonQuery();
            InsertQualificationCodecEvent(connection, QualificationEventWithProfile(template,
                CalibrationExportPackageTests.ExportFixture.Create().Profile));
        }
        using (var connection = SqliteNative.Open(path, readOnly: true))
            Assert.Throws<InvalidOperationException>(() => SqliteCommandStore.ReadStationQualificationRows(
                connection.Handle!, new StationQualificationStoreOptions(), new StoreDeadline(TimeSpan.FromSeconds(10))));
    }

    private static StationQualificationSessionEvent QualificationEventWithProfile(
        StationQualificationSessionEvent template, PublishedCalibrationProfileVersion profile)
    {
        var old = template.Header.TargetBaseline;
        var snapshot = old.SuccessfulSnapshot!;
        var replacement = new RecipeActivationSnapshot(snapshot.EvidenceKind, snapshot.Release,
            snapshot.PreparedAlgorithmInstanceId, snapshot.AlgorithmExecutionPolicy, snapshot.CameraSetup,
            snapshot.PlcResultContract, new[] { new CalibrationRunProfileBinding(new string('A', 64), profile, null, null) },
            snapshot.FramePoolCapacity, snapshot.FramePoolMaximumBytes);
        var target = new RecipeActivationRecord(old.Position, old.ActivationId, old.AttemptId, old.OperationId,
            old.AdmissionReference, old.PreviousActivation, old.PreviousRecipe, old.PreviousSnapshotContentHash,
            old.Candidate, old.ReleaseId, old.ReleaseRecordContentHash, old.ResultingRecipe, old.Outcome,
            old.Checks, old.Restoration, replacement, old.EvidenceKind, old.ActorPrincipalId, old.ActorSessionId,
            old.ActorAuthorizationRevision, old.AuthorizationPolicy, old.ChangeReason, old.AuthorizationTarget,
            old.RecordedAtUtc, historicalSelection: old.HistoricalSelection);
        var p = template.Header.Plan;
        var plan = new StationQualificationPlan(target.Reference, p.TargetStationFingerprint,
            p.ReleaseCandidateFingerprint, p.ProfileHash, p.QualificationContextHash, p.Harness,
            p.TargetControllerConfiguration, p.TransientControllerConfiguration, p.DestinationBindings, p.ScenarioIds);
        var h = template.Header;
        var header = new StationQualificationSessionHeader(h.SessionId, h.RuntimeEpoch, h.LeaseNonce,
            h.StartCorrelationId, h.StartAttemptId, plan, target, h.ActorPrincipalId, h.ActorSessionId,
            h.ActorAuthorizationRevision, h.AuthorizationPolicy, h.StepUpGrantId, h.AuthorizationTarget,
            h.ChangeReason, h.StartedAtUtc);
        return new(1, null, header, template.CommandCorrelationId, template.AttemptId, template.CommandKind,
            template.Phase, template.Restoration, template.ReasonCode, false, template.RecordedAtUtc,
            commandAuditSequence: template.CommandAuditSequence, commandAuditHash: template.CommandAuditHash,
            authorizationAuditSequence: template.AuthorizationAuditSequence, authorizationAuditHash: template.AuthorizationAuditHash,
            auditSequence: template.AuditSequence, auditHash: template.AuditHash,
            commandAuthorizationTarget: template.CommandAuthorizationTarget);
    }

    private static void InsertQualificationCodecEvent(SqliteConnection connection, StationQualificationSessionEvent e)
    {
        var h = e.Header;
        InsertQualificationCodecRow(connection, "station_qualification_events", new()
        {
            ["Position"] = e.Position, ["PreviousHash"] = null, ["SessionId"] = h.SessionId.ToString("D"),
            ["RuntimeEpoch"] = h.RuntimeEpoch.ToString("D"), ["StartCorrelationId"] = h.StartCorrelationId.ToString("D"),
            ["StartAttemptId"] = h.StartAttemptId.ToString("D"), ["CommandCorrelationId"] = e.CommandCorrelationId.ToString("D"),
            ["AttemptId"] = e.AttemptId.ToString("D"), ["CommandKind"] = (int)e.CommandKind, ["Phase"] = (int)e.Phase,
            ["Restoration"] = (int)e.Restoration, ["Terminal"] = 0, ["RunId"] = null,
            ["ActorPrincipalId"] = h.ActorPrincipalId.ToString("D"), ["ActorSessionId"] = h.ActorSessionId.ToString("D"),
            ["ActorAuthorizationRevision"] = h.ActorAuthorizationRevision, ["AuthorizationTarget"] = h.AuthorizationTarget,
            ["ReasonCode"] = e.ReasonCode, ["RecordedAtUtc"] = e.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["HeaderContentHash"] = h.ContentHash, ["ContentHash"] = e.ContentHash,
            ["PayloadHash"] = StationQualificationStorageCodec.PayloadHash(SqliteCommandStore.EncodeStationQualificationAuditPayload(e)),
            ["Payload"] = Convert.ToBase64String(StationQualificationStorageCodec.Encode(e)),
            ["CommandAuditSequence"] = e.CommandAuditSequence, ["CommandAuditHash"] = e.CommandAuditHash,
            ["AuthorizationAuditSequence"] = e.AuthorizationAuditSequence, ["AuthorizationAuditHash"] = e.AuthorizationAuditHash,
            ["AuditSequence"] = e.AuditSequence, ["AuditHash"] = e.AuditHash,
            ["CommandAuthorizationTarget"] = e.CommandAuthorizationTarget
        });
    }

    private static void InsertQualificationCodecRow(SqliteConnection connection, string table, Dictionary<string, object?> values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(key => '@' + key))})";
        foreach (var pair in values) command.Parameters.AddWithValue('@' + pair.Key, pair.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
