using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Activation v3 evidence boundaries: the human record/command hashes and version
/// 1/2 bytes stay exactly as they were, while a PLC adapter request is a distinct,
/// fully specified actor plus request context that never masquerades as a human.
/// </summary>
public sealed class RecipeActivationPlcActorCodecTests
{
    // Frozen from the T45 Release assembly (SharpInspect.Abstractions.dll/Runtime.dll,
    // 2026/9/11) before this change; the admission and record hashes were additionally
    // re-derived from the exact hash part layout, so a silent human-path change to
    // either the hash or the version 1/2 bytes fails immediately.
    private const string V1AdmissionHash = "D8AF98B4510E0718598F76BE1594457D044A743E00F516852F1F137B0CD45B81";
    private const string V1RecordHash = "84BBAF28591DCD0610D0A09B2C5E2F78E39066CEABF095C479ABC3237E735227";
    private const string V1PayloadHash = "6F29DEA9035CA7C4FBD90E4B347DF6804FCC84DBDDEA846F676685387D115A51";
    private const string V1CommandTarget = "EFEF01CF60E8B28574FA4B6C6864B29C58D2A8289428E25BBD9D602D74122F60";
    private const string V2AdmissionHash = "A010A93B9A8C9811E84360256C3803750A9C5B6A0D1423019793264833710472";
    private const string V2RecordHash = "BD375F8F485740DD883CBE727F69A958D31C9CF6AEE70278A66BB814A2375B30";
    private const string V2PayloadHash = "06775BA4F41D13B114148E643771AFBFA7D61218346C8F59901C141A5CEC3A2A";
    private const string V2CommandTarget = "320214B17B5FD294E0FCD8FD1EC066888759081FFBFE0C45C6B43E55C6B8DB75";

    private static readonly Guid ActivationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PrincipalId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ReleaseId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid RuntimeEpoch = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid PlcReleaseId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset RecordedAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly RecipeReference Candidate = new("Fixture.Recipe", "1", new string('C', 64));
    private static readonly RecipeReference PlcCandidate = new("Fixture.PlcRecipe", "2", new string('B', 64));
    private static readonly RecipeContractReference AuthorizationPolicy =
        new("Fixture.Authorization", "1", new string('E', 64));
    private static readonly string ReleaseRecordContentHash = new('D', 64);
    private static readonly string AuthorizationTarget = new('F', 64);

