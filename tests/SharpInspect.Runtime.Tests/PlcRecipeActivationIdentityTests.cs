using System.Buffers.Binary;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// The schema-31 identity envelope: existing human rows keep their exact 49-field
/// bytes, while a PLC Adapter request may append one bounded, canonical evidence
/// field that only a true system actor can carry and only a matching activation
/// record can match.
/// </summary>
public sealed class PlcRecipeActivationIdentityTests
{
    [Theory]
    [InlineData("SharpInspect.PlcAdapter")]
    [InlineData("RequestMappedRecipeActivation")]
    public void V146_I10_SystemPrincipalAndCapabilityAreExplicitAndCannotBeSubstituted(string binding)
    {
        var evidence = PlcRecipeActivationIdentityCodec.Encode(PlcContext());
        var encoded = evidence[3..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
        var bytes = Convert.FromBase64String(encoded);
        var at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(binding).AsSpan());
        Assert.True(at >= 0);
        bytes[at] = (byte)'X';
        var changed = "v1." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<InvalidOperationException>(() => PlcRecipeActivationIdentityCodec.Decode(changed));
    }

    [Theory]
    [InlineData(6, "00000000-0000-0000-0000-000000000001")]
    [InlineData(10, "S-1-5-21-123-456-789-1001")]
    [InlineData(43, "00000000-0000-0000-0000-000000000001")]
    public void V146_I11_PlcEvidenceCannotCarryHumanCredentialOrRecoveryAttribution(int index, string attribution)
    {
        var fixture = BuildPlcFixture(PlcContext());
        var fields = ReadFields(fixture.Payload);
        fields[index] = attribution;
        Assert.Throws<InvalidOperationException>(() => IdentityAuditEvent.VerifyPayload(
            AuditCanonical.Encode("IdentityEvent", fields), 3, StationId, 31));
    }

    private const string StationId = "PlcIdentityStation";
    private const string ChangeReason = "plc-recipe-change";
    private static readonly Guid ActivationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PrincipalId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid StepUpGrantId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ReleaseId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid RuntimeEpoch = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset RecordedAt = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private static readonly RecipeReference Candidate = new("Fixture.PlcRecipe", "2", new string('B', 64));
    private static readonly RecipeContractReference DeploymentPolicy =
        new("Fixture.Authorization", "1", new string('E', 64));
    private static readonly string AuthenticationHash = new('A', 64);
    private static readonly string ReleaseRecordContentHash = new('9', 64);

    [Fact]
    public void V146_I01_HumanSchema30AndSchema31PayloadsStayByteIdenticalAnd49Fields()
    {
        var human = HumanEvent();

        var legacy = human.Encode(7, 30);
        var current = human.Encode(7, 31);

        // The opt-in schema never rewrites an existing human row.
        Assert.Equal(legacy, current);
        Assert.Equal(49, ReadFields(current).Length);
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(legacy, 7, StationId, 30));
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(current, 7, StationId, 31));
        Assert.True(IdentityAuditEvent.TryReadEventKind(current, out var kind));
        Assert.Equal(IdentityEventKind.RecipeActivationAdmitted, kind);
        Assert.Equal(human.EventId.ToString("D"), IdentityAuditEvent.DecodeEventId(current));
    }

    [Fact]
    public void V146_I02_PlcEnvelopeRoundTripsFullContextAndOuterDeploymentPolicy()
    {
        var context = PlcContext();
        var fixture = BuildPlcFixture(context);
        var fields = ReadFields(fixture.Payload);

        Assert.Equal(50, fields.Length);
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(fixture.Payload, 3, StationId, 31));
        // The outer deployment policy fields stay exactly the bound deployment policy;
        // the selection policy and map live inside the envelope instead.
        Assert.Equal(DeploymentPolicy.Id, fields[27]);
        Assert.Equal(DeploymentPolicy.Version, fields[28]);
        Assert.Equal(DeploymentPolicy.ContentHash, fields[29]);
        Assert.NotEqual(DeploymentPolicy.Id, context.SelectionPolicy.Id);

        var decoded = PlcRecipeActivationIdentityCodec.Decode(fields[49]!);
        AssertContext(context, decoded);
        Assert.Equal(fixture.Record.AuthorizationTarget, fields[37]);
        Assert.Equal(fixture.Record.OperationId.ToString("D"), fields[31]);
        Assert.Equal(fixture.Record.OperationId.ToString("D"), fields[38]);
        Assert.Equal(fixture.Record.OperationId.ToString("D"), fields[42]);
        Assert.Equal(AuditedCommandKind.ActivateRecipe.ToString(), fields[39]);
        Assert.Null(fields[5]);
        Assert.Null(fields[25]);
        Assert.Null(fields[30]);
        Assert.Null(fields[32]);
        Assert.Null(fields[33]);
        Assert.Null(fields[34]);
        Assert.Equal("0", fields[35]);

        // Decoding is deterministic and never re-points the request.
        Assert.Equal(PlcRecipeActivationIdentityCodec.Encode(decoded),
            PlcRecipeActivationIdentityCodec.Encode(context));
        Assert.True(context.Matches(decoded));
    }

    [Fact]
    public void V146_I03_PlcEnvelopeRejectsEveryHumanIdentityField()
    {
        var fixture = BuildPlcFixture(PlcContext());

        // A true system request has no human principal, session, Step-Up grant,
        // target principal, human permission, or human authorization revision.
        RejectEnvelope(fixture.Authorization with { PrincipalId = PrincipalId });
        RejectEnvelope(fixture.Authorization with { ActorPrincipalId = PrincipalId });
        RejectEnvelope(fixture.Authorization with { SessionId = SessionId });
        RejectEnvelope(fixture.Authorization with { StepUpGrantId = StepUpGrantId });
        RejectEnvelope(fixture.Authorization with { TargetPrincipalId = PrincipalId });
        RejectEnvelope(fixture.Authorization with { RequiredPermission = Permission.ActivateRecipe.ToString() });
        RejectEnvelope(fixture.Authorization with { RequiredPermission = Permission.ManageRecipeSelectionMap.ToString() });
        RejectEnvelope(fixture.Authorization with { AuthorizationRevision = 4 });

        // The unmodified row is the one accepted shape.
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(fixture.Payload, 3, StationId, 31));
    }

    [Fact]
    public void V146_I04_PlcEnvelopeIsClosedToItsEventKindsAndCommand()
    {
        var fixture = BuildPlcFixture(PlcContext());

        // Only the existing activation facts and a rejected ActivateRecipe
        // management fact may carry the envelope.
        RejectEnvelope(fixture.Authorization with { Kind = IdentityEventKind.IdentityConfigured });
        RejectEnvelope(fixture.Authorization with { Kind = IdentityEventKind.RecipeDraftSaved });
        RejectEnvelope(fixture.Authorization with { Kind = IdentityEventKind.RecipeSelectionChanged });
        RejectEnvelope(fixture.Authorization with
        {
            ActionCommandKind = AuditedCommandKind.ChangeRecipeSelection.ToString()
        });
        RejectEnvelope(fixture.Authorization with
        {
            ActionCommandKind = AuditedCommandKind.SelectHistoricalCalibration.ToString()
        });

        // The rejected management fact is an allowed carrier.
        var rejected = fixture.Authorization with { Kind = IdentityEventKind.ManagementRejected,
            ReasonCode = "RecipeActivationPlcRejected" };
        var rejectedPayload = rejected.Encode(3, 31);
        Assert.Equal(50, ReadFields(rejectedPayload).Length);
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(rejectedPayload, 3, StationId, 31));

        // An accepted admission must carry the exact correlated operation; a rejected
        // row may record the correlation without an operation of its own.
        RejectEnvelope(fixture.Authorization with { BoundCommandCorrelationId = Guid.NewGuid() });
        RejectEnvelope(fixture.Authorization with { OperationId = null });
        RejectEnvelope(fixture.Authorization with { CommandCorrelationId = Guid.NewGuid() });
        var correlated = rejected with { OperationId = null };
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(correlated.Encode(3, 31), 3, StationId, 31));
    }

    [Fact]
    public void V146_I05_MalformedTamperedAndLegacySchemaEvidenceIsRejected()
    {
        var fixture = BuildPlcFixture(PlcContext());
        var evidence = PlcRecipeActivationIdentityCodec.Encode(fixture.Context);
        Assert.StartsWith("v1.", evidence, StringComparison.Ordinal);
        Assert.Equal(evidence, PlcRecipeActivationIdentityCodec.Encode(
            PlcRecipeActivationIdentityCodec.Decode(evidence)));

        // A wrong domain, an empty value, invalid base64, and trailing encoding
        // characters can never be read as evidence.
        Assert.Equal("PlcRecipeActivationEvidenceInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                PlcRecipeActivationIdentityCodec.Decode("v2." + evidence[3..])).Message);
        Assert.Equal("PlcRecipeActivationEvidenceInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                PlcRecipeActivationIdentityCodec.Decode("")).Message);
        Assert.Equal("PlcRecipeActivationEvidenceInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                PlcRecipeActivationIdentityCodec.Decode("v1.!!!!")).Message);
        Assert.Equal("PlcRecipeActivationEvidenceInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                PlcRecipeActivationIdentityCodec.Decode(evidence + "=")).Message);
        Assert.Equal("PlcRecipeActivationEvidenceInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                PlcRecipeActivationIdentityCodec.Decode(evidence + "A")).Message);
        Assert.Throws<ArgumentNullException>(() => PlcRecipeActivationIdentityCodec.Encode(null!));

        // Every byte of the canonical envelope is covered: a changed character (a
        // hash, identifier, version, or request value) is not the same evidence.
        foreach (var index in new[] { evidence.Length / 4, evidence.Length / 2, evidence.Length - 5 })
        {
            var replacement = evidence[index] == 'A' ? 'B' : 'A';
            var tampered = evidence[..index] + replacement + evidence[(index + 1)..];
            Assert.NotEqual(evidence, tampered);
            RejectEnvelope(fixture.Authorization with { PlcRecipeActivationEvidence = tampered });
            Assert.Equal("PlcRecipeActivationEvidenceInvalid",
                Assert.Throws<InvalidOperationException>(() =>
                    PlcRecipeActivationIdentityCodec.Decode(tampered)).Message);
        }

        RejectEnvelope(fixture.Authorization with { PlcRecipeActivationEvidence = "v1.AAAA" });
        RejectEnvelope(fixture.Authorization with { PlcRecipeActivationEvidence = evidence.ToUpperInvariant() });

        // A store that cannot carry the field never silently drops it, and a
        // legacy payload never reads as a PLC request.
        Assert.Equal("AuditPlcRecipeActivationEvidenceUnsupported",
            Assert.Throws<InvalidOperationException>(() => fixture.Authorization.Encode(3, 30)).Message);
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(fixture.Payload, 3, StationId, 30)).Message);
    }

    [Fact]
    public void V146_I06_CanonicalFieldCountAndTrailingBytesStayStrict()
    {
        var fixture = BuildPlcFixture(PlcContext());
        var human = HumanEvent();
        var humanPayload = human.Encode(7, 30);

        // A 50-field row whose last field is absent must not read as evidence.
        var fields = ReadFields(fixture.Payload);
        fields[49] = null;
        Assert.Equal("AuditPlcRecipeActivationEvidenceMissing", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(AuditCanonical.Encode("IdentityEvent", fields), 3, StationId, 31)).Message);

        // Extra bytes are not part of any row.
        var extended = fixture.Payload.Concat(new byte[] { 0 }).ToArray();
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(extended, 3, StationId, 31)).Message);

        // A PLC row relabelled as 49 fields, and a human row relabelled as 50, are
        // both rejected by the declared field count alone.
        var undercount = fixture.Payload.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(undercount.AsSpan(CountOffset), 49);
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(undercount, 3, StationId, 31)).Message);
        var overcount = humanPayload.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(overcount.AsSpan(CountOffset), 50);
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(overcount, 7, StationId, 31)).Message);

        // The human row itself stays exactly 49 fields and verifies on both schemas.
        Assert.Equal(49, ReadFields(humanPayload).Length);
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(humanPayload, 7, StationId, 30));
    }

    [Fact]
    public void V146_I07_MatchesRecipeActivationAuthorizationBindsThePlcRecord()
    {
        var fixture = BuildPlcFixture(PlcContext());
        Assert.True(IdentityAuditEvent.MatchesRecipeActivationAuthorization(fixture.Payload, 3, StationId,
            fixture.Record, 31));

        // Another request sequence, selection map, runtime epoch, target, timestamp,
        // reason, operation, or deployment policy is a different action.
        Assert.False(Matches(fixture, BuildPlcFixture(Mutate(fixture.Context,
            requestSequence: fixture.Context.RequestSequence + 1)).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(Mutate(fixture.Context,
            selectionMap: new RecipeContractReference("Fixture.SelectionMap", "2", new string('4', 64)))).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(Mutate(fixture.Context,
            endpoint: new string('7', 64))).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(fixture.Context, target: new string('8', 64)).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(fixture.Context,
            recordedAt: RecordedAt.AddSeconds(1)).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(fixture.Context, reason: "RecipeActivationRetried").Record));
        Assert.False(Matches(fixture, BuildPlcFixture(fixture.Context, operationId: Guid.NewGuid()).Record));
        Assert.False(Matches(fixture, BuildPlcFixture(fixture.Context, policy: OtherPolicy()).Record));

        // A human record can never claim the PLC envelope, and a PLC record can
        // never match a human row.
        Assert.False(Matches(fixture, HumanRecord()));
        Assert.False(IdentityAuditEvent.MatchesRecipeActivationAuthorization(HumanEvent().Encode(7, 31), 7,
            StationId, fixture.Record, 31));
    }

    [Fact]
    public void V146_I08_RecipeSelectionMapChangeStaysSchema31Only()
    {
        var mapChange = MapChangeEvent();
        var payload = mapChange.Encode(1, 31);

        Assert.Equal(49, ReadFields(payload).Length);
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(payload, 1, StationId, 31));
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(payload, 1, StationId, 30)).Message);

        // The event kind and the command kind are separate schema-31 gates: the map
        // kind with an already-known command, and the map command on a management
        // rejection, are each rejected on the previous schema and accepted on 31.
        var mapKind = mapChange with { ActionCommandKind = AuditedCommandKind.ActivateRecipe.ToString() };
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(mapKind.Encode(1, 31), 1, StationId, 31));
        Assert.Equal("AuditIdentityPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(mapKind.Encode(1, 30), 1, StationId, 30)).Message);

        var mapCommand = mapChange with { Kind = IdentityEventKind.ManagementRejected };
        Assert.Equal(1, IdentityAuditEvent.VerifyPayload(mapCommand.Encode(1, 31), 1, StationId, 31));
        Assert.Equal("AuditAuthorizationPayloadInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(mapCommand.Encode(1, 30), 1, StationId, 30)).Message);
    }

    [Fact]
    public void V146_I09_PlcCommandBaselineIsBoundBeforeTheRequestContext()
    {
        var context = PlcContext();
        var invocation = new CommandInvocation(CommandSource.Integration, SystemPrincipalId.PlcAdapter, null, null);
        var baseline = new RecipeActivationReference(4, ActivationId, new string('C', 64));
        var otherBaseline = new RecipeActivationReference(5, Guid.NewGuid(), new string('D', 64));

        var withoutBaseline = new ActivateRecipeCommand(OperationId, invocation, context, ChangeReason);
        var withBaseline = new ActivateRecipeCommand(OperationId, invocation, context, baseline, ChangeReason);
        var withOtherBaseline = new ActivateRecipeCommand(OperationId, invocation, context, otherBaseline,
            ChangeReason);

        // The baseline-free overload keeps the exact PLC target it always had.
        Assert.Null(withoutBaseline.ExpectedActive);
        Assert.Equal(ActivateRecipeCommand.ComputePlcAuthorizationTarget(
            ActivateRecipeCommand.ComputeAuthorizationTarget(context.Candidate, context.ReleaseId,
                context.ReleaseRecordContentHash, null, null, ChangeReason), context),
            withoutBaseline.AuthorizationTarget);

        // The frozen baseline is bound exactly where a human command binds it, before
        // the immutable request context, so two baselines never share one target.
        Assert.Equal(baseline, withBaseline.ExpectedActive);
        Assert.Equal(ActivateRecipeCommand.ComputePlcAuthorizationTarget(
            ActivateRecipeCommand.ComputeAuthorizationTarget(context.Candidate, context.ReleaseId,
                context.ReleaseRecordContentHash, baseline, null, ChangeReason), context),
            withBaseline.AuthorizationTarget);
        Assert.NotEqual(withoutBaseline.AuthorizationTarget, withBaseline.AuthorizationTarget);
        Assert.NotEqual(withBaseline.AuthorizationTarget, withOtherBaseline.AuthorizationTarget);

        // A PLC command still admits no human identity and no calibration choice.
        Assert.Empty(withBaseline.CalibrationSelections);
        Assert.Null(withBaseline.HistoricalSelection);
        Assert.Equal(context.ContentHash, withBaseline.PlcRequestContext!.ContentHash);
    }

    private static string OtherPolicyHash() => new('F', 64);

    private static RecipeContractReference OtherPolicy() => new("Fixture.OtherAuthorization", "2", OtherPolicyHash());

    private static IdentityAuditEvent HumanEvent() => new(Guid.NewGuid(), IdentityEventKind.RecipeActivationAdmitted,
        RecordedAt, StationId, PrincipalId, null, null, null, "RecipeActivationAdmitted",
        AuthenticationPolicyId: "Fixture.Authentication", AuthenticationPolicyVersion: "1",
        AuthenticationPolicyHash: AuthenticationHash, StateRevision: 1, SessionId: SessionId,
        AuthorizationPolicyId: DeploymentPolicy.Id, AuthorizationPolicyVersion: DeploymentPolicy.Version,
        AuthorizationPolicyHash: DeploymentPolicy.ContentHash, ActorPrincipalId: PrincipalId,
        CommandCorrelationId: OperationId, StepUpGrantId: StepUpGrantId,
        RequiredPermission: Permission.ActivateRecipe.ToString(), AuthorizationRevision: 1,
        ActionTargetId: new string('C', 64), BoundCommandCorrelationId: OperationId,
        ActionCommandKind: AuditedCommandKind.ActivateRecipe.ToString(), OperationId: OperationId);

    private static IdentityAuditEvent MapChangeEvent() => new(Guid.NewGuid(),
        IdentityEventKind.RecipeSelectionChanged, RecordedAt, StationId, PrincipalId, null, null, null,
        "RecipeSelectionChanged", AuthenticationPolicyId: "Fixture.Authentication",
        AuthenticationPolicyVersion: "1", AuthenticationPolicyHash: AuthenticationHash, StateRevision: 1,
        SessionId: SessionId,
        AuthorizationPolicyId: DeploymentPolicy.Id, AuthorizationPolicyVersion: DeploymentPolicy.Version,
        AuthorizationPolicyHash: DeploymentPolicy.ContentHash, ActorPrincipalId: PrincipalId,
        CommandCorrelationId: OperationId, StepUpGrantId: StepUpGrantId,
        RequiredPermission: Permission.ManageRecipeSelectionMap.ToString(), AuthorizationRevision: 1,
        ActionTargetId: new string('C', 64), BoundCommandCorrelationId: OperationId,
        ActionCommandKind: AuditedCommandKind.ChangeRecipeSelection.ToString(), OperationId: OperationId);

    private static PlcRecipeActivationRequestContext PlcContext() => new(RuntimeEpoch, new string('A', 64),
        new RecipeContractReference("Fixture.ProtocolProfile", "1", new string('1', 64)), 7, 42, 3,
        new RecipeContractReference("Fixture.SelectionPolicy", "1", new string('2', 64)),
        new RecipeContractReference("Fixture.SelectionMap", "1", new string('3', 64)),
        Candidate, ReleaseId, ReleaseRecordContentHash);

    private static PlcRecipeActivationRequestContext Mutate(PlcRecipeActivationRequestContext context,
        string? endpoint = null, uint? requestSequence = null, RecipeContractReference? selectionMap = null) =>
        new(context.RuntimeEpoch, endpoint ?? context.EndpointContentHash, context.ProtocolProfile,
            context.ControllerEpoch, requestSequence ?? context.RequestSequence, context.SelectionCode,
            context.SelectionPolicy, selectionMap ?? context.SelectionMap, context.Candidate, context.ReleaseId,
            context.ReleaseRecordContentHash);

    private static string PlcTarget(PlcRecipeActivationRequestContext context) =>
        ActivateRecipeCommand.ComputePlcAuthorizationTarget(ActivateRecipeCommand.ComputeAuthorizationTarget(
            context.Candidate, context.ReleaseId, context.ReleaseRecordContentHash, null, null, ChangeReason),
            context);

    private static PlcFixture BuildPlcFixture(PlcRecipeActivationRequestContext context, string? target = null,
        RecipeContractReference? policy = null, string? reason = null, Guid? operationId = null,
        DateTimeOffset? recordedAt = null)
    {
        var actor = RecipeActivationActor.PlcAdapter(context);
        var effectivePolicy = policy ?? DeploymentPolicy;
        var effectiveReason = reason ?? "RecipeActivationAdmitted";
        var effectiveOperation = operationId ?? OperationId;
        var effectiveTarget = target ?? PlcTarget(context);
        var effectiveRecordedAt = recordedAt ?? RecordedAt;
        var admission = new RecipeActivationAdmission(1, ActivationId, AttemptId, effectiveOperation,
            context.Candidate, context.ReleaseId, context.ReleaseRecordContentHash, null, null, null, null, null,
            ChangeReason, actor, effectivePolicy, effectiveTarget, RecipeActivationEvidenceKind.LocalAuthority,
            effectiveRecordedAt);
        var record = new RecipeActivationRecord(1, ActivationId, AttemptId, effectiveOperation, null, null, null,
            null, context.Candidate, context.ReleaseId, context.ReleaseRecordContentHash, null,
            new RecipeActivationOutcome(RecipeActivationOutcomeState.Admitted, effectiveReason),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.LocalAuthority, actor, effectivePolicy, ChangeReason, effectiveTarget,
            effectiveRecordedAt, admission, null);
        var authorization = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.RecipeActivationAdmitted,
            effectiveRecordedAt, StationId, null, null, null, null, effectiveReason,
            AuthenticationPolicyId: "Fixture.Authentication", AuthenticationPolicyVersion: "1",
            AuthenticationPolicyHash: AuthenticationHash, StateRevision: 1,
            AuthorizationPolicyId: effectivePolicy.Id,
            AuthorizationPolicyVersion: effectivePolicy.Version, AuthorizationPolicyHash: effectivePolicy.ContentHash,
            CommandCorrelationId: effectiveOperation, ActionTargetId: effectiveTarget,
            BoundCommandCorrelationId: effectiveOperation,
            ActionCommandKind: AuditedCommandKind.ActivateRecipe.ToString(), OperationId: effectiveOperation,
            PlcRecipeActivationEvidence: PlcRecipeActivationIdentityCodec.Encode(context));
        return new(context, record, authorization, authorization.Encode(3, 31));
    }

    private static RecipeActivationRecord HumanRecord()
    {
        var admission = new RecipeActivationAdmission(1, ActivationId, AttemptId, OperationId, Candidate, ReleaseId,
            ReleaseRecordContentHash, null, null, null, null, null, ChangeReason, PrincipalId, SessionId, 1,
            DeploymentPolicy, new string('F', 64), RecipeActivationEvidenceKind.InternalContractFixture, RecordedAt);
        return new RecipeActivationRecord(1, ActivationId, AttemptId, OperationId, null, null, null, null,
            Candidate, ReleaseId, ReleaseRecordContentHash, null,
            new RecipeActivationOutcome(RecipeActivationOutcomeState.Admitted, "RecipeActivationAdmitted"),
            Array.Empty<RecipeActivationCheck>(), Restoration(), null,
            RecipeActivationEvidenceKind.InternalContractFixture, PrincipalId, SessionId, 1, DeploymentPolicy,
            ChangeReason, new string('F', 64), RecordedAt, admission, null);
    }

    private static RecipeActivationRestoration Restoration() =>
        new(RecipeActivationRestorationState.NotRequired, "RecipeActivationHardwareUntouched");

    private static bool Matches(PlcFixture fixture, RecipeActivationRecord record) =>
        IdentityAuditEvent.MatchesRecipeActivationAuthorization(fixture.Payload, 3, StationId, record, 31);

    private static void RejectEnvelope(IdentityAuditEvent value)
    {
        var payload = value.Encode(3, 31);
        Assert.Equal(50, ReadFields(payload).Length);
        Assert.Equal("AuditPlcRecipeActivationEvidenceInvalid", Assert.Throws<InvalidOperationException>(() =>
            IdentityAuditEvent.VerifyPayload(payload, 3, StationId, 31)).Message);
    }

    /// <summary>
    /// The canonical audit envelope is a shared fixed format: version, kind label,
    /// field count, then one marker-prefixed value per field. Reading it here proves
    /// the exact field count and lets a row be rebuilt with one field changed.
    /// </summary>
    private static string?[] ReadFields(byte[] payload)
    {
        var offset = 4;
        string? ReadValue()
        {
            var marker = payload[offset++];
            if (marker == 0) return null;
            Assert.Equal(1, marker);
            var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
            offset += 4;
            var value = Encoding.UTF8.GetString(payload, offset, length);
            offset += length;
            return value;
        }

        Assert.Equal("IdentityEvent", ReadValue());
        var count = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;
        var fields = Enumerable.Range(0, count).Select(_ => ReadValue()).ToArray();
        Assert.Equal(payload.Length, offset);
        return fields;
    }

    private static int CountOffset => 4 + 1 + 4 + Encoding.UTF8.GetByteCount("IdentityEvent");

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

    private sealed record PlcFixture(PlcRecipeActivationRequestContext Context, RecipeActivationRecord Record,
        IdentityAuditEvent Authorization, byte[] Payload);
}