    [Fact]
    public void V146_A01_HumanActorV1RecordHashAndPayloadBytesStayFrozen()
    {
        var fixture = BuildHumanFixture(historicalSelection: null);

        Assert.Equal(V1AdmissionHash, fixture.Admission.ContentHash);
        Assert.Equal(V1RecordHash, fixture.Record.ContentHash);
        Assert.Equal(V1CommandTarget, fixture.Command.AuthorizationTarget);
        Assert.True(fixture.Admission.Actor.IsHuman);
        Assert.Equal(PrincipalId, fixture.Admission.ActorPrincipalId);

        var payload = RecipeActivationStorageCodec.Encode(fixture.Record);
        Assert.Equal(1, (int)payload[4]);
        Assert.Equal(V1PayloadHash, Hash(payload));

        var decoded = RecipeActivationStorageCodec.Decode(payload);
        Assert.Equal(V1RecordHash, decoded.ContentHash);
        Assert.Equal(V1AdmissionHash, decoded.Admission!.ContentHash);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, decoded.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, decoded.EvidenceKind);
        Assert.True(decoded.Actor!.IsHuman);
        Assert.Equal(PrincipalId, decoded.ActorPrincipalId);
        Assert.Equal(SessionId, decoded.ActorSessionId);
        Assert.Equal(1L, decoded.ActorAuthorizationRevision);
        Assert.Null(decoded.HistoricalSelection);
        Assert.Equal(payload, RecipeActivationStorageCodec.Encode(decoded));
    }

    [Fact]
    public void V146_A02_HumanHistoricalSelectionV2RecordHashAndPayloadBytesStayFrozen()
    {
        var fixture = BuildHumanFixture(HistoricalSelection());

        Assert.Equal(V2AdmissionHash, fixture.Admission.ContentHash);
        Assert.Equal(V2RecordHash, fixture.Record.ContentHash);
        Assert.Equal(V2CommandTarget, fixture.Command.AuthorizationTarget);

        var payload = RecipeActivationStorageCodec.Encode(fixture.Record);
        Assert.Equal(2, (int)payload[4]);
        Assert.Equal(V2PayloadHash, Hash(payload));

        var decoded = RecipeActivationStorageCodec.Decode(payload);
        Assert.Equal(V2RecordHash, decoded.ContentHash);
        var admission = decoded.Admission!;
        Assert.NotNull(decoded.Admission);
        Assert.Equal(V2AdmissionHash, admission.ContentHash);
        Assert.Equal(HistoricalSelection(), decoded.HistoricalSelection);
        Assert.Equal(HistoricalSelection(), admission.HistoricalSelection);
        Assert.Equal(payload, RecipeActivationStorageCodec.Encode(decoded));
    }

    [Fact]
    public void V146_A03_PlcAdapterV3RecordRoundTripsWithNoHumanFields()
    {
        var fixture = BuildPlcFixture(PlcContext());

        Assert.Equal(RecipeActivationEvidenceKind.LocalAuthority, fixture.Record.EvidenceKind);
        var payload = RecipeActivationStorageCodec.Encode(fixture.Record);
        Assert.Equal(3, (int)payload[4]);

        var decoded = RecipeActivationStorageCodec.Decode(payload);
        Assert.Equal(fixture.Record.ContentHash, decoded.ContentHash);
        var admission = decoded.Admission!;
        Assert.NotNull(decoded.Admission);
        Assert.Equal(fixture.Admission.ContentHash, admission.ContentHash);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, decoded.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.LocalAuthority, decoded.EvidenceKind);
        Assert.Null(decoded.HistoricalSelection);
        // The existing nullable human identity properties stay null for a PLC request.
        Assert.Null(decoded.ActorPrincipalId);
        Assert.Null(decoded.ActorSessionId);
        Assert.Null(decoded.ActorAuthorizationRevision);

        var actor = decoded.Actor!;
        Assert.NotNull(decoded.Actor);
        Assert.True(actor.IsPlcAdapter);
        Assert.Equal(SystemPrincipalId.PlcAdapter, actor.SystemPrincipalId);
        Assert.Null(actor.HumanPrincipalId);
        Assert.Null(actor.HumanSessionId);
        Assert.Null(actor.HumanAuthorizationRevision);
        var context = actor.PlcRequestContext!;
        Assert.NotNull(actor.PlcRequestContext);
        AssertContext(fixture.Context, context);
        Assert.Equal(actor.ContentHash, admission.Actor.ContentHash);

        // The old human-only getters are honest on a PLC admission instead of inventing a person.
        var principal = Assert.Throws<InvalidOperationException>(() => admission.ActorPrincipalId);
        Assert.Equal("RecipeActivationActorIsNotHuman", principal.Message);
        var session = Assert.Throws<InvalidOperationException>(() => admission.ActorSessionId);
        Assert.Equal("RecipeActivationActorIsNotHuman", session.Message);
        var revision = Assert.Throws<InvalidOperationException>(() => admission.ActorAuthorizationRevision);
        Assert.Equal("RecipeActivationActorIsNotHuman", revision.Message);
        Assert.Empty(admission.CalibrationSelections);

        // Re-encoding the decoded event reproduces the exact canonical version 3 bytes.
        Assert.Equal(payload, RecipeActivationStorageCodec.Encode(decoded));
        Assert.Equal(fixture.Actor.ContentHash, actor.ContentHash);
    }

    [Fact]
    public void V146_A04_PlcRequestIdentityIgnoresRuntimeEpochCodeAndProfile()
    {
        var baseline = PlcContext();
        var restarted = new PlcRecipeActivationRequestContext(Guid.NewGuid(), baseline.EndpointContentHash,
            new RecipeContractReference("Fixture.ProtocolProfile", "2", new string('4', 64)),
            baseline.ControllerEpoch, baseline.RequestSequence, baseline.SelectionCode + 5,
            new RecipeContractReference("Fixture.SelectionPolicy", "2", new string('5', 64)),
            new RecipeContractReference("Fixture.SelectionMap", "2", new string('6', 64)),
            Candidate, ReleaseId, new string('7', 64));

        // A restart, a new protocol profile, or another selection code cannot erase the
        // identity of an already observed PLC request.
        Assert.Equal(baseline.RequestIdentityHash, restarted.RequestIdentityHash);
        Assert.NotEqual(baseline.ContentHash, restarted.ContentHash);

        // Every identity input stays sensitive on its own.
        Assert.NotEqual(baseline.RequestIdentityHash, Mutate(baseline, endpoint: new string('8', 64)).RequestIdentityHash);
        Assert.NotEqual(baseline.RequestIdentityHash,
            Mutate(baseline, controllerEpoch: baseline.ControllerEpoch + 1).RequestIdentityHash);
        Assert.NotEqual(baseline.RequestIdentityHash,
            Mutate(baseline, requestSequence: baseline.RequestSequence + 1).RequestIdentityHash);
        Assert.Equal(baseline.RequestIdentityHash, Mutate(baseline, selectionCode: 99).RequestIdentityHash);
    }

    [Fact]
    public void V146_A05_InternalPlcCommandClaimsOnlyTheFixedAdapterActor()
    {
        var context = PlcContext();
        var command = PlcCommand(context);

        Assert.Equal(context.Candidate, command.Candidate);
        Assert.Equal(context.ReleaseId, command.ReleaseId);
        Assert.Equal(context.ReleaseRecordContentHash, command.ReleaseRecordContentHash);
        Assert.Null(command.ExpectedActive);
        Assert.Empty(command.CalibrationSelections);
        Assert.Null(command.HistoricalSelection);
        Assert.Equal(context.ContentHash, command.PlcRequestContext!.ContentHash);
        Assert.Null(BuildHumanFixture(null).Command.PlcRequestContext);

        // The PLC target keeps the exact human candidate target and binds the request
        // context on top of it.
        var candidateTarget = ActivateRecipeCommand.ComputeAuthorizationTarget(context.Candidate,
            context.ReleaseId, context.ReleaseRecordContentHash, null, null, command.ChangeReason);
        Assert.Equal(ActivateRecipeCommand.ComputePlcAuthorizationTarget(candidateTarget, context),
            command.AuthorizationTarget);
        Assert.NotEqual(candidateTarget, command.AuthorizationTarget);

        // A claimed human console identity, another principal, a session, or a Step-Up
        // grant can never become a PLC request.
        Assert.Throws<ArgumentException>(() => new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.PhysicalConsole, SystemPrincipalId.PlcAdapter, null, null),
            context, "plc-recipe-change"));
        Assert.Throws<ArgumentException>(() => new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.Integration, "SharpInspect.OtherSystem", null, null),
            context, "plc-recipe-change"));
        Assert.Throws<ArgumentException>(() => new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, SessionId, null),
            context, "plc-recipe-change"));
        Assert.Throws<ArgumentException>(() => new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, null, Guid.NewGuid()),
            context, "plc-recipe-change"));
        Assert.Throws<ArgumentNullException>(() => new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, null, null),
            null!, "plc-recipe-change"));
    }

    [Fact]
    public void V146_A06_MixedAndMismatchedActorsAndContextsAreRejected()
    {
        var fixture = BuildPlcFixture(PlcContext());
        var other = BuildPlcFixture(Mutate(fixture.Context, requestSequence: fixture.Context.RequestSequence + 1));
        var selection = new CalibrationProfileSelection(new string('A', 64), new CalibrationProfileReference(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), 1, new string('0', 64)));

        // The admission and the record must resolve to the candidate and release of the
        // exact context; another candidate or release is not a PLC request.
        Assert.Throws<ArgumentException>(() => PlcAdmission(fixture.Context, fixture.Actor, fixture.Command,
            candidate: Candidate));
        Assert.Throws<ArgumentException>(() => PlcAdmission(fixture.Context, fixture.Actor, fixture.Command,
            releaseId: ReleaseId));
        Assert.Throws<ArgumentException>(() => PlcAdmission(fixture.Context, fixture.Actor, fixture.Command,
            calibrationSelections: new[] { selection }));
        Assert.Throws<ArgumentException>(() => PlcRecord(fixture.Context, fixture.Actor, fixture.Command,
            candidate: Candidate));
        Assert.Throws<ArgumentException>(() => PlcRecord(fixture.Context, fixture.Actor, fixture.Command,
            releaseRecordContentHash: ReleaseRecordContentHash));
        // A PLC event can never carry a historical calibration selection.
        Assert.Throws<ArgumentException>(() => PlcRecord(fixture.Context, fixture.Actor, fixture.Command,
            historicalSelection: new HistoricalCalibrationSelectionIntent(
                HistoricalCalibrationSelectionSources.LocalProfileHistory, null, "plc-recipe-change")));

        // A record can never borrow another context's admission, and a human record can
        // never claim a PLC admission.
        Assert.Throws<ArgumentException>(() => PlcRecord(fixture.Context, fixture.Actor, fixture.Command,
            admission: other.Admission));
        Assert.Throws<ArgumentException>(() => new RecipeActivationRecord(1, ActivationId, AttemptId, OperationId,
            null, null, null, null, fixture.Context.Candidate, fixture.Context.ReleaseId,
            fixture.Context.ReleaseRecordContentHash, null, Admitted(), Array.Empty<RecipeActivationCheck>(),
            Restoration(), null, RecipeActivationEvidenceKind.LocalAuthority, PrincipalId, SessionId, 1,
            AuthorizationPolicy, "plc-recipe-change", fixture.Command.AuthorizationTarget, RecordedAt,
            fixture.Admission, null));

        // An admitted or succeeded event always carries an actor; only the original
        // unauthenticated pre-admission rejection may have none.
        Assert.Throws<ArgumentException>(() => new RecipeActivationRecord(1, ActivationId, AttemptId, OperationId,
            null, null, null, null, Candidate, ReleaseId, ReleaseRecordContentHash, null, Admitted(),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.LocalAuthority, (RecipeActivationActor?)null, null,
            "fixture-reason", AuthorizationTarget, RecordedAt));
        var rejected = new RecipeActivationRecord(1, ActivationId, AttemptId, OperationId,
            null, null, null, null, Candidate, ReleaseId, ReleaseRecordContentHash, null,
            new RecipeActivationOutcome(RecipeActivationOutcomeState.Failed, "RecipeActivationUnauthorized"),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.LocalAuthority, (Guid?)null, (Guid?)null, (long?)null, null,
            "fixture-reason", AuthorizationTarget, RecordedAt);
        Assert.Null(rejected.Actor);
        Assert.Null(rejected.ActorPrincipalId);
        Assert.Equal(1, (int)RecipeActivationStorageCodec.Encode(rejected)[4]);
    }

    [Fact]
    public void V146_A07_V3MalformedHashTamperAndExtraBytesAreRejected()
    {
        var fixture = BuildPlcFixture(PlcContext());
        var payload = RecipeActivationStorageCodec.Encode(fixture.Record);

        // An unknown version and extra trailing bytes stay rejected.
        Rejected(Patch(payload, 4, 4), "RecipeActivationPayloadVersionUnsupported");
        Rejected(Append(payload), "RecipeActivationPayloadTrailingBytes");
        Rejected(payload[..^1], null);

        // A human event relabelled as version 3 is a mixed human/system record, not a
        // PLC request; a PLC event relabelled as version 2 is not silently a human one.
        var humanPayload = RecipeActivationStorageCodec.Encode(BuildHumanFixture(HistoricalSelection()).Record);
        Rejected(Patch(humanPayload, 4, 3), "RecipeActivationPlcActorMixed");
        Rejected(Patch(payload, 4, 2), null);

        // The fixed adapter identity and the context values are covered by the payload.
        var endpointHashIndex = FindAscii(payload, fixture.Context.EndpointContentHash, 0);
        Rejected(Patch(payload, endpointHashIndex, (byte)'7'), "RecipeActivationPlcContextHashMismatch");
        var principalIndex = FindAscii(payload, SystemPrincipalId.PlcAdapter, 0);
        Rejected(Patch(payload, principalIndex, (byte)'X'), "RecipeActivationPlcAdmissionActorInvalid");
        var recordPrincipalIndex = FindAscii(payload, SystemPrincipalId.PlcAdapter, 1);
        Rejected(Patch(payload, recordPrincipalIndex, (byte)'X'), "RecipeActivationPlcRecordActorInvalid");
        var recordEndpointHashIndex = FindAscii(payload, fixture.Context.EndpointContentHash, 1);
        Rejected(Patch(payload, recordEndpointHashIndex, (byte)'7'), "RecipeActivationPlcContextHashMismatch");
    }

    private static HistoricalCalibrationSelectionIntent HistoricalSelection() => new(
        HistoricalCalibrationSelectionSources.LocalProfileHistory, null, "fixture-historical-reason");

    private static RecipeActivationOutcome Admitted() =>
        new(RecipeActivationOutcomeState.Admitted, "RecipeActivationAdmitted");

    private static RecipeActivationRestoration Restoration() =>
        new(RecipeActivationRestorationState.NotRequired, "RecipeActivationHardwareUntouched");

    private static HumanFixture BuildHumanFixture(HistoricalCalibrationSelectionIntent? historicalSelection)
    {
        var reason = historicalSelection is null ? "fixture-reason" : "fixture-historical-reason";
        var admission = new RecipeActivationAdmission(1, ActivationId, AttemptId, OperationId, Candidate,
            ReleaseId, ReleaseRecordContentHash, null, null, null, null, null, reason, PrincipalId, SessionId, 1,
            AuthorizationPolicy, AuthorizationTarget, RecipeActivationEvidenceKind.InternalContractFixture,
            RecordedAt, historicalSelection);
        var record = new RecipeActivationRecord(1, ActivationId, AttemptId, OperationId, null, null, null, null,
            Candidate, ReleaseId, ReleaseRecordContentHash, null, Admitted(),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.InternalContractFixture, PrincipalId, SessionId, 1, AuthorizationPolicy,
            reason, AuthorizationTarget, RecordedAt, admission, historicalSelection);
        var command = new ActivateRecipeCommand(OperationId,
            new CommandInvocation(CommandSource.PhysicalConsole, "fixture-principal", SessionId, null), Candidate,
            ReleaseId, ReleaseRecordContentHash, null, null, reason);
        return new(admission, record, command);
    }

    private static PlcRecipeActivationRequestContext PlcContext() => new(RuntimeEpoch, new string('A', 64),
        new RecipeContractReference("Fixture.ProtocolProfile", "1", new string('1', 64)), 7, 42, 3,
        new RecipeContractReference("Fixture.SelectionPolicy", "1", new string('2', 64)),
        new RecipeContractReference("Fixture.SelectionMap", "1", new string('3', 64)),
        PlcCandidate, PlcReleaseId, new string('9', 64));

    private static PlcRecipeActivationRequestContext Mutate(PlcRecipeActivationRequestContext context,
        string? endpoint = null, uint? controllerEpoch = null, uint? requestSequence = null, uint? selectionCode = null)
        => new(context.RuntimeEpoch, endpoint ?? context.EndpointContentHash, context.ProtocolProfile,
            controllerEpoch ?? context.ControllerEpoch, requestSequence ?? context.RequestSequence,
            selectionCode ?? context.SelectionCode, context.SelectionPolicy, context.SelectionMap,
            context.Candidate, context.ReleaseId, context.ReleaseRecordContentHash);

    private static ActivateRecipeCommand PlcCommand(PlcRecipeActivationRequestContext context) =>
        new(OperationId, new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, null, null),
            context, "plc-recipe-change");

    private static RecipeActivationAdmission PlcAdmission(PlcRecipeActivationRequestContext context,
        RecipeActivationActor actor, ActivateRecipeCommand command, RecipeReference? candidate = null,
        Guid? releaseId = null, string? releaseRecordContentHash = null,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections = null) =>
        new(1, ActivationId, AttemptId, OperationId, candidate ?? context.Candidate, releaseId ?? context.ReleaseId,
            releaseRecordContentHash ?? context.ReleaseRecordContentHash, null, null, null, null,
            calibrationSelections, command.ChangeReason, actor, AuthorizationPolicy, command.AuthorizationTarget,
            RecipeActivationEvidenceKind.LocalAuthority, RecordedAt);

    private static RecipeActivationRecord PlcRecord(PlcRecipeActivationRequestContext context,
        RecipeActivationActor actor, ActivateRecipeCommand command, RecipeReference? candidate = null,
        string? releaseRecordContentHash = null, RecipeActivationAdmission? admission = null,
        HistoricalCalibrationSelectionIntent? historicalSelection = null) =>
        new(1, ActivationId, AttemptId, OperationId, null, null, null, null, candidate ?? context.Candidate,
            context.ReleaseId, releaseRecordContentHash ?? context.ReleaseRecordContentHash, null, Admitted(),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.LocalAuthority, actor, AuthorizationPolicy, command.ChangeReason,
            command.AuthorizationTarget, RecordedAt, admission, historicalSelection);

    private static PlcFixture BuildPlcFixture(PlcRecipeActivationRequestContext context)
    {
        var actor = RecipeActivationActor.PlcAdapter(context);
        var command = PlcCommand(context);
        var admission = PlcAdmission(context, actor, command);
        return new(context, actor, admission, PlcRecord(context, actor, command, admission: admission), command);
    }

    private static void AssertContext(PlcRecipeActivationRequestContext expected,
        PlcRecipeActivationRequestContext actual)
    {
        Assert.Equal(expected.RuntimeEpoch, actual.RuntimeEpoch);
        Assert.Equal(expected.EndpointContentHash, actual.EndpointContentHash);
        Assert.Equal(expected.ProtocolProfile, actual.ProtocolProfile);
        Assert.Equal(expected.ControllerEpoch, actual.ControllerEpoch);
        Assert.Equal(expected.RequestSequence, actual.RequestSequence);
        Assert.Equal(expected.SelectionCode, actual.SelectionCode);
        Assert.Equal(expected.SelectionPolicy, actual.SelectionPolicy);
        Assert.Equal(expected.SelectionMap, actual.SelectionMap);
        Assert.Equal(expected.Candidate, actual.Candidate);
        Assert.Equal(expected.ReleaseId, actual.ReleaseId);
        Assert.Equal(expected.ReleaseRecordContentHash, actual.ReleaseRecordContentHash);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.RequestIdentityHash, actual.RequestIdentityHash);
    }

    private static void Rejected(byte[] payload, string? innerReason)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RecipeActivationStorageCodec.Decode(payload));
        Assert.Equal("RecipeActivationPayloadInvalid", exception.Message);
        if (innerReason is not null)
            Assert.Equal(innerReason, exception.InnerException?.Message);
    }

    private static byte[] Patch(byte[] payload, int offset, byte value)
    {
        var copy = payload.ToArray();
        copy[offset] = value;
        return copy;
    }

    private static byte[] Append(byte[] payload) => payload.Concat(new byte[] { 0 }).ToArray();

    private static int FindAscii(byte[] payload, string text, int occurrence)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        var found = 0;
        for (var index = 0; index <= payload.Length - needle.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (payload[index + offset] != needle[offset]) { match = false; break; }
            }
            if (!match) continue;
            if (found == occurrence) return index;
            found++;
        }
        throw new InvalidOperationException("FixturePatternMissing");
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private sealed record HumanFixture(RecipeActivationAdmission Admission, RecipeActivationRecord Record,
        ActivateRecipeCommand Command);

    private sealed record PlcFixture(PlcRecipeActivationRequestContext Context, RecipeActivationActor Actor,
        RecipeActivationAdmission Admission, RecipeActivationRecord Record, ActivateRecipeCommand Command);
}
