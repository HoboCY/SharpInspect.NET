using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SQLitePCL;

namespace SharpInspect.Runtime.Calibration
{

/// <summary>
/// One immutable session transition supplied by the calibration coordinator.  The
/// writer assigns the durable position, sequence and predecessor hash.  OperationId
/// is the command correlation used to bind the event to the signed identity audit;
/// it is deliberately repeatable across phases of one command.
/// </summary>
internal sealed record CalibrationSessionEvent
{
    internal CalibrationSessionEvent(Guid eventId, Guid sessionId, Guid operationId,
        CalibrationSessionPhase phase, CalibrationSessionOutcome outcome, string reasonCode,
        DateTimeOffset occurredAtUtc, CalibrationFrameEvidence? frame = null,
        CalibrationObservationEvidence? observation = null,
        CalibrationEvidenceExclusion? exclusion = null,
        CalibrationCandidateEvidence? candidate = null,
        CalibrationSessionCommand? authorizationCommand = null,
        CalibrationTemporaryConfigurationEvidence? temporaryConfiguration = null)
    {
        if (eventId == Guid.Empty || sessionId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("CalibrationEventIdentityInvalid");
        if (!Enum.IsDefined(typeof(CalibrationSessionPhase), phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(typeof(CalibrationSessionOutcome), outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        ReasonCode = AlgorithmContractValidation.BoundedText(reasonCode, nameof(reasonCode), 128);
        if (occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("CalibrationEventUtcRequired", nameof(occurredAtUtc));
        var supplied = (frame is null ? 0 : 1) + (observation is null ? 0 : 1) +
            (exclusion is null ? 0 : 1) + (candidate is null ? 0 : 1) +
            (temporaryConfiguration is null ? 0 : 1);
        if (supplied > 1) throw new ArgumentException("CalibrationEventPayloadMultiplicityInvalid");
        if (temporaryConfiguration is not null && phase != CalibrationSessionPhase.Configuring)
            throw new ArgumentException("CalibrationTemporaryConfigurationPhaseMismatch", nameof(temporaryConfiguration));
        if (frame is not null && frame.SessionId != sessionId)
            throw new ArgumentException("CalibrationEventFrameSessionMismatch", nameof(frame));
        if (observation is not null && observation.Frame.SessionId != sessionId)
            throw new ArgumentException("CalibrationEventObservationSessionMismatch", nameof(observation));
        if (exclusion is not null && exclusion.FrameId == Guid.Empty)
            throw new ArgumentException("CalibrationEventExclusionInvalid", nameof(exclusion));
        if (candidate is not null && candidate.SessionId != sessionId)
            throw new ArgumentException("CalibrationEventCandidateSessionMismatch", nameof(candidate));
        if (authorizationCommand is not null &&
            (authorizationCommand.CalibrationSessionId != sessionId ||
             authorizationCommand.CorrelationId != operationId))
            throw new ArgumentException("CalibrationEventCommandBindingMismatch", nameof(authorizationCommand));

        EventId = eventId;
        SessionId = sessionId;
        OperationId = operationId;
        Phase = phase;
        Outcome = outcome;
        OccurredAtUtc = occurredAtUtc;
        Frame = frame;
        Observation = observation;
        Exclusion = exclusion;
        Candidate = candidate;
        AuthorizationCommand = authorizationCommand;
        TemporaryConfiguration = temporaryConfiguration;
    }

    public Guid EventId { get; }
    public Guid SessionId { get; }
    public Guid OperationId { get; }
    public CalibrationSessionPhase Phase { get; }
    public CalibrationSessionOutcome Outcome { get; }
    public string ReasonCode { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public CalibrationFrameEvidence? Frame { get; }
    public CalibrationObservationEvidence? Observation { get; }
    public CalibrationEvidenceExclusion? Exclusion { get; }
    public CalibrationCandidateEvidence? Candidate { get; }
    internal CalibrationSessionCommand? AuthorizationCommand { get; }
    public CalibrationTemporaryConfigurationEvidence? TemporaryConfiguration { get; }

    internal bool IsAdmission => Phase == CalibrationSessionPhase.Admitted &&
        Frame is null && Observation is null && Exclusion is null && Candidate is null &&
        TemporaryConfiguration is null;

    internal string Kind => Frame is not null ? "FrameCaptured" :
        Observation is not null ? "ObservationExtracted" :
        Exclusion is not null ? "FrameExcluded" :
        Candidate is not null ? "CandidateRetained" : TemporaryConfiguration is not null
            ? "TemporaryConfigurationApplied" : Phase.ToString();
}

/// <summary>Bounded immutable read projection used by the Runtime coordinator.</summary>
internal interface ICalibrationSessionPersistence
{
    ValueTask<CalibrationSessionQueryResult> ReadCalibrationSessionAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<CalibrationSessionEvidence>> ReadOpenCalibrationSessionsAsync(
        CancellationToken cancellationToken = default);
    ValueTask<StoreWriteResult> AppendCalibrationEventAsync(CalibrationSessionEvent value,
        CommandAuditFact? terminal, StoreDeadline deadline,
        CancellationToken cancellationToken = default);
}

internal sealed class SqliteCalibrationSessionPersistence : ICalibrationSessionPersistence
{
    private readonly SqliteCommandStore _store;

    internal SqliteCalibrationSessionPersistence(SqliteCommandStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<CalibrationSessionQueryResult> ReadCalibrationSessionAsync(Guid sessionId,
        CancellationToken cancellationToken = default) =>
        _store.ReadCalibrationSessionAsync(sessionId, cancellationToken);

    public ValueTask<IReadOnlyList<CalibrationSessionEvidence>> ReadOpenCalibrationSessionsAsync(
        CancellationToken cancellationToken = default) =>
        _store.ReadOpenCalibrationSessionsAsync(cancellationToken);

    public ValueTask<StoreWriteResult> AppendCalibrationEventAsync(CalibrationSessionEvent value,
        CommandAuditFact? terminal, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        _store.AppendCalibrationEventAsync(value, terminal, cancellationToken, deadline);
}

internal static class CalibrationSessionPersistenceFactory
{
    internal static ICalibrationSessionPersistence? Create(ICommandAuditWriter? audit) =>
        audit is SqliteCommandStore { CalibrationEnabled: true } store
            ? new SqliteCalibrationSessionPersistence(store) : null;
}

internal sealed class CalibrationEventWork
{
    internal CalibrationEventWork(CalibrationSessionEvent value, CommandAuditFact? terminal)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Terminal = terminal;
    }

    internal CalibrationSessionEvent Value { get; }
    internal CommandAuditFact? Terminal { get; }
}

/// <summary>Strict, versioned JSON codec for immutable schema-14 session records.</summary>
internal static partial class CalibrationSessionStorageCodec
{
    internal const int FormatVersion = 1;
    private const int EvidenceEventFormatVersion = 2;
    internal const int MaximumEncodedChars = 700_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static bool UsesEvidenceV2(CalibrationSessionEvent value) =>
        value.Candidate?.Result.Evidence is not null || value.Observation?.Result.Receipt is not null;

    internal static byte[] EncodeHeader(CalibrationSessionHeader value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dto = HeaderDto.From(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationSessionHeaderPayloadOversized");
        return payload;
    }

    internal static CalibrationSessionHeader DecodeHeader(byte[] payload)
    {
        if (payload is null || payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationSessionHeaderPayloadInvalid");
        HeaderDto? dto;
        try { dto = JsonSerializer.Deserialize<HeaderDto>(payload, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("CalibrationSessionHeaderPayloadInvalid", ex); }
        if (dto is null) throw new InvalidOperationException("CalibrationSessionHeaderPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("CalibrationSessionHeaderCanonicalMismatch");
        return dto.ToValue();
    }

    internal static byte[] EncodeEvent(CalibrationSessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = UsesEvidenceV2(value)
            ? JsonSerializer.SerializeToUtf8Bytes(EventV2Dto.From(value), Json)
            : JsonSerializer.SerializeToUtf8Bytes(EventDto.From(value), Json);
        if (payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationSessionEventPayloadOversized");
        return payload;
    }

    internal static CalibrationSessionEvent DecodeEvent(byte[] payload)
    {
        if (payload is null || payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid");
        int formatVersion;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("FormatVersion", out var version) ||
                !version.TryGetInt32(out formatVersion))
                throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid", ex);
        }

        return formatVersion switch
        {
            FormatVersion => DecodeV1Event(payload),
            EvidenceEventFormatVersion => DecodeV2Event(payload),
            _ => throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid")
        };
    }

    private static CalibrationSessionEvent DecodeV1Event(byte[] payload)
    {
        EventDto? dto;
        try { dto = JsonSerializer.Deserialize<EventDto>(payload, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid", ex); }
        if (dto is null) throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("CalibrationSessionEventCanonicalMismatch");
        return dto.ToValue();
    }

    private static CalibrationSessionEvent DecodeV2Event(byte[] payload)
    {
        EventV2Dto? dto;
        try { dto = JsonSerializer.Deserialize<EventV2Dto>(payload, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid", ex); }
        if (dto is null) throw new InvalidOperationException("CalibrationSessionEventPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("CalibrationSessionEventCanonicalMismatch");
        return dto.ToValue();
    }

    internal static string PayloadHash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    internal static string EventContentHash(CalibrationSessionEvent value, long sequence,
        string? previousHash, byte[] payload, long authorizationAuditSequence, string authorizationAuditHash) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            UsesEvidenceV2(value)
                ? "sharpinspect-calibration-session-event-v2"
                : "sharpinspect-calibration-session-event-v1", value.SessionId.ToString("D"),
            value.EventId.ToString("D"), value.OperationId.ToString("D"), sequence.ToString(CultureInfo.InvariantCulture),
            value.Kind, value.Phase.ToString(), value.Outcome.ToString(), value.ReasonCode,
            value.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), previousHash,
            PayloadHash(payload), authorizationAuditSequence.ToString(CultureInfo.InvariantCulture),
            authorizationAuditHash
        });

    internal static string SessionContentHash(CalibrationSessionHeader header, long position,
        byte[] payload, long authorizationAuditSequence, string authorizationAuditHash) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-session-v1", header.SessionId.ToString("D"),
            position.ToString(CultureInfo.InvariantCulture), header.ContentHash, PayloadHash(payload),
            authorizationAuditSequence.ToString(CultureInfo.InvariantCulture), authorizationAuditHash
        });

    internal static string FrameContentHash(CalibrationFrameEvidence frame, long position,
        byte[] payload, long authorizationAuditSequence, string authorizationAuditHash) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-frame-manifest-v1", frame.SessionId.ToString("D"),
            frame.FrameId.ToString("D"), position.ToString(CultureInfo.InvariantCulture), frame.SourceHash,
            frame.RelativePath, frame.ByteLength.ToString(CultureInfo.InvariantCulture), frame.PixelHash,
            PayloadHash(payload), authorizationAuditSequence.ToString(CultureInfo.InvariantCulture), authorizationAuditHash
        });

    internal static byte[] EncodeFrameManifest(CalibrationFrameEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(FrameDto.From(value), Json);
        if (payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationFrameManifestPayloadOversized");
        return payload;
    }

    internal static CalibrationFrameEvidence DecodeFrameManifest(byte[] payload)
    {
        if (payload is null || payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
            throw new InvalidOperationException("CalibrationFrameManifestPayloadInvalid");
        FrameDto? dto;
        try { dto = JsonSerializer.Deserialize<FrameDto>(payload, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("CalibrationFrameManifestPayloadInvalid", ex); }
        if (dto is null) throw new InvalidOperationException("CalibrationFrameManifestPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("CalibrationFrameManifestCanonicalMismatch");
        return dto.ToValue();
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class HeaderDto
    {
        public int FormatVersion { get; set; }
        public string? SessionId { get; set; }
        public string? RuntimeEpoch { get; set; }
        public string? AdmissionAttemptId { get; set; }
        public string? ActorPrincipalId { get; set; }
        public string? InteractiveSessionId { get; set; }
        public long AuthorizationRevision { get; set; }
        public string? AdmittedAtUtc { get; set; }
        public StartCommandDto? Command { get; set; }
        public BindingDto? Binding { get; set; }
        public ConfigurationDto? BaselineRequested { get; set; }
        public ConfigurationDto? BaselineEffective { get; set; }
        public string? DevelopmentFixtureHash { get; set; }
        public string? ContentHash { get; set; }

        internal static HeaderDto From(CalibrationSessionHeader value) => new()
        {
            FormatVersion = CalibrationSessionStorageCodec.FormatVersion,
            SessionId = value.SessionId.ToString("D"), RuntimeEpoch = value.RuntimeEpoch.ToString("D"),
            AdmissionAttemptId = value.AdmissionAttemptId.ToString("D"),
            ActorPrincipalId = value.ActorPrincipalId.ToString("D"),
            InteractiveSessionId = value.InteractiveSessionId.ToString("D"),
            AuthorizationRevision = value.AuthorizationRevision,
            AdmittedAtUtc = value.AdmittedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Command = StartCommandDto.From(value.Command), Binding = BindingDto.From(value.Binding),
            BaselineRequested = ConfigurationDto.From(value.BaselineRequested),
            BaselineEffective = ConfigurationDto.From(value.BaselineEffective),
            DevelopmentFixtureHash = value.DevelopmentFixtureHash, ContentHash = value.ContentHash
        };

        internal CalibrationSessionHeader ToValue()
        {
            if (FormatVersion != CalibrationSessionStorageCodec.FormatVersion ||
                !Guid.TryParseExact(SessionId, "D", out var sessionId) || sessionId == Guid.Empty ||
                !Guid.TryParseExact(RuntimeEpoch, "D", out var runtimeEpoch) || runtimeEpoch == Guid.Empty ||
                !Guid.TryParseExact(AdmissionAttemptId, "D", out var attemptId) || attemptId == Guid.Empty ||
                !Guid.TryParseExact(ActorPrincipalId, "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(InteractiveSessionId, "D", out var interactive) || interactive == Guid.Empty ||
                !DateTimeOffset.TryParseExact(AdmittedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var admittedAt) || admittedAt.Offset != TimeSpan.Zero ||
                Binding is null || Command is null || BaselineRequested is null || BaselineEffective is null ||
                DevelopmentFixtureHash is null || ContentHash is null)
                throw new InvalidOperationException("CalibrationSessionHeaderPayloadInvalid");
            var command = Command.ToValue();
            var binding = Binding.ToValue();
            var requested = BaselineRequested.ToRequested();
            var effective = BaselineEffective.ToEffective();
            var header = new CalibrationSessionHeader(sessionId, runtimeEpoch, attemptId, actor, interactive,
                AuthorizationRevision, admittedAt, command, binding, requested, effective, DevelopmentFixtureHash);
            if (!string.Equals(header.ContentHash, ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("CalibrationSessionHeaderHashMismatch");
            return header;
        }
    }

    private sealed class StartCommandDto
    {
        public string? CorrelationId { get; set; }
        public InvocationDto? Invocation { get; set; }
        public PlanDto? Plan { get; set; }
        public long ExpectedBindingRevision { get; set; }
        public string? ExpectedBindingRevisionHash { get; set; }
        public ImagingSetupDto? ExpectedImagingSetup { get; set; }
        public string? Reason { get; set; }

        internal static StartCommandDto From(StartCalibrationSessionCommand value) => new()
        {
            CorrelationId = value.CorrelationId.ToString("D"), Invocation = InvocationDto.From(value.Invocation),
            Plan = PlanDto.From(value.Plan), ExpectedBindingRevision = value.ExpectedBindingRevision,
            ExpectedBindingRevisionHash = value.ExpectedBindingRevisionHash,
            ExpectedImagingSetup = ImagingSetupDto.From(value.ExpectedImagingSetup), Reason = value.Reason
        };

        internal StartCalibrationSessionCommand ToValue()
        {
            if (!Guid.TryParseExact(CorrelationId, "D", out var correlation) || correlation == Guid.Empty ||
                Invocation is null || Plan is null || ExpectedImagingSetup is null ||
                ExpectedBindingRevisionHash is null || Reason is null)
                throw new InvalidOperationException("CalibrationSessionCommandPayloadInvalid");
            return new StartCalibrationSessionCommand(correlation, Invocation.ToValue(), Plan.ToValue(),
                ExpectedBindingRevision, ExpectedBindingRevisionHash, ExpectedImagingSetup.ToValue(), Reason);
        }
    }

    private sealed class InvocationDto
    {
        public int Source { get; set; }
        public string? PrincipalId { get; set; }
        public string? SessionId { get; set; }
        public string? StepUpGrantId { get; set; }

        internal static InvocationDto From(CommandInvocation value) => new()
        {
            Source = (int)value.Source, PrincipalId = value.PrincipalId,
            SessionId = value.SessionId?.ToString("D"), StepUpGrantId = value.StepUpGrantId?.ToString("D")
        };

        internal CommandInvocation ToValue() => new((CommandSource)Source, PrincipalId,
            ParseNullableGuid(SessionId, "CalibrationInvocationSessionInvalid"),
            ParseNullableGuid(StepUpGrantId, "CalibrationInvocationGrantInvalid"));
    }

    private sealed class PlanDto
    {
        public RequirementDto? Requirement { get; set; }
        public ProcedureDto? Procedure { get; set; }
        public InputDto? Input { get; set; }
        public ConfigurationDto? TemporaryConfiguration { get; set; }
        public SelectionPolicyDto? SelectionPolicy { get; set; }

        internal static PlanDto From(CalibrationSessionPlan value) => new()
        {
            Requirement = RequirementDto.From(value.Requirement), Procedure = ProcedureDto.From(value.Procedure),
            Input = InputDto.From(value.Input), TemporaryConfiguration = ConfigurationDto.From(value.TemporaryConfiguration),
            SelectionPolicy = SelectionPolicyDto.From(value.SelectionPolicy)
        };

        internal CalibrationSessionPlan ToValue()
        {
            if (Requirement is null || Procedure is null || Input is null || TemporaryConfiguration is null ||
                SelectionPolicy is null) throw new InvalidOperationException("CalibrationSessionPlanPayloadInvalid");
            return new CalibrationSessionPlan(Requirement.ToValue(), Procedure.ToValue(), Input.ToValue(),
                TemporaryConfiguration.ToRequested(), SelectionPolicy.ToValue());
        }
    }

    private sealed class RequirementDto
    {
        public string? LogicalCameraRole { get; set; }
        public int Kind { get; set; }
        public string? LogicalPurpose { get; set; }
        public ContractDto? CoefficientContract { get; set; }
        public ContractDto? AcceptancePolicy { get; set; }

        internal static RequirementDto From(CalibrationRequirement value) => new()
        {
            LogicalCameraRole = value.LogicalCameraRole, Kind = (int)value.Kind, LogicalPurpose = value.LogicalPurpose,
            CoefficientContract = ContractDto.From(value.CoefficientContract), AcceptancePolicy = ContractDto.From(value.AcceptancePolicy)
        };

        internal CalibrationRequirement ToValue() => new(LogicalCameraRole ?? string.Empty, (CalibrationKind)Kind,
            LogicalPurpose ?? string.Empty, CoefficientContract?.ToValue() ?? throw Invalid("CalibrationRequirementCoefficientMissing"),
            AcceptancePolicy?.ToValue() ?? throw Invalid("CalibrationRequirementAcceptanceMissing"));
    }

    private sealed class ProcedureDto
    {
        public ContractDto? Procedure { get; set; }
        public ContractDto? InputContract { get; set; }
        public int CalibrationKind { get; set; }

        internal static ProcedureDto From(CalibrationProcedureDescriptor value) => new()
        {
            Procedure = ContractDto.From(value.Procedure), InputContract = ContractDto.From(value.InputContract),
            CalibrationKind = (int)value.CalibrationKind
        };

        internal CalibrationProcedureDescriptor ToValue() => new(
            Procedure?.ToValue() ?? throw Invalid("CalibrationProcedureMissing"),
            InputContract?.ToValue() ?? throw Invalid("CalibrationProcedureInputContractMissing"),
            (CalibrationKind)CalibrationKind);
    }

    private sealed class InputDto
    {
        public ContractDto? InputContract { get; set; }
        public string? CanonicalBytes { get; set; }

        internal static InputDto From(CalibrationProcedureInputPayload value) => new()
        {
            InputContract = ContractDto.From(value.InputContract), CanonicalBytes = Convert.ToBase64String(value.GetBytes())
        };

        internal CalibrationProcedureInputPayload ToValue()
        {
            if (InputContract is null || CanonicalBytes is null)
                throw Invalid("CalibrationProcedureInputPayloadInvalid");
            return new CalibrationProcedureInputPayload(InputContract.ToValue(), DecodeBytes(CanonicalBytes,
                "CalibrationProcedureInputPayloadInvalid"));
        }
    }

    private sealed class ContractDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? ContentHash { get; set; }
        internal static ContractDto From(RecipeContractReference value) => new()
        { Id = value.Id, Version = value.Version, ContentHash = value.ContentHash };
        internal RecipeContractReference ToValue() => new(Id ?? string.Empty, Version ?? string.Empty, ContentHash ?? string.Empty);
    }

    private sealed class SelectionPolicyDto
    {
        public ContractDto? Contract { get; set; }
        public int MinimumFrames { get; set; }
        public int MinimumFeaturesPerFrame { get; set; }
        public double MinimumImageCoverage { get; set; }
        internal static SelectionPolicyDto From(CalibrationEvidenceSelectionPolicy value) => new()
        { Contract = new ContractDto { Id = value.Id, Version = value.Version, ContentHash = value.ContentHash }, MinimumFrames = value.MinimumFrames,
            MinimumFeaturesPerFrame = value.MinimumFeaturesPerFrame, MinimumImageCoverage = value.MinimumImageCoverage };
        internal CalibrationEvidenceSelectionPolicy ToValue()
        {
            var policy = new CalibrationEvidenceSelectionPolicy(Contract?.Id ?? string.Empty,
                Contract?.Version ?? string.Empty, MinimumFrames, MinimumFeaturesPerFrame,
                MinimumImageCoverage);
            if (Contract?.ContentHash != policy.ContentHash)
                throw Invalid("CalibrationSelectionPolicyHashMismatch");
            return policy;
        }
    }

    private sealed class ConfigurationDto
    {
        public int ProductionAcquisitionMode { get; set; }
        public double ExposureTimeUs { get; set; }
        public double GainDb { get; set; }
        public RoiDto? RegionOfInterest { get; set; }
        public int PixelFormat { get; set; }
        public int? ValidBits { get; set; }
        public int AcquisitionTimeoutMs { get; set; }
        public double TriggerDelayUs { get; set; }
        public WhiteBalanceDto? WhiteBalanceRgb { get; set; }

        internal static ConfigurationDto From(RequestedCameraConfiguration value) => new()
        {
            ProductionAcquisitionMode = (int)value.ProductionAcquisitionMode,
            ExposureTimeUs = value.ExposureTimeUs, GainDb = value.GainDb,
            RegionOfInterest = RoiDto.From(value.RegionOfInterest), PixelFormat = (int)value.PixelFormat,
            ValidBits = value.ValidBits, AcquisitionTimeoutMs = value.AcquisitionTimeoutMs,
            TriggerDelayUs = value.TriggerDelayUs, WhiteBalanceRgb = WhiteBalanceDto.From(value.WhiteBalanceRgb)
        };

        internal static ConfigurationDto From(EffectiveCameraConfiguration value) => new()
        {
            ProductionAcquisitionMode = (int)value.ProductionAcquisitionMode,
            ExposureTimeUs = value.ExposureTimeUs, GainDb = value.GainDb,
            RegionOfInterest = RoiDto.From(value.RegionOfInterest), PixelFormat = (int)value.PixelFormat,
            ValidBits = value.ValidBits, AcquisitionTimeoutMs = value.AcquisitionTimeoutMs,
            TriggerDelayUs = value.TriggerDelayUs, WhiteBalanceRgb = WhiteBalanceDto.From(value.WhiteBalanceRgb)
        };

        internal RequestedCameraConfiguration ToRequested() => new((ProductionAcquisitionMode)ProductionAcquisitionMode,
            ExposureTimeUs, GainDb, RegionOfInterest?.ToValue() ?? throw Invalid("CalibrationConfigurationRoiMissing"),
            (VisionPixelFormat)PixelFormat, ValidBits, AcquisitionTimeoutMs, TriggerDelayUs,
            WhiteBalanceRgb?.ToValue());

        internal EffectiveCameraConfiguration ToEffective() => new((ProductionAcquisitionMode)ProductionAcquisitionMode,
            ExposureTimeUs, GainDb, RegionOfInterest?.ToValue() ?? throw Invalid("CalibrationConfigurationRoiMissing"),
            (VisionPixelFormat)PixelFormat, ValidBits, AcquisitionTimeoutMs, TriggerDelayUs,
            WhiteBalanceRgb?.ToValue());
    }

    private sealed class RoiDto
    {
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        internal static RoiDto From(RegionOfInterest value) => new()
        { OffsetX = value.OffsetX, OffsetY = value.OffsetY, Width = value.Width, Height = value.Height };
        internal RegionOfInterest ToValue() => new(OffsetX, OffsetY, Width, Height);
    }

    private sealed class WhiteBalanceDto
    {
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }
        internal static WhiteBalanceDto? From(WhiteBalanceRgb? value) => value is null ? null : new()
        { Red = value.Red, Green = value.Green, Blue = value.Blue };
        internal WhiteBalanceRgb ToValue() => new(Red, Green, Blue);
    }

    private sealed class BindingDto
    {
        public long Position { get; set; }
        public string? LogicalRole { get; set; }
        public long Revision { get; set; }
        public string? OperationId { get; set; }
        public string? PreviousRevisionHash { get; set; }
        public string? RevisionHash { get; set; }
        public TargetDto? Target { get; set; }
        public string? AuthorPrincipalId { get; set; }
        public string? AuthorSessionId { get; set; }
        public long AuthorAuthorizationRevision { get; set; }
        public string? ChangeReason { get; set; }
        public string? RecordedAtUtc { get; set; }

        internal static BindingDto From(CameraBindingRevision value) => new()
        {
            Position = value.Position, LogicalRole = value.LogicalRole, Revision = value.Revision,
            OperationId = value.OperationId.ToString("D"), PreviousRevisionHash = value.PreviousRevisionHash,
            RevisionHash = value.RevisionHash, Target = TargetDto.From(value.Target),
            AuthorPrincipalId = value.AuthorPrincipalId.ToString("D"), AuthorSessionId = value.AuthorSessionId.ToString("D"),
            AuthorAuthorizationRevision = value.AuthorAuthorizationRevision, ChangeReason = value.ChangeReason,
            RecordedAtUtc = value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };

        internal CameraBindingRevision ToValue()
        {
            if (!Guid.TryParseExact(OperationId, "D", out var operationId) || operationId == Guid.Empty ||
                !Guid.TryParseExact(AuthorPrincipalId, "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(AuthorSessionId, "D", out var session) || session == Guid.Empty ||
                Target is null || !DateTimeOffset.TryParseExact(RecordedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var recorded) || recorded.Offset != TimeSpan.Zero)
                throw Invalid("CalibrationBindingPayloadInvalid");
            return new CameraBindingRevision(Position, LogicalRole ?? string.Empty, Revision, operationId,
                PreviousRevisionHash, RevisionHash ?? string.Empty, Target.ToValue(), principal, session,
                AuthorAuthorizationRevision, ChangeReason ?? string.Empty, recorded);
        }
    }

    private sealed class TemporaryConfigurationDto
    {
        public ConfigurationDto? Requested { get; set; }
        public ConfigurationDto? Effective { get; set; }
        public string? RequestedHash { get; set; }
        public string? EffectiveHash { get; set; }
        public string? ContentHash { get; set; }

        internal static TemporaryConfigurationDto? From(CalibrationTemporaryConfigurationEvidence? value) =>
            value is null ? null : new()
            {
                Requested = ConfigurationDto.From(value.Requested),
                Effective = ConfigurationDto.From(value.Effective),
                RequestedHash = value.RequestedHash,
                EffectiveHash = value.EffectiveHash,
                ContentHash = value.ContentHash
            };

        internal CalibrationTemporaryConfigurationEvidence ToValue()
        {
            if (Requested is null || Effective is null || RequestedHash is null ||
                EffectiveHash is null || ContentHash is null)
                throw Invalid("CalibrationTemporaryConfigurationPayloadInvalid");
            var value = new CalibrationTemporaryConfigurationEvidence(Requested.ToRequested(), Effective.ToEffective());
            if (value.RequestedHash != RequestedHash || value.EffectiveHash != EffectiveHash ||
                value.ContentHash != ContentHash)
                throw Invalid("CalibrationTemporaryConfigurationHashMismatch");
            return value;
        }
    }

    private sealed class TargetDto
    {
        public ProviderDto? Provider { get; set; }
        public string? StableDeviceIdentity { get; set; }
        public string? ContentHash { get; set; }
        internal static TargetDto From(CameraBindingTarget value) => new()
        { Provider = ProviderDto.From(value.Provider), StableDeviceIdentity = value.StableDeviceIdentity, ContentHash = value.ContentHash };
        internal CameraBindingTarget ToValue()
        {
            var target = new CameraBindingTarget(Provider?.ToValue() ?? throw Invalid("CalibrationTargetProviderMissing"),
                StableDeviceIdentity ?? string.Empty);
            if (target.ContentHash != ContentHash) throw Invalid("CalibrationTargetHashMismatch");
            return target;
        }
    }

    private sealed class ProviderDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? AdapterPackageId { get; set; }
        public string? AdapterVersion { get; set; }
        internal static ProviderDto From(CameraProviderIdentity value) => new()
        { Id = value.Id, Version = value.Version, AdapterPackageId = value.AdapterPackageId, AdapterVersion = value.AdapterVersion };
        internal CameraProviderIdentity ToValue() => new(Id ?? string.Empty, Version ?? string.Empty,
            AdapterPackageId ?? string.Empty, AdapterVersion ?? string.Empty);
    }

    private sealed class ImagingSetupDto
    {
        public string? LogicalCameraRole { get; set; }
        public string? RevisionId { get; set; }
        public long Revision { get; set; }
        public string? RevisionHash { get; set; }
        internal static ImagingSetupDto From(ImagingSetupRevisionReference value) => new()
        { LogicalCameraRole = value.LogicalCameraRole, RevisionId = value.RevisionId.ToString("D"), Revision = value.Revision, RevisionHash = value.RevisionHash };
        internal ImagingSetupRevisionReference ToValue() =>
            Guid.TryParseExact(RevisionId, "D", out var id) && id != Guid.Empty
                ? new ImagingSetupRevisionReference(LogicalCameraRole ?? string.Empty, id, Revision, RevisionHash ?? string.Empty)
                : throw Invalid("CalibrationImagingSetupPayloadInvalid");
    }

    private sealed class AuthorizationCommandDto
    {
        public int CommandKind { get; set; }
        public string? CorrelationId { get; set; }
        public InvocationDto? Invocation { get; set; }
        public string? SessionId { get; set; }
        public string? FrameId { get; set; }
        public string? Reason { get; set; }
        public bool Cancel { get; set; }

        internal static AuthorizationCommandDto? From(CalibrationSessionCommand? value)
        {
            if (value is null) return null;
            var kind = value switch
            {
                CaptureCalibrationFrameCommand => AuditedCommandKind.CaptureCalibrationFrame,
                ExcludeCalibrationFrameCommand => AuditedCommandKind.ExcludeCalibrationFrame,
                ComputeCalibrationCandidateCommand => AuditedCommandKind.ComputeCalibrationCandidate,
                ExitCalibrationSessionCommand => AuditedCommandKind.ExitCalibrationSession,
                _ => throw new InvalidOperationException("CalibrationCommandKindInvalid")
            };
            return new AuthorizationCommandDto
            {
                CommandKind = (int)kind, CorrelationId = value.CorrelationId.ToString("D"),
                Invocation = InvocationDto.From(value.Invocation), SessionId = value.CalibrationSessionId.ToString("D"),
                FrameId = value is ExcludeCalibrationFrameCommand exclude ? exclude.FrameId.ToString("D") : null,
                Reason = value switch
                {
                    ExcludeCalibrationFrameCommand excluded => excluded.Reason,
                    ExitCalibrationSessionCommand exit => exit.Reason,
                    _ => null
                },
                Cancel = value is ExitCalibrationSessionCommand ending && ending.Cancel
            };
        }

        internal CalibrationSessionCommand ToValue()
        {
            if (!Enum.IsDefined(typeof(AuditedCommandKind), CommandKind) ||
                (AuditedCommandKind)CommandKind is not (AuditedCommandKind.CaptureCalibrationFrame or
                    AuditedCommandKind.ExcludeCalibrationFrame or AuditedCommandKind.ComputeCalibrationCandidate or
                    AuditedCommandKind.ExitCalibrationSession) ||
                !Guid.TryParseExact(CorrelationId, "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(SessionId, "D", out var session) || session == Guid.Empty || Invocation is null)
                throw Invalid("CalibrationAuthorizationCommandInvalid");
            var invocation = Invocation.ToValue();
            if (!Enum.IsDefined(typeof(CommandSource), invocation.Source))
                throw Invalid("CalibrationAuthorizationCommandInvalid");
            return (AuditedCommandKind)CommandKind switch
            {
                AuditedCommandKind.CaptureCalibrationFrame => new CaptureCalibrationFrameCommand(correlation,
                    invocation, session),
                AuditedCommandKind.ExcludeCalibrationFrame =>
                    Guid.TryParseExact(FrameId, "D", out var frame) && frame != Guid.Empty &&
                    Reason is { Length: > 0 and <= 512 } excludeReason
                        ? new ExcludeCalibrationFrameCommand(correlation, invocation, session, frame, excludeReason)
                        : throw Invalid("CalibrationAuthorizationCommandInvalid"),
                AuditedCommandKind.ComputeCalibrationCandidate => new ComputeCalibrationCandidateCommand(
                    correlation, invocation, session),
                AuditedCommandKind.ExitCalibrationSession => Reason is { Length: > 0 and <= 512 } exitReason
                    ? new ExitCalibrationSessionCommand(correlation, invocation, session, exitReason, Cancel)
                    : throw Invalid("CalibrationAuthorizationCommandInvalid"),
                _ => throw Invalid("CalibrationAuthorizationCommandInvalid")
            };
        }
    }

    private sealed class EventDto
    {
        public int FormatVersion { get; set; }
        public string? EventId { get; set; }
        public string? SessionId { get; set; }
        public string? OperationId { get; set; }
        public int Phase { get; set; }
        public int Outcome { get; set; }
        public string? ReasonCode { get; set; }
        public string? OccurredAtUtc { get; set; }
        public FrameDto? Frame { get; set; }
        public ObservationDto? Observation { get; set; }
        public ExclusionDto? Exclusion { get; set; }
        public CandidateDto? Candidate { get; set; }
        public AuthorizationCommandDto? AuthorizationCommand { get; set; }
        public TemporaryConfigurationDto? TemporaryConfiguration { get; set; }

        internal static EventDto From(CalibrationSessionEvent value) => new()
        {
            FormatVersion = CalibrationSessionStorageCodec.FormatVersion, EventId = value.EventId.ToString("D"), SessionId = value.SessionId.ToString("D"),
            OperationId = value.OperationId.ToString("D"), Phase = (int)value.Phase, Outcome = (int)value.Outcome,
            ReasonCode = value.ReasonCode, OccurredAtUtc = value.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Frame = FrameDto.From(value.Frame), Observation = ObservationDto.From(value.Observation),
            Exclusion = ExclusionDto.From(value.Exclusion), Candidate = CandidateDto.From(value.Candidate),
            AuthorizationCommand = AuthorizationCommandDto.From(value.AuthorizationCommand),
            TemporaryConfiguration = TemporaryConfigurationDto.From(value.TemporaryConfiguration)
        };

        internal CalibrationSessionEvent ToValue()
        {
            if (FormatVersion != CalibrationSessionStorageCodec.FormatVersion ||
                !Guid.TryParseExact(EventId, "D", out var eventId) || eventId == Guid.Empty ||
                !Guid.TryParseExact(SessionId, "D", out var sessionId) || sessionId == Guid.Empty ||
                !Guid.TryParseExact(OperationId, "D", out var operationId) || operationId == Guid.Empty ||
                !DateTimeOffset.TryParseExact(OccurredAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var occurred) || occurred.Offset != TimeSpan.Zero)
                throw Invalid("CalibrationSessionEventPayloadInvalid");
            return new CalibrationSessionEvent(eventId, sessionId, operationId, (CalibrationSessionPhase)Phase,
                (CalibrationSessionOutcome)Outcome, ReasonCode ?? string.Empty, occurred,
                Frame?.ToValue(), Observation?.ToValue(), Exclusion?.ToValue(), Candidate?.ToValue(),
                AuthorizationCommand?.ToValue(), TemporaryConfiguration?.ToValue());
        }
    }

    /// <summary>
    /// V2 is used when a candidate carries computation evidence or an observation
    /// carries an extractor receipt. Keeping separate DTOs leaves the V1 computation
    /// and extraction shapes untouched, including their null omission-free JSON.
    /// </summary>
    private sealed class EventV2Dto
    {
        public int FormatVersion { get; set; }
        public string? EventId { get; set; }
        public string? SessionId { get; set; }
        public string? OperationId { get; set; }
        public int Phase { get; set; }
        public int Outcome { get; set; }
        public string? ReasonCode { get; set; }
        public string? OccurredAtUtc { get; set; }
        public FrameDto? Frame { get; set; }
        public ObservationV2Dto? Observation { get; set; }
        public ExclusionDto? Exclusion { get; set; }
        public CandidateV2Dto? Candidate { get; set; }
        public AuthorizationCommandDto? AuthorizationCommand { get; set; }
        public TemporaryConfigurationDto? TemporaryConfiguration { get; set; }

        internal static EventV2Dto From(CalibrationSessionEvent value) => new()
        {
            FormatVersion = EvidenceEventFormatVersion,
            EventId = value.EventId.ToString("D"), SessionId = value.SessionId.ToString("D"),
            OperationId = value.OperationId.ToString("D"), Phase = (int)value.Phase,
            Outcome = (int)value.Outcome, ReasonCode = value.ReasonCode,
            OccurredAtUtc = value.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Frame = FrameDto.From(value.Frame), Observation = ObservationV2Dto.From(value.Observation),
            Exclusion = ExclusionDto.From(value.Exclusion), Candidate = CandidateV2Dto.From(value.Candidate),
            AuthorizationCommand = AuthorizationCommandDto.From(value.AuthorizationCommand),
            TemporaryConfiguration = TemporaryConfigurationDto.From(value.TemporaryConfiguration)
        };

        internal CalibrationSessionEvent ToValue()
        {
            if (FormatVersion != EvidenceEventFormatVersion ||
                !Guid.TryParseExact(EventId, "D", out var eventId) || eventId == Guid.Empty ||
                !Guid.TryParseExact(SessionId, "D", out var sessionId) || sessionId == Guid.Empty ||
                !Guid.TryParseExact(OperationId, "D", out var operationId) || operationId == Guid.Empty ||
                !DateTimeOffset.TryParseExact(OccurredAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var occurred) || occurred.Offset != TimeSpan.Zero ||
                (Candidate?.Result?.Evidence is null && Observation?.Result?.Receipt is null))
                throw Invalid("CalibrationSessionEventPayloadInvalid");
            return new CalibrationSessionEvent(eventId, sessionId, operationId,
                (CalibrationSessionPhase)Phase, (CalibrationSessionOutcome)Outcome,
                ReasonCode ?? string.Empty, occurred, Frame?.ToValue(), Observation?.ToValue(),
                Exclusion?.ToValue(), Candidate?.ToValue(), AuthorizationCommand?.ToValue(),
                TemporaryConfiguration?.ToValue());
        }
    }

    private sealed class FrameDto
    {
        public string? SessionId { get; set; }
        public string? FrameId { get; set; }
        public FrameMetadataDto? Metadata { get; set; }
        public ProvenanceDto? Provenance { get; set; }
        public int SourceStrideBytes { get; set; }
        public string? PixelHash { get; set; }
        public long ByteLength { get; set; }
        public string? RelativePath { get; set; }

        internal static FrameDto? From(CalibrationFrameEvidence? value) => value is null ? null : new()
        {
            SessionId = value.SessionId.ToString("D"), FrameId = value.FrameId.ToString("D"),
            Metadata = FrameMetadataDto.From(value.Metadata), Provenance = ProvenanceDto.From(value.Provenance),
            SourceStrideBytes = value.SourceStrideBytes, PixelHash = value.PixelHash,
            ByteLength = value.ByteLength, RelativePath = value.RelativePath
        };

        internal CalibrationFrameEvidence ToValue()
        {
            if (!Guid.TryParseExact(SessionId, "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(FrameId, "D", out var frame) || frame == Guid.Empty ||
                Metadata is null || Provenance is null)
                throw Invalid("CalibrationFramePayloadInvalid");
            return new CalibrationFrameEvidence(session, frame, Metadata.ToValue(), Provenance.ToValue(),
                PixelHash ?? string.Empty, ByteLength, RelativePath ?? string.Empty, SourceStrideBytes);
        }
    }

    private sealed class FrameMetadataDto
    {
        public int CorrelationKind { get; set; }
        public string? CorrelationValue { get; set; }
        public string? LogicalCameraRole { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int StrideBytes { get; set; }
        public int PixelFormat { get; set; }
        public int? ValidBits { get; set; }
        public string? HostCaptureUtc { get; set; }
        public ConfigurationDto? EffectiveConfiguration { get; set; }

        internal static FrameMetadataDto From(FrameMetadata value) => new()
        {
            CorrelationKind = (int)value.Correlation.Kind, CorrelationValue = value.Correlation.Value.ToString("D"),
            LogicalCameraRole = value.LogicalCameraRole, Width = value.Width, Height = value.Height,
            StrideBytes = value.StrideBytes, PixelFormat = (int)value.PixelFormat, ValidBits = value.ValidBits,
            HostCaptureUtc = value.HostCaptureUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            EffectiveConfiguration = ConfigurationDto.From(value.EffectiveCameraConfiguration)
        };

        internal FrameMetadata ToValue()
        {
            if (!Guid.TryParseExact(CorrelationValue, "D", out var correlation) || correlation == Guid.Empty ||
                !DateTimeOffset.TryParseExact(HostCaptureUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var captured) || captured.Offset != TimeSpan.Zero ||
                EffectiveConfiguration is null)
                throw Invalid("CalibrationFrameMetadataPayloadInvalid");
            return new FrameMetadata(new ExecutionCorrelationId((ExecutionKind)CorrelationKind, correlation),
                LogicalCameraRole ?? string.Empty, Width, Height, StrideBytes, (VisionPixelFormat)PixelFormat,
                ValidBits, captured, EffectiveConfiguration.ToEffective());
        }
    }

    private sealed class ProvenanceDto
    {
        public int CorrelationKind { get; set; }
        public string? CorrelationValue { get; set; }
        public string? ProviderId { get; set; }
        public string? ProviderVersion { get; set; }
        public string? AdapterId { get; set; }
        public string? AdapterVersion { get; set; }
        public string? SdkId { get; set; }
        public string? SdkVersion { get; set; }
        public string? NativeRuntimeVersion { get; set; }
        public string? StableDeviceIdentity { get; set; }
        public string? ReportedModel { get; set; }
        public string? FirmwareVersion { get; set; }
        public string? NativePixelFormatDescription { get; set; }
        public string? NormalizationDetails { get; set; }
        public bool NormalizationAllocated { get; set; }
        public bool NormalizationTransformed { get; set; }
        public DeviceTimestampDto? DeviceTimestamp { get; set; }
        public ulong? FrameCounter { get; set; }
        public MilestonesDto? Milestones { get; set; }
        public PoolCopyDto? PoolCopy { get; set; }

        internal static ProvenanceDto From(FrameProvenance value) => new()
        {
            CorrelationKind = (int)value.Correlation.Kind, CorrelationValue = value.Correlation.Value.ToString("D"),
            ProviderId = value.ProviderId, ProviderVersion = value.ProviderVersion, AdapterId = value.AdapterId,
            AdapterVersion = value.AdapterVersion, SdkId = value.SdkId, SdkVersion = value.SdkVersion,
            NativeRuntimeVersion = value.NativeRuntimeVersion, StableDeviceIdentity = value.StableDeviceIdentity,
            ReportedModel = value.ReportedModel, FirmwareVersion = value.FirmwareVersion,
            NativePixelFormatDescription = value.NativePixelFormatDescription,
            NormalizationDetails = value.NormalizationDetails, NormalizationAllocated = value.NormalizationAllocated,
            NormalizationTransformed = value.NormalizationTransformed,
            DeviceTimestamp = DeviceTimestampDto.From(value.DeviceTimestamp), FrameCounter = value.FrameCounter,
            Milestones = MilestonesDto.From(value.Milestones), PoolCopy = PoolCopyDto.From(value.PoolCopyEvidence)
        };

        internal FrameProvenance ToValue()
        {
            if (!Guid.TryParseExact(CorrelationValue, "D", out var correlation) || correlation == Guid.Empty ||
                Milestones is null)
                throw Invalid("CalibrationFrameProvenancePayloadInvalid");
            var value = new FrameProvenance(new ExecutionCorrelationId((ExecutionKind)CorrelationKind, correlation),
                ProviderId ?? string.Empty, ProviderVersion ?? string.Empty, AdapterId ?? string.Empty,
                AdapterVersion ?? string.Empty, SdkId ?? string.Empty, SdkVersion ?? string.Empty,
                NativeRuntimeVersion, StableDeviceIdentity ?? string.Empty, ReportedModel, FirmwareVersion,
                NativePixelFormatDescription ?? string.Empty, NormalizationDetails ?? string.Empty,
                NormalizationAllocated, NormalizationTransformed, DeviceTimestamp?.ToValue(), FrameCounter,
                Milestones.ToValue());
            if (PoolCopy is not null)
            {
                if (value.PoolCopyEvidence is not null)
                    throw Invalid("CalibrationFrameProvenancePayloadInvalid");
                value = value.WithPoolCopyEvidence(PoolCopy.SourceStrideBytes, PoolCopy.DestinationStrideBytes);
                if (value.PoolCopyEvidence!.InputNormalizationTransformed != PoolCopy.InputNormalizationTransformed)
                    throw Invalid("CalibrationFrameProvenancePayloadInvalid");
            }
            return value;
        }
    }

    private sealed class DeviceTimestampDto
    {
        public long Value { get; set; }
        public long? TickFrequency { get; set; }
        public string? Unit { get; set; }
        public string? ClockDomain { get; set; }
        public long? CounterRollover { get; set; }
        public int Synchronization { get; set; }
        internal static DeviceTimestampDto? From(DeviceTimestamp? value) => value is null ? null : new()
        { Value = value.Value, TickFrequency = value.TickFrequency, Unit = value.Unit, ClockDomain = value.ClockDomain,
            CounterRollover = value.CounterRollover, Synchronization = (int)value.Synchronization };
        internal DeviceTimestamp ToValue() => new(Value, TickFrequency, Unit, ClockDomain ?? string.Empty,
            CounterRollover, (DeviceClockSynchronization)Synchronization);
    }

    private sealed class MilestonesDto
    {
        public long MonotonicFrequency { get; set; }
        public TimePointDto? TriggerAccepted { get; set; }
        public TimePointDto? AcquisitionStarted { get; set; }
        public TimePointDto? NativeFrameReceived { get; set; }
        public TimePointDto? NormalizedFrameReady { get; set; }
        internal static MilestonesDto From(FrameAcquisitionMilestones value) => new()
        { MonotonicFrequency = value.MonotonicFrequency, TriggerAccepted = TimePointDto.From(value.TriggerAccepted),
            AcquisitionStarted = TimePointDto.From(value.AcquisitionStarted), NativeFrameReceived = TimePointDto.From(value.NativeFrameReceived),
            NormalizedFrameReady = TimePointDto.From(value.NormalizedFrameReady) };
        internal FrameAcquisitionMilestones ToValue() => new(MonotonicFrequency, TriggerAccepted?.ToValue(),
            AcquisitionStarted?.ToValue(), NativeFrameReceived?.ToValue(), NormalizedFrameReady?.ToValue());
    }

    private sealed class TimePointDto
    {
        public string? HostObservedAtUtc { get; set; }
        public long MonotonicTimestamp { get; set; }
        internal static TimePointDto? From(FrameTimePoint? value) => value is null ? null : new()
        { HostObservedAtUtc = value.HostObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), MonotonicTimestamp = value.MonotonicTimestamp };
        internal FrameTimePoint ToValue() => DateTimeOffset.TryParseExact(HostObservedAtUtc, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var utc) && utc.Offset == TimeSpan.Zero ? new FrameTimePoint(utc, MonotonicTimestamp) : throw Invalid("CalibrationFrameTimePayloadInvalid");
    }

    private sealed class PoolCopyDto
    {
        public int SourceStrideBytes { get; set; }
        public int DestinationStrideBytes { get; set; }
        public bool InputNormalizationTransformed { get; set; }
        internal static PoolCopyDto? From(PoolCopyEvidence? value) => value is null ? null : new()
        { SourceStrideBytes = value.SourceStrideBytes, DestinationStrideBytes = value.DestinationStrideBytes,
            InputNormalizationTransformed = value.InputNormalizationTransformed };
    }

    private sealed class ObservationDto
    {
        public string? ObservationId { get; set; }
        public FrameDto? Frame { get; set; }
        public ProcedureDto? Procedure { get; set; }
        public string? InputHash { get; set; }
        public ExtractionDto? Result { get; set; }

        internal static ObservationDto? From(CalibrationObservationEvidence? value) => value is null ? null : new()
        { ObservationId = value.ObservationId.ToString("D"), Frame = FrameDto.From(value.Frame),
            Procedure = ProcedureDto.From(value.Procedure), InputHash = value.InputHash, Result = ExtractionDto.From(value.Result) };

        internal CalibrationObservationEvidence ToValue()
        {
            if (!Guid.TryParseExact(ObservationId, "D", out var id) || id == Guid.Empty || Frame is null ||
                Procedure is null || Result is null)
                throw Invalid("CalibrationObservationPayloadInvalid");
            return new CalibrationObservationEvidence(id, Frame.ToValue(), Procedure.ToValue(),
                InputHash ?? string.Empty, Result.ToValue());
        }
    }

    private sealed class ExtractionDto
    {
        public List<FeatureDto>? Features { get; set; }
        public List<DiagnosticDto>? Diagnostics { get; set; }
        internal static ExtractionDto From(CalibrationExtractionResult value) => new()
        { Features = value.Features.Select(FeatureDto.From).ToList(), Diagnostics = value.Diagnostics.Select(DiagnosticDto.From).ToList() };
        internal CalibrationExtractionResult ToValue() => new(Features?.Select(value => value.ToValue()),
            Diagnostics?.Select(value => value.ToValue()));
    }

    private sealed class ObservationV2Dto
    {
        public string? ObservationId { get; set; }
        public FrameDto? Frame { get; set; }
        public ProcedureDto? Procedure { get; set; }
        public string? InputHash { get; set; }
        public ExtractionV2Dto? Result { get; set; }

        internal static ObservationV2Dto? From(CalibrationObservationEvidence? value) =>
            value is null ? null : new()
            {
                ObservationId = value.ObservationId.ToString("D"), Frame = FrameDto.From(value.Frame),
                Procedure = ProcedureDto.From(value.Procedure), InputHash = value.InputHash,
                Result = ExtractionV2Dto.From(value.Result)
            };

        internal CalibrationObservationEvidence ToValue()
        {
            if (!Guid.TryParseExact(ObservationId, "D", out var id) || id == Guid.Empty ||
                Frame is null || Procedure is null || Result is null)
                throw Invalid("CalibrationObservationPayloadInvalid");
            return new CalibrationObservationEvidence(id, Frame.ToValue(), Procedure.ToValue(),
                InputHash ?? string.Empty, Result.ToValue());
        }
    }

    private sealed class ExtractionV2Dto
    {
        public List<FeatureDto>? Features { get; set; }
        public List<DiagnosticDto>? Diagnostics { get; set; }
        public ExtractionReceiptDto? Receipt { get; set; }

        internal static ExtractionV2Dto From(CalibrationExtractionResult value) => new()
        {
            Features = value.Features.Select(FeatureDto.From).ToList(),
            Diagnostics = value.Diagnostics.Select(DiagnosticDto.From).ToList(),
            Receipt = ExtractionReceiptDto.From(value.Receipt)
        };

        internal CalibrationExtractionResult ToValue() =>
            new(Features?.Select(value => value.ToValue()),
                Diagnostics?.Select(value => value.ToValue()), Receipt?.ToValue());
    }

    private sealed class ExtractionReceiptDto
    {
        public ContractDto? Format { get; set; }
        public string? CanonicalBytes { get; set; }

        internal static ExtractionReceiptDto? From(CalibrationExtractionReceipt? value) =>
            value is null ? null : new()
            {
                Format = ContractDto.From(value.Format),
                CanonicalBytes = Convert.ToBase64String(value.GetBytes())
            };

        internal CalibrationExtractionReceipt ToValue() =>
            new(Format?.ToValue() ?? throw Invalid("CalibrationExtractionReceiptFormatMissing"),
                DecodeBytes(CanonicalBytes, "CalibrationExtractionReceiptInvalid",
                    CalibrationExtractionReceipt.MaximumBytes));
    }

    private sealed class FeatureDto
    {
        public string? StableFeatureId { get; set; }
        public double PixelX { get; set; }
        public double PixelY { get; set; }
        internal static FeatureDto From(CalibrationImageFeature value) => new()
        { StableFeatureId = value.StableFeatureId, PixelX = value.PixelX, PixelY = value.PixelY };
        internal CalibrationImageFeature ToValue() => new(StableFeatureId ?? string.Empty, PixelX, PixelY);
    }

    private sealed class DiagnosticDto
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        internal static DiagnosticDto From(CalibrationProcedureDiagnostic value) => new()
        { Key = value.Key, Value = value.Value };
        internal CalibrationProcedureDiagnostic ToValue() => new(Key ?? string.Empty, Value);
    }

    private sealed class ExclusionDto
    {
        public string? FrameId { get; set; }
        public string? ActorPrincipalId { get; set; }
        public string? InteractiveSessionId { get; set; }
        public string? Reason { get; set; }
        public string? RecordedAtUtc { get; set; }
        internal static ExclusionDto? From(CalibrationEvidenceExclusion? value) => value is null ? null : new()
        { FrameId = value.FrameId.ToString("D"), ActorPrincipalId = value.ActorPrincipalId.ToString("D"),
            InteractiveSessionId = value.InteractiveSessionId.ToString("D"), Reason = value.Reason,
            RecordedAtUtc = value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
        internal CalibrationEvidenceExclusion ToValue()
        {
            if (!Guid.TryParseExact(FrameId, "D", out var frame) || frame == Guid.Empty ||
                !Guid.TryParseExact(ActorPrincipalId, "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(InteractiveSessionId, "D", out var interactive) || interactive == Guid.Empty ||
                !DateTimeOffset.TryParseExact(RecordedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var recorded) || recorded.Offset != TimeSpan.Zero)
                throw Invalid("CalibrationExclusionPayloadInvalid");
            return new CalibrationEvidenceExclusion(frame, actor, interactive, Reason ?? string.Empty, recorded);
        }
    }

    private sealed class CandidateDto
    {
        public string? CandidateId { get; set; }
        public string? SessionId { get; set; }
        public string? SessionHeaderHash { get; set; }
        public string? SelectionHash { get; set; }
        public ComputationDto? Result { get; set; }
        public string? ComputedAtUtc { get; set; }
        internal static CandidateDto? From(CalibrationCandidateEvidence? value) => value is null ? null : new()
        { CandidateId = value.CandidateId.ToString("D"), SessionId = value.SessionId.ToString("D"),
            SessionHeaderHash = value.SessionHeaderHash, SelectionHash = value.SelectionHash,
            Result = ComputationDto.From(value.Result), ComputedAtUtc = value.ComputedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
        internal CalibrationCandidateEvidence ToValue()
        {
            if (!Guid.TryParseExact(CandidateId, "D", out var candidate) || candidate == Guid.Empty ||
                !Guid.TryParseExact(SessionId, "D", out var session) || session == Guid.Empty || Result is null ||
                !DateTimeOffset.TryParseExact(ComputedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var computed) || computed.Offset != TimeSpan.Zero)
                throw Invalid("CalibrationCandidatePayloadInvalid");
            return new CalibrationCandidateEvidence(candidate, session, SessionHeaderHash ?? string.Empty,
                SelectionHash ?? string.Empty, Result.ToValue(), computed);
        }
    }

    private sealed class ComputationDto
    {
        public CoefficientsDto? Coefficients { get; set; }
        public List<MetricDto>? QualityMetrics { get; set; }
        public List<DiagnosticDto>? Diagnostics { get; set; }
        internal static ComputationDto From(CalibrationProcedureComputationResult value) => new()
        { Coefficients = CoefficientsDto.From(value.Coefficients), QualityMetrics = value.QualityMetrics.Select(MetricDto.From).ToList(),
            Diagnostics = value.Diagnostics.Select(DiagnosticDto.From).ToList() };
        internal CalibrationProcedureComputationResult ToValue() => new(Coefficients?.ToValue() ?? throw Invalid("CalibrationCoefficientPayloadMissing"),
            QualityMetrics?.Select(value => value.ToValue()), Diagnostics?.Select(value => value.ToValue()));
    }

    private sealed class CandidateV2Dto
    {
        public string? CandidateId { get; set; }
        public string? SessionId { get; set; }
        public string? SessionHeaderHash { get; set; }
        public string? SelectionHash { get; set; }
        public ComputationV2Dto? Result { get; set; }
        public string? ComputedAtUtc { get; set; }

        internal static CandidateV2Dto? From(CalibrationCandidateEvidence? value) =>
            value is null ? null : new()
            {
                CandidateId = value.CandidateId.ToString("D"), SessionId = value.SessionId.ToString("D"),
                SessionHeaderHash = value.SessionHeaderHash, SelectionHash = value.SelectionHash,
                Result = ComputationV2Dto.From(value.Result),
                ComputedAtUtc = value.ComputedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            };

        internal CalibrationCandidateEvidence ToValue()
        {
            if (!Guid.TryParseExact(CandidateId, "D", out var candidate) || candidate == Guid.Empty ||
                !Guid.TryParseExact(SessionId, "D", out var session) || session == Guid.Empty ||
                Result is null ||
                !DateTimeOffset.TryParseExact(ComputedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var computed) || computed.Offset != TimeSpan.Zero)
                throw Invalid("CalibrationCandidatePayloadInvalid");
            return new CalibrationCandidateEvidence(candidate, session, SessionHeaderHash ?? string.Empty,
                SelectionHash ?? string.Empty, Result!.ToValue(), computed);
        }
    }

    private sealed class ComputationV2Dto
    {
        public CoefficientsDto? Coefficients { get; set; }
        public List<MetricDto>? QualityMetrics { get; set; }
        public List<DiagnosticDto>? Diagnostics { get; set; }
        public ComputationEvidenceDto? Evidence { get; set; }

        internal static ComputationV2Dto From(CalibrationProcedureComputationResult value) => new()
        {
            Coefficients = CoefficientsDto.From(value.Coefficients),
            QualityMetrics = value.QualityMetrics.Select(MetricDto.From).ToList(),
            Diagnostics = value.Diagnostics.Select(DiagnosticDto.From).ToList(),
            Evidence = ComputationEvidenceDto.From(value.Evidence)
        };

        internal CalibrationProcedureComputationResult ToValue() =>
            new(Coefficients?.ToValue() ?? throw Invalid("CalibrationCoefficientPayloadMissing"),
                QualityMetrics?.Select(value => value.ToValue()),
                Diagnostics?.Select(value => value.ToValue()), Evidence?.ToValue());
    }

    private sealed class ComputationEvidenceDto
    {
        public ContractDto? Format { get; set; }
        public string? CanonicalBytes { get; set; }

        internal static ComputationEvidenceDto? From(CalibrationComputationEvidencePayload? value) =>
            value is null ? null : new()
            {
                Format = ContractDto.From(value.Format),
                CanonicalBytes = Convert.ToBase64String(value.GetBytes())
            };

        internal CalibrationComputationEvidencePayload ToValue() =>
            new(Format?.ToValue() ?? throw Invalid("CalibrationComputationEvidenceFormatMissing"),
                DecodeBytes(CanonicalBytes, "CalibrationComputationEvidencePayloadInvalid"));
    }

    private sealed class CoefficientsDto
    {
        public ContractDto? Format { get; set; }
        public string? CanonicalBytes { get; set; }
        internal static CoefficientsDto From(CalibrationCoefficientPayload value) => new()
        { Format = ContractDto.From(value.Format), CanonicalBytes = Convert.ToBase64String(value.GetBytes()) };
        internal CalibrationCoefficientPayload ToValue() => new(Format?.ToValue() ?? throw Invalid("CalibrationCoefficientFormatMissing"),
            DecodeBytes(CanonicalBytes, "CalibrationCoefficientPayloadInvalid"));
    }

    private sealed class MetricDto
    {
        public string? Key { get; set; }
        public double Value { get; set; }
        public string? Unit { get; set; }
        internal static MetricDto From(CalibrationQualityMetric value) => new()
        { Key = value.Key, Value = value.Value, Unit = value.Unit };
        internal CalibrationQualityMetric ToValue() => new(Key ?? string.Empty, Value, Unit);
    }

    private static Guid? ParseNullableGuid(string? value, string reason)
    {
        if (value is null) return null;
        return Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed : throw Invalid(reason);
    }

    private static byte[] DecodeBytes(string? value, string reason,
        int maximumBytes = CalibrationProcedureInputPayload.MaximumBytes)
    {
        try
        {
            var bytes = Convert.FromBase64String(value ?? string.Empty);
            if (bytes.Length < 1 || bytes.Length > maximumBytes)
                throw Invalid(reason);
            return bytes;
        }
        catch (FormatException) { throw Invalid(reason); }
    }

    private static InvalidOperationException Invalid(string reason) => new(reason);
}

}

namespace SharpInspect.Runtime.Storage
{

using SharpInspect.Runtime.Calibration;

internal sealed partial class SqliteCommandStore
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal ValueTask<StoreWriteResult> AppendCalibrationEventAsync(CalibrationSessionEvent value,
        CommandAuditFact? terminal, CancellationToken cancellationToken = default,
        StoreDeadline? commandDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!CalibrationSessionsEnabled || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult(new StoreWriteResult(false, "CalibrationSessionStoreUnavailable"));
        var deadline = commandDeadline ?? new StoreDeadline(CommitTimeout);
        return EnqueueCalibrationEventAsync(new CalibrationEventWork(value, terminal), deadline,
            cancellationToken);
    }

    private async ValueTask<StoreWriteResult> EnqueueCalibrationEventAsync(CalibrationEventWork work,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
            CalibrationEvent: work), deadline, cancellationToken,
            "CalibrationSessionStoreUnavailable", "CalibrationSessionCommitDeadlineExceeded")
            .ConfigureAwait(false);
        return result;
    }

    internal ValueTask<CalibrationSessionQueryResult> ReadCalibrationSessionAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("CalibrationSessionIdRequired", nameof(sessionId));
        return new ValueTask<CalibrationSessionQueryResult>(ReadCalibrationSessionCoreAsync(sessionId,
            cancellationToken));
    }

    internal ValueTask<IReadOnlyList<CalibrationSessionEvidence>> ReadOpenCalibrationSessionsAsync(
        CancellationToken cancellationToken = default) =>
        new(ReadOpenCalibrationSessionsCoreAsync(cancellationToken));

    /// <summary>
    /// Returns accepted calibration actions which have no durable terminal fact yet.
    /// Restart recovery uses these facts to close the exact command correlation after
    /// restoration; it never synthesizes a new operation identity.
    /// </summary>
    internal ValueTask<IReadOnlyList<CommandAuditFact>> ReadPendingCalibrationActionsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("CalibrationSessionIdRequired", nameof(sessionId));
        return new(ReadPendingCalibrationActionsCoreAsync(sessionId, cancellationToken));
    }

    private async Task<IReadOnlyList<CommandAuditFact>> ReadPendingCalibrationActionsCoreAsync(
        Guid sessionId, CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !CalibrationSessionsEnabled || _databasePath is null ||
            _policy is null || _signingKey is null)
            return Array.Empty<CommandAuditFact>();
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath, true);
            SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            try
            {
                VerifyCalibrationReadSnapshot(database, deadline);
                var header = ReadCalibrationSessionRows(database, sessionId, deadline).SingleOrDefault();
                if (header is null)
                {
                    SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                    return (IReadOnlyList<CommandAuditFact>)Array.Empty<CommandAuditFact>();
                }
                var facts = ReadPendingCalibrationFactRows(database, header.Header, deadline);
                var result = facts.GroupBy(item => (item.CorrelationId, item.CommandKind))
                    .Select(group => group.First())
                    .Select(item => new CommandAuditFact(item.EventId, item.AttemptId, item.CorrelationId,
                        item.RuntimeEpoch, item.OccurredAtUtc, item.CommandKind, item.Source,
                        item.ClaimedPrincipalId, item.ClaimedSessionId, item.ClaimedStepUpGrantId,
                        item.Phase, item.Disposition, item.ReasonCode, item.AuthenticatedHumanPrincipalId))
                    .ToArray();
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                return (IReadOnlyList<CommandAuditFact>)result;
            }
            catch
            {
                try { SqliteNative.Execute(database, "ROLLBACK;", deadline); } catch { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static List<PendingCalibrationFactRow> ReadPendingCalibrationFactRows(
        sqlite3 database, CalibrationSessionHeader header, StoreDeadline deadline)
    {
        var facts = AuditChainDatabase.Read(database, @"
            SELECT f.Position,f.EventId,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,
                f.OccurredAtUtc,f.CommandKind,f.Source,f.ClaimedPrincipalId,
                f.ClaimedSessionId,f.ClaimedStepUpGrantId,f.Phase,f.Disposition,
                f.ReasonCode,f.AuthenticatedHumanPrincipalId
            FROM command_facts f
            WHERE f.Phase=? AND f.Disposition=? AND f.CommandKind IN (?,?,?,?)
              AND NOT EXISTS (SELECT 1 FROM command_facts terminal
                  WHERE terminal.CorrelationId=f.CorrelationId
                    AND terminal.CommandKind=f.CommandKind
                    AND terminal.Phase IN (?,?))
            ORDER BY f.Position;", deadline, statement => new PendingCalibrationFactRow(
                SqliteNative.ColumnInt64(statement, 0),
                ParseGuid(SqliteNative.ColumnText(statement, 1)),
                ParseGuid(SqliteNative.ColumnText(statement, 2)),
                ParseGuid(SqliteNative.ColumnText(statement, 3)),
                ParseGuid(SqliteNative.ColumnText(statement, 4)),
                ParseTime(SqliteNative.ColumnText(statement, 5)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 6),
                ParseNullableEnum<CommandSource>(SqliteNative.ColumnText(statement, 7)),
                SqliteNative.ColumnText(statement, 8),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 9)),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 10)),
                (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 11),
                ParseNullableEnum<CommandDisposition>(SqliteNative.ColumnText(statement, 12)),
                SqliteNative.ColumnText(statement, 13) ?? string.Empty,
                SqliteNative.ColumnText(statement, 14)),
            ((int)CommandAuditPhase.Outcome).ToString(CultureInfo.InvariantCulture),
            ((int)CommandDisposition.Accepted).ToString(CultureInfo.InvariantCulture),
            ((int)AuditedCommandKind.CaptureCalibrationFrame).ToString(CultureInfo.InvariantCulture),
            ((int)AuditedCommandKind.ExcludeCalibrationFrame).ToString(CultureInfo.InvariantCulture),
            ((int)AuditedCommandKind.ComputeCalibrationCandidate).ToString(CultureInfo.InvariantCulture),
            ((int)AuditedCommandKind.ExitCalibrationSession).ToString(CultureInfo.InvariantCulture),
            ((int)CommandAuditPhase.Completed).ToString(CultureInfo.InvariantCulture),
            ((int)CommandAuditPhase.Failed).ToString(CultureInfo.InvariantCulture));
        if (facts.Count == 0) return facts;

        var identity = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY IdentityPosition;", deadline,
            statement => (Ordinal: SqliteNative.ColumnInt64(statement, 0),
                Payload: DecodeStoredPayload(SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                    "CalibrationAuthorizationAuditPayloadInvalid")));
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ?? string.Empty;
        return facts.Where(fact => identity.Any(item =>
            MatchesCalibrationActionAuthorization(item.Payload, item.Ordinal, stationId, header, fact))).ToList();
    }

    private static bool MatchesCalibrationActionAuthorization(byte[] payload, long ordinal,
        string stationId, CalibrationSessionHeader header, PendingCalibrationFactRow fact)
    {
        string?[] fields;
        try
        {
            IdentityAuditEvent.VerifyPayload(payload, ordinal, stationId,
                CalibrationSessionStoreOptions.SchemaVersion);
            fields = DecodeIdentityFields(payload);
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
        if (fields.Length != 49 || fields[2] != IdentityEventKind.CalibrationSessionActionAuthorized.ToString() ||
            fields[31] != fact.CorrelationId.ToString("D") || fields[42] != header.SessionId.ToString("D"))
            return false;
        var actor = header.ActorPrincipalId.ToString("D");
        if (fact.RuntimeEpoch != header.RuntimeEpoch || fact.Source != header.Command.Invocation.Source ||
            fact.AuthenticatedHumanPrincipalId != actor || fact.ClaimedPrincipalId != actor ||
            fact.ClaimedSessionId != header.InteractiveSessionId || fact.ClaimedStepUpGrantId is not null ||
            fields[5] != actor || fields[30] != actor ||
            fields[25] != header.InteractiveSessionId.ToString("D") ||
            fields[35] != header.AuthorizationRevision.ToString(CultureInfo.InvariantCulture) ||
            fields[32] is not null || fields[33] != Permission.RunCalibration.ToString() ||
            fields[38] != fact.CorrelationId.ToString("D") || fields[39] != fact.CommandKind.ToString())
            throw new InvalidOperationException("CalibrationActionAuthorizationMismatch");
        return true;
    }

    private static string?[] DecodeIdentityFields(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (ReadBigEndianInt32(reader) != AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var marker = reader.ReadByte();
        if (marker != 1) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var labelLength = ReadBigEndianInt32(reader);
        if (labelLength is < 0 or > 1024) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var labelBytes = reader.ReadBytes(labelLength);
        if (labelBytes.Length != labelLength)
            throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var label = new UTF8Encoding(false, true).GetString(labelBytes);
        if (label != "IdentityEvent") throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var count = ReadBigEndianInt32(reader);
        if (count is not (46 or 49)) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        var fields = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var valueMarker = reader.ReadByte();
            if (valueMarker == 0) continue;
            if (valueMarker != 1) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
            var length = ReadBigEndianInt32(reader);
            if (length is < 0 or > 1024) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
            fields[i] = new UTF8Encoding(false, true).GetString(bytes);
        }
        if (stream.Position != stream.Length) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        return fields;
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new InvalidOperationException("CalibrationAuthorizationAuditPayloadInvalid");
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private async Task<CalibrationSessionQueryResult> ReadCalibrationSessionCoreAsync(Guid sessionId,
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !CalibrationSessionsEnabled || _databasePath is null ||
            _policy is null || _signingKey is null)
            return new(false, initialized.ReasonCode);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = SqliteNative.Open(_databasePath, true);
                SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
                var database = connection.Handle!;
                var deadline = new StoreDeadline(_options.QueryTimeout);
                SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
                try
                {
                    VerifyCalibrationReadSnapshot(database, deadline);
                    var row = ReadCalibrationSessionRows(database, sessionId, deadline).SingleOrDefault();
                    if (row is null)
                    {
                        SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                        return new CalibrationSessionQueryResult(false, "CalibrationSessionNotFound");
                    }
                    var evidence = BuildEvidence(database, row, deadline);
                    SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                    return new CalibrationSessionQueryResult(true, "CalibrationSessionAvailable", evidence);
                }
                catch
                {
                    try { SqliteNative.Execute(database, "ROLLBACK;", deadline); } catch { }
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CalibrationSessionQueryResult(false, SafeCalibrationReason(ex));
        }
    }

    private async Task<IReadOnlyList<CalibrationSessionEvidence>> ReadOpenCalibrationSessionsCoreAsync(
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !CalibrationSessionsEnabled || _databasePath is null ||
            _policy is null || _signingKey is null)
            return Array.Empty<CalibrationSessionEvidence>();
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath, true);
            SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            try
            {
                VerifyCalibrationReadSnapshot(database, deadline);
                var result = new List<CalibrationSessionEvidence>();
                foreach (var row in ReadCalibrationSessionRows(database, null, deadline))
                {
                    var events = ReadCalibrationEventRows(database, row.Header.SessionId, deadline);
                    if (events.Count == 0) continue;
                    var pendingActions = ReadPendingCalibrationFactRows(database, row.Header, deadline);
                    if (events[^1].Value.Outcome != CalibrationSessionOutcome.Pending && pendingActions.Count == 0)
                        continue;
                    result.Add(BuildEvidence(database, row, deadline));
                }
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                return (IReadOnlyList<CalibrationSessionEvidence>)result;
            }
            catch
            {
                try { SqliteNative.Execute(database, "ROLLBACK;", deadline); } catch { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void VerifyCalibrationReadSnapshot(sqlite3 database, StoreDeadline deadline)
    {
        var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
            _signingKey.PublicKeyBase64, new AuditVerificationRequest(0,
                _policy!.MaximumVerificationEntries), startup: true, deadline,
            validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
            recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
            cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
            imagingSetupOptions: _options.ImagingSetup,
            calibrationSessionOptions: _options.CalibrationSessions,
            governanceOptions: _options.CalibrationGovernance,
            releaseOptions: _options.RecipeReleases,
            contractOptions: _options.PlcResultContracts,
            activationOptions: _options.RecipeActivations,
            previewOptions: _options.PreviewSessions, importOptions: _options.CalibrationImports, manualOptions: _options.ManualInspections);
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        if (_options.RecipeDrafts is not null)
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline, _options.RecipeDrafts);
        if (_options.CameraSetup is not null)
            AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline, _options.CameraSetup);
        if (_options.CameraRecovery is not null)
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline, _options.CameraRecovery);
        if (_options.CameraNetwork is not null)
            AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline, _options.CameraNetwork);
        if (_options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline, _options.ImagingSetup);
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
        if (_options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
        if (_options.RecipeActivations is not null)
            AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                _options.CalibrationGovernance);
        if (_options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                _options.PreviewSessions);
            if (_options.CalibrationImports is not null)
                AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline, _options.CalibrationImports);
                if (_options.ManualInspections is not null)
                    AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline, _options.ManualInspections);
        ValidateCalibrationSessionHistory(database, _options.CalibrationSessions!, deadline, _policy!.StationId);
    }

    private StoreWriteResult AppendCalibrationEventCore(sqlite3 database, CalibrationEventWork work,
        StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified)
            return new(false, Integrity?.ReasonCode ?? "CalibrationAuditUnavailable",
                RetryAfterIntegrityRecheck: Integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");
        var committed = false;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            VerifyCalibrationReadSnapshot(database, deadline);
            var value = work.Value;
            var sessionRow = ReadCalibrationSessionRows(database, value.SessionId, deadline).SingleOrDefault();
            if (sessionRow is null) return new(false, "CalibrationSessionNotFound");
            var existing = ReadCalibrationEventRows(database, value.SessionId, deadline)
                .SingleOrDefault(item => item.Value.EventId == value.EventId);
            var payload = CalibrationSessionStorageCodec.EncodeEvent(value);
            var options = _options.CalibrationSessions!;
            if (payload.Length > options.MaximumEventPayloadBytes)
                return new(false, "CalibrationEventPayloadCapacityExceeded");
            if (existing is not null)
            {
                var existingPayload = Convert.FromBase64String(existing.Payload);
                if (existing.OperationId == value.OperationId.ToString("D") && existing.Value.Phase == value.Phase &&
                    existing.Value.Outcome == value.Outcome && existing.Value.ReasonCode == value.ReasonCode &&
                    existing.Value.OccurredAtUtc == value.OccurredAtUtc && existingPayload.SequenceEqual(payload))
                {
                    SqliteNative.Execute(database, "ROLLBACK;", deadline);
                    committed = true;
                    return new(true, "CalibrationEventAlreadyPersisted");
                }
                return new(false, "CalibrationEventConflict");
            }

            if (value.IsAdmission) return new(false, "CalibrationAdmissionRequiresIdentityTransaction");
            var eventRows = ReadCalibrationEventRows(database, value.SessionId, deadline);
            if (eventRows.Count == 0 || eventRows[^1].Value.Outcome != CalibrationSessionOutcome.Pending)
                return new(false, "CalibrationSessionClosed");
            var priorTemporaryConfiguration = eventRows
                .Select(item => item.Value.TemporaryConfiguration)
                .SingleOrDefault(item => item is not null);
            ValidateCalibrationEventSemantics(value, eventRows[^1].Value, sessionRow.Header,
                priorTemporaryConfiguration);
            var sequence = eventRows.Count == 0 ? 1 : checked(eventRows[^1].Sequence + 1);
            if (sequence > options.MaximumEvents)
                return new(false, "CalibrationEventCapacityExceeded");
            var previousHash = eventRows.Count == 0 ? null : eventRows[^1].ContentHash;
            var frames = eventRows.Where(item => item.Value.Frame is not null)
                .Select(item => item.Value.Frame!).ToList();
            var observations = eventRows.Where(item => item.Value.Observation is not null)
                .Select(item => item.Value.Observation!).ToList();
            var exclusions = eventRows.Where(item => item.Value.Exclusion is not null)
                .Select(item => item.Value.Exclusion!).ToList();
            if (value.Frame is { } frame) frames.Add(frame);
            if (value.Observation is { } observation) observations.Add(observation);
            if (value.Exclusion is { } exclusion) exclusions.Add(exclusion);
            if (frames.Count > options.MaximumFramesPerSession)
                return new(false, "CalibrationFrameCapacityExceeded");
            if (frames.Any(item => item.ByteLength > options.MaximumFrameBytes) ||
                frames.Aggregate(0L, (total, item) => checked(total + item.ByteLength)) > options.MaximumTotalFrameBytes)
                return new(false, "CalibrationFrameTotalCapacityExceeded");
            var selection = CalibrationEvidenceSelection.Evaluate(sessionRow.Header, frames, observations, exclusions);
            if (value.Candidate is { } candidate &&
                (candidate.SessionHeaderHash != sessionRow.Header.ContentHash ||
                 candidate.SelectionHash != selection.SelectionHash))
                return new(false, "CalibrationCandidateSelectionMismatch");
            var authorization = FindCalibrationAuthorization(database, sessionRow.Header, value,
                _policy!.StationId, deadline);
            if (authorization is null)
                return new(false, "CalibrationAuthorizationBindingMissing");
            var (auditSequence, auditHash) = authorization.Value;
            var eventContentHash = CalibrationSessionStorageCodec.EventContentHash(value, sequence,
                previousHash, payload, auditSequence, auditHash);
            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0)+1 FROM calibration_session_events;", deadline));
            var signedEventSequence = AuditChainDatabase.AppendCalibrationLedgerEntry(database, _policy!,
                _signingKey!, "CalibrationSessionEvent", position, payload, deadline);
            var signedEventHash = ReadCalibrationLedgerHash(database, signedEventSequence,
                "CalibrationSessionEvent", position, deadline);
            AuditChainDatabase.Execute(database, @"INSERT INTO calibration_session_events
                (Position,SessionId,Sequence,EventId,OperationId,Kind,PreviousHash,Payload,PayloadHash,
                 ContentHash,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(position), value.SessionId.ToString("D"), Number(sequence), value.EventId.ToString("D"),
                value.OperationId.ToString("D"), value.Kind, previousHash, Convert.ToBase64String(payload),
                CalibrationSessionStorageCodec.PayloadHash(payload), eventContentHash,
                Number(auditSequence), auditHash, Number(signedEventSequence), signedEventHash);
            if (value.Frame is not null)
                AppendFrameManifest(database, value.Frame, auditSequence, auditHash, deadline);
            if (work.Terminal is not null)
            {
                ValidateCalibrationTerminal(work.Terminal, sessionRow.Header, value);
                AppendIdentityCommandFacts(database, new[] { work.Terminal }, work.Terminal.CorrelationId, deadline,
                    allowTerminalContinuation: true);
            }
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            var tail = AuditChainDatabase.Tail(database, deadline).Sequence;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, tail);
            try
            {
                PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                    "AuditRecheckPending"));
                WakeIntegrityMonitor();
            }
            catch { }
            return new(true, "CalibrationEventPersisted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = SafeCalibrationReason(ex);
            if (!AuditChainDatabase.IsCapacityReason(reason) && reason.StartsWith("Calibration", StringComparison.Ordinal))
                SetIntegrityFault(reason, IsStructuralCalibrationReason(reason));
            return new(false, reason);
        }
        finally
        {
            if (!committed) Rollback(database);
        }
    }

    private void AppendFrameManifest(sqlite3 database, CalibrationFrameEvidence frame,
        long auditSequence, string auditHash, StoreDeadline deadline)
    {
        var payload = CalibrationSessionStorageCodec.EncodeFrameManifest(frame);
        var manifestPosition = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM calibration_frame_manifests;", deadline));
        var signedManifestSequence = AuditChainDatabase.AppendCalibrationLedgerEntry(database, _policy!,
            _signingKey!, "CalibrationFrameManifest", manifestPosition, payload, deadline);
        var signedManifestHash = ReadCalibrationLedgerHash(database, signedManifestSequence,
            "CalibrationFrameManifest", manifestPosition, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO calibration_frame_manifests
            (FrameId,SessionId,Position,SourceHash,RelativePath,ByteLength,PixelHash,Payload,PayloadHash,
             AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline, frame.FrameId.ToString("D"), frame.SessionId.ToString("D"),
            Number(manifestPosition), frame.SourceHash, frame.RelativePath,
            frame.ByteLength.ToString(CultureInfo.InvariantCulture), frame.PixelHash,
            Convert.ToBase64String(payload), CalibrationSessionStorageCodec.PayloadHash(payload),
            Number(auditSequence), auditHash, Number(signedManifestSequence), signedManifestHash);
    }

    private static void ValidateCalibrationTerminal(CommandAuditFact terminal,
        CalibrationSessionHeader header, CalibrationSessionEvent value)
    {
        var startTerminal = terminal.CommandKind == AuditedCommandKind.StartCalibrationSession;
        var contextMatches = startTerminal
            ? terminal.CorrelationId == header.Command.CorrelationId &&
              terminal.AttemptId == header.AdmissionAttemptId && terminal.RuntimeEpoch == header.RuntimeEpoch &&
              terminal.ClaimedSessionId == header.InteractiveSessionId &&
              terminal.ClaimedStepUpGrantId == header.Command.Invocation.StepUpGrantId &&
              terminal.AuthenticatedHumanPrincipalId == header.ActorPrincipalId.ToString("D")
            : terminal.CorrelationId == value.OperationId &&
              CalibrationAuthorizationKinds(header, value).Contains(terminal.CommandKind);
        if (terminal.EventId == Guid.Empty || terminal.AttemptId == Guid.Empty ||
            terminal.Phase is not (CommandAuditPhase.Completed or CommandAuditPhase.Failed) ||
            terminal.Disposition is not null || !contextMatches)
            throw new InvalidOperationException("CalibrationTerminalBindingMismatch");
    }

    private static (long Sequence, string Hash)? FindCalibrationAuthorization(sqlite3 database,
        CalibrationSessionHeader header, CalibrationSessionEvent value, string stationId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database,
            "SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;",
            deadline, statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1), Payload: SqliteNative.ColumnText(statement, 2)!,
                Hash: SqliteNative.ColumnText(statement, 3)!));
        var matches = new List<(long Sequence, string Hash)>();
        foreach (var row in rows)
        {
            byte[] payload;
            try { payload = DecodeStoredPayload(row.Payload, "CalibrationAuthorizationAuditPayloadInvalid"); }
            catch (InvalidOperationException) { continue; }
            if (MatchesCalibrationEventAuthorization(payload, row.Ordinal, stationId, header, value))
                matches.Add((row.Sequence, row.Hash));
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string CalibrationActionTarget(CalibrationSessionEvent value, AuditedCommandKind kind)
    {
        var commandName = kind switch
        {
            AuditedCommandKind.CaptureCalibrationFrame => nameof(CaptureCalibrationFrameCommand),
            AuditedCommandKind.ExcludeCalibrationFrame => nameof(ExcludeCalibrationFrameCommand),
            AuditedCommandKind.ComputeCalibrationCandidate => nameof(ComputeCalibrationCandidateCommand),
            AuditedCommandKind.ExitCalibrationSession => nameof(ExitCalibrationSessionCommand),
            _ => throw new InvalidOperationException("CalibrationCommandKindInvalid")
        };
        var frame = value.Exclusion?.FrameId.ToString("D");
        var reason = value.Exclusion?.Reason ?? (kind == AuditedCommandKind.ExitCalibrationSession ? value.ReasonCode : null);
        var cancel = kind == AuditedCommandKind.ExitCalibrationSession
            ? (value.Outcome == CalibrationSessionOutcome.Cancelled ? "True" : "False") : null;
        return AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-action-v1", commandName, value.SessionId.ToString("D"), frame, reason, cancel
        });
    }

    private void AppendCalibrationAdmission(sqlite3 database, CalibrationSessionHeader header,
        long identitySequence, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (identitySequence < 1) throw new InvalidOperationException("CalibrationAdmissionAuditMissing");
        var options = _options.CalibrationSessions ?? throw new InvalidOperationException("CalibrationConfigurationRequired");
        // Session ownership is checked under the same writer transaction as admission.
        // A read-side preflight cannot prevent two station instances from admitting at once.
        var pendingSessions = ReadCalibrationSessionRows(database, null, deadline)
            .Count(session => ReadCalibrationEventRows(database, session.Header.SessionId, deadline)
                .LastOrDefault()?.Value.Outcome == CalibrationSessionOutcome.Pending);
        if (pendingSessions != 0)
            throw new InvalidOperationException("CalibrationSessionPending");

        var cameraState = ReadCameraSetupState(database, header.Binding.LogicalRole, deadline);
        var camera = cameraState.Binding;
        AuditChainDatabase.Require(!cameraState.HasPending && camera is not null &&
            camera.LogicalRole == header.Binding.LogicalRole && camera.Revision == header.Binding.Revision &&
            camera.RevisionHash == header.Binding.RevisionHash && camera.Target.ContentHash == header.Binding.Target.ContentHash,
            "CalibrationAdmissionBindingMismatch");
        var imaging = ReadImagingSetupState(database, header.Command.Plan.Requirement.LogicalCameraRole,
            deadline).Current;
        var expectedImaging = header.Command.ExpectedImagingSetup;
        AuditChainDatabase.Require(imaging is not null && imaging.LogicalCameraRole == expectedImaging.LogicalCameraRole &&
            imaging.OperationId == expectedImaging.RevisionId && imaging.Revision == expectedImaging.Revision &&
            imaging.RevisionHash == expectedImaging.RevisionHash &&
            imaging.Binding.Revision == header.Binding.Revision &&
            imaging.Binding.RevisionHash == header.Binding.RevisionHash &&
            imaging.Binding.Target.ContentHash == header.Binding.Target.ContentHash,
            "CalibrationAdmissionImagingMismatch");

        var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM calibration_sessions;", deadline);
        if (count >= options.MaximumSessions) throw new InvalidOperationException("CalibrationSessionCapacityExceeded");
        if (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM calibration_sessions WHERE SessionId=?;", deadline,
                header.SessionId.ToString("D")) != 0)
            throw new InvalidOperationException("CalibrationSessionAdmissionConflict");
        var audit = ReadIdentityAudit(database, identitySequence, deadline);
        AuditChainDatabase.Require(audit is not null && IdentityAuditEvent.MatchesCalibrationAuthorization(
            audit.Value.Payload, audit.Value.Ordinal, ReadStationId(database, deadline), header,
            AuditedCommandKind.StartCalibrationSession, header.Command.CorrelationId, header.Command.AuthorizationTarget),
            "CalibrationAdmissionAuditBindingMismatch");
        var headerPayload = CalibrationSessionStorageCodec.EncodeHeader(header);
        var headerPosition = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM calibration_sessions;", deadline));
        var signedHeaderSequence = AuditChainDatabase.AppendCalibrationLedgerEntry(database, _policy!,
            _signingKey!, "CalibrationSessionHeader", headerPosition, headerPayload, deadline);
        var signedHeaderHash = ReadCalibrationLedgerHash(database, signedHeaderSequence,
            "CalibrationSessionHeader", headerPosition, deadline);
        var headerContentHash = CalibrationSessionStorageCodec.SessionContentHash(header, headerPosition,
            headerPayload, identitySequence, audit!.Value.Hash);
        var initial = new CalibrationSessionEvent(header.AdmissionAttemptId, header.SessionId,
            header.Command.CorrelationId, CalibrationSessionPhase.Admitted, CalibrationSessionOutcome.Pending,
            "CalibrationSessionAdmitted", header.AdmittedAtUtc);
        var eventPayload = CalibrationSessionStorageCodec.EncodeEvent(initial);
        var eventPosition = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM calibration_session_events;", deadline));
        var signedEventSequence = AuditChainDatabase.AppendCalibrationLedgerEntry(database, _policy!,
            _signingKey!, "CalibrationSessionEvent", eventPosition, eventPayload, deadline);
        var signedEventHash = ReadCalibrationLedgerHash(database, signedEventSequence,
            "CalibrationSessionEvent", eventPosition, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO calibration_sessions
            (SessionId,Position,AdmissionCorrelationId,AdmissionAttemptId,RuntimeEpoch,ActorPrincipalId,
             InteractiveSessionId,AuthorizationRevision,StepUpGrantId,LogicalRole,Payload,PayloadHash,
             ContentHash,AdmissionAuditSequence,AdmissionAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline, header.SessionId.ToString("D"), Number(headerPosition),
            header.Command.CorrelationId.ToString("D"), header.AdmissionAttemptId.ToString("D"), header.RuntimeEpoch.ToString("D"),
            header.ActorPrincipalId.ToString("D"), header.InteractiveSessionId.ToString("D"),
            header.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), header.Command.Invocation.StepUpGrantId!.Value.ToString("D"),
            header.Binding.LogicalRole, Convert.ToBase64String(headerPayload), CalibrationSessionStorageCodec.PayloadHash(headerPayload),
            headerContentHash, Number(identitySequence), audit.Value.Hash,
            Number(signedHeaderSequence), signedHeaderHash);
        var eventHash = CalibrationSessionStorageCodec.EventContentHash(initial, 1, null, eventPayload,
            identitySequence, audit.Value.Hash);
        AuditChainDatabase.Execute(database, @"INSERT INTO calibration_session_events
            (Position,SessionId,Sequence,EventId,OperationId,Kind,PreviousHash,Payload,PayloadHash,
             ContentHash,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline, Number(eventPosition), header.SessionId.ToString("D"), "1",
            initial.EventId.ToString("D"), initial.OperationId.ToString("D"), initial.Kind, null,
            Convert.ToBase64String(eventPayload), CalibrationSessionStorageCodec.PayloadHash(eventPayload), eventHash,
            Number(identitySequence), audit.Value.Hash, Number(signedEventSequence), signedEventHash);
    }

    private sealed record CalibrationSessionRawRow(long Position, string SessionId,
        string AdmissionCorrelationId, string AdmissionAttemptId, string RuntimeEpoch,
        string ActorPrincipalId, string InteractiveSessionId, long AuthorizationRevision,
        string StepUpGrantId, string LogicalRole, string Payload, string PayloadHash,
        string ContentHash, long AdmissionAuditSequence, string AdmissionAuditHash,
        long AuditSequence, string AuditHash);

    private sealed record CalibrationSessionRow(long Position, string SessionId,
        string AdmissionCorrelationId, string AdmissionAttemptId, string RuntimeEpoch,
        string ActorPrincipalId, string InteractiveSessionId, long AuthorizationRevision,
        string StepUpGrantId, string LogicalRole, string Payload, string PayloadHash,
        string ContentHash, long AdmissionAuditSequence, string AdmissionAuditHash,
        long AuditSequence, string AuditHash, CalibrationSessionHeader Header);

    private sealed record CalibrationSessionEventRawRow(long Position, string SessionId,
        long Sequence, string EventId, string OperationId, string Kind, string? PreviousHash,
        string Payload, string PayloadHash, string ContentHash,
        long AuthorizationAuditSequence, string AuthorizationAuditHash,
        long AuditSequence, string AuditHash);

    private sealed record CalibrationSessionEventRow(long Position, string SessionId,
        long Sequence, string EventId, string OperationId, string Kind, string? PreviousHash,
        string Payload, string PayloadHash, string ContentHash,
        long AuthorizationAuditSequence, string AuthorizationAuditHash,
        long AuditSequence, string AuditHash, CalibrationSessionEvent Value);

    private sealed record CalibrationFrameManifestRawRow(string FrameId, string SessionId,
        long Position, string SourceHash, string RelativePath, long ByteLength, string PixelHash,
        string Payload, string PayloadHash, long AuthorizationAuditSequence,
        string AuthorizationAuditHash, long AuditSequence, string AuditHash);

    private sealed record CalibrationFrameManifestRow(string FrameId, string SessionId,
        long Position, string SourceHash, string RelativePath, long ByteLength, string PixelHash,
        string Payload, string PayloadHash, long AuthorizationAuditSequence,
        string AuthorizationAuditHash, long AuditSequence, string AuditHash, CalibrationFrameEvidence Value);

    private readonly record struct IdentityAuditRow(long Sequence, long Ordinal, byte[] Payload, string Hash);

    private readonly record struct CalibrationLedgerAuditRow(long Sequence, string Kind,
        long? SessionPosition, long? EventPosition, long? ManifestPosition, string Payload, string Hash);

    private static List<CalibrationSessionRow> ReadCalibrationSessionRows(sqlite3 database,
        Guid? sessionId, StoreDeadline deadline)
    {
        var suffix = sessionId is null ? "ORDER BY Position" : "WHERE SessionId=? ORDER BY Position";
        var raw = AuditChainDatabase.Read(database, @"SELECT Position,SessionId,AdmissionCorrelationId,
            AdmissionAttemptId,RuntimeEpoch,ActorPrincipalId,InteractiveSessionId,AuthorizationRevision,
            StepUpGrantId,LogicalRole,Payload,PayloadHash,ContentHash,AdmissionAuditSequence,AdmissionAuditHash,
            AuditSequence,AuditHash
            FROM calibration_sessions " + suffix + ";", deadline,
            statement => new CalibrationSessionRawRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                SqliteNative.ColumnText(statement, 6) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 7),
                SqliteNative.ColumnText(statement, 8) ?? string.Empty,
                SqliteNative.ColumnText(statement, 9) ?? string.Empty,
                SqliteNative.ColumnText(statement, 10) ?? string.Empty,
                SqliteNative.ColumnText(statement, 11) ?? string.Empty,
                SqliteNative.ColumnText(statement, 12) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 13),
                SqliteNative.ColumnText(statement, 14) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 15),
                SqliteNative.ColumnText(statement, 16) ?? string.Empty),
            sessionId is { } id ? new string?[] { id.ToString("D") } : Array.Empty<string?>());
        return raw.Select(DecodeSessionRow).ToList();
    }

    private static CalibrationSessionRow DecodeSessionRow(CalibrationSessionRawRow row)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationSessionHeaderPayloadInvalid");
        var header = CalibrationSessionStorageCodec.DecodeHeader(payload);
        return new CalibrationSessionRow(row.Position, row.SessionId, row.AdmissionCorrelationId,
            row.AdmissionAttemptId, row.RuntimeEpoch, row.ActorPrincipalId, row.InteractiveSessionId,
            row.AuthorizationRevision, row.StepUpGrantId, row.LogicalRole, row.Payload, row.PayloadHash,
            row.ContentHash, row.AdmissionAuditSequence, row.AdmissionAuditHash,
            row.AuditSequence, row.AuditHash, header);
    }

    private static List<CalibrationSessionEventRow> ReadCalibrationEventRows(sqlite3 database,
        Guid? sessionId, StoreDeadline deadline)
    {
        var suffix = sessionId is null ? "ORDER BY Position" : "WHERE SessionId=? ORDER BY Sequence";
        var raw = AuditChainDatabase.Read(database, @"SELECT Position,SessionId,Sequence,EventId,
            OperationId,Kind,PreviousHash,Payload,PayloadHash,ContentHash,
            AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash
            FROM calibration_session_events " + suffix + ";",
            deadline, statement => new CalibrationSessionEventRawRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 2),
                SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                SqliteNative.ColumnText(statement, 6),
                SqliteNative.ColumnText(statement, 7) ?? string.Empty,
                SqliteNative.ColumnText(statement, 8) ?? string.Empty,
                SqliteNative.ColumnText(statement, 9) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 10),
                SqliteNative.ColumnText(statement, 11) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 12),
                SqliteNative.ColumnText(statement, 13) ?? string.Empty),
            sessionId is { } id ? new string?[] { id.ToString("D") } : Array.Empty<string?>());
        return raw.Select(DecodeEventRow).ToList();
    }

    private static CalibrationSessionEventRow DecodeEventRow(CalibrationSessionEventRawRow row)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationSessionEventPayloadInvalid");
        var value = CalibrationSessionStorageCodec.DecodeEvent(payload);
        return new CalibrationSessionEventRow(row.Position, row.SessionId, row.Sequence, row.EventId,
            row.OperationId, row.Kind, row.PreviousHash, row.Payload, row.PayloadHash, row.ContentHash,
            row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.AuditSequence, row.AuditHash, value);
    }

    private static List<CalibrationFrameManifestRow> ReadCalibrationFrameManifestRows(sqlite3 database,
        StoreDeadline deadline)
    {
        var raw = AuditChainDatabase.Read(database, @"SELECT FrameId,SessionId,Position,SourceHash,
            RelativePath,ByteLength,PixelHash,Payload,PayloadHash,AuthorizationAuditSequence,
            AuthorizationAuditHash,AuditSequence,AuditHash
            FROM calibration_frame_manifests ORDER BY Position;", deadline,
            statement => new CalibrationFrameManifestRawRow(SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 2),
                SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 5),
                SqliteNative.ColumnText(statement, 6) ?? string.Empty,
                SqliteNative.ColumnText(statement, 7) ?? string.Empty,
                SqliteNative.ColumnText(statement, 8) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 9),
                SqliteNative.ColumnText(statement, 10) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 11),
                SqliteNative.ColumnText(statement, 12) ?? string.Empty));
        return raw.Select(DecodeManifestRow).ToList();
    }

    private static CalibrationFrameManifestRow DecodeManifestRow(CalibrationFrameManifestRawRow row)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationFrameManifestPayloadInvalid");
        var value = CalibrationSessionStorageCodec.DecodeFrameManifest(payload);
        return new CalibrationFrameManifestRow(row.FrameId, row.SessionId, row.Position, row.SourceHash,
            row.RelativePath, row.ByteLength, row.PixelHash, row.Payload, row.PayloadHash,
            row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.AuditSequence, row.AuditHash, value);
    }

    private static byte[] DecodeStoredPayload(string encoded, string reason)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > CalibrationSessionStorageCodec.MaximumEncodedChars)
            throw new InvalidOperationException(reason);
        try
        {
            var payload = Convert.FromBase64String(encoded);
            if (payload.Length is < 1 or > CalibrationSessionStoreOptions.SqliteValueLimitBytes)
                throw new InvalidOperationException(reason);
            return payload;
        }
        catch (FormatException exception) { throw new InvalidOperationException(reason, exception); }
    }

    private static IdentityAuditRow? ReadIdentityAudit(sqlite3 database, long sequence,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' AND IdentityPosition IS NOT NULL;",
            deadline, statement => new IdentityAuditRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnInt64(statement, 1),
                DecodeStoredPayload(SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                    "CalibrationAuthorizationAuditPayloadInvalid"),
                SqliteNative.ColumnText(statement, 3) ?? string.Empty), Number(sequence));
        return rows.Count == 0 ? null : rows[0];
    }

    private static CalibrationLedgerAuditRow? ReadCalibrationLedgerAudit(sqlite3 database,
        long sequence, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,Kind,
            CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind IN (?,?,?);", deadline,
            statement => new CalibrationLedgerAuditRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParseNullableLong(SqliteNative.ColumnText(statement, 2)),
                ParseNullableLong(SqliteNative.ColumnText(statement, 3)),
                ParseNullableLong(SqliteNative.ColumnText(statement, 4)),
                SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                SqliteNative.ColumnText(statement, 6) ?? string.Empty), Number(sequence),
            "CalibrationSessionHeader", "CalibrationSessionEvent", "CalibrationFrameManifest");
        return rows.Count == 1 ? rows[0] : null;
    }

    private static string ReadCalibrationLedgerHash(sqlite3 database, long sequence,
        string kind, long position, StoreDeadline deadline)
    {
        var column = kind switch
        {
            "CalibrationSessionHeader" => "CalibrationSessionPosition",
            "CalibrationSessionEvent" => "CalibrationEventPosition",
            "CalibrationFrameManifest" => "CalibrationManifestPosition",
            _ => throw new InvalidOperationException("CalibrationAuditKindInvalid")
        };
        var hashes = AuditChainDatabase.Read(database,
            $"SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND {column}=?;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            Number(sequence), kind, Number(position));
        if (hashes.Count != 1 || hashes[0].Length != 64)
            throw new InvalidOperationException("CalibrationAuditEntryMissing");
        return hashes[0];
    }

    private string ReadStationId(sqlite3 database, StoreDeadline deadline) =>
        _options.LocalIdentity?.StationId ??
        AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ??
        throw new InvalidOperationException("CalibrationStationIdentityMissing");

    private static CalibrationSessionEvidence BuildEvidence(sqlite3 database, CalibrationSessionRow row,
        StoreDeadline deadline)
    {
        var events = ReadCalibrationEventRows(database, row.Header.SessionId, deadline);
        var frames = events.Where(item => item.Value.Frame is not null)
            .Select(item => item.Value.Frame!).ToArray();
        var observations = events.Where(item => item.Value.Observation is not null)
            .Select(item => item.Value.Observation!).ToArray();
        var exclusions = events.Where(item => item.Value.Exclusion is not null)
            .Select(item => item.Value.Exclusion!).ToArray();
        var candidates = events.Where(item => item.Value.Candidate is not null)
            .Select(item => item.Value.Candidate!).ToArray();
        var candidate = candidates.Length == 0 ? null : candidates[^1];
        var temporaryConfigurations = events.Where(item => item.Value.TemporaryConfiguration is not null)
            .Select(item => item.Value.TemporaryConfiguration!).ToArray();
        var temporaryConfiguration = temporaryConfigurations.Length == 0 ? null : temporaryConfigurations[^1];
        var latest = events[^1].Value;
        var selection = CalibrationEvidenceSelection.Evaluate(row.Header, frames, observations, exclusions);
        var state = new CalibrationSessionState(row.Header.SessionId, latest.Phase, latest.Outcome,
            frames.Length, observations.Length, exclusions.Length, candidate?.CandidateId,
            latest.ReasonCode, latest.Phase == CalibrationSessionPhase.Restored);
        return new CalibrationSessionEvidence(row.Header, state, frames, observations, exclusions,
            candidate, selection, temporaryConfiguration);
    }

    /// <summary>
    /// Replays every immutable calibration ledger before exposing a projection.  The
    /// session tables have their own immutable payload chain and a signed audit
    /// envelope; this check binds each row to both chains and to its identity event.
    /// </summary>
    private static void ValidateCalibrationSessionHistory(sqlite3 database,
        CalibrationSessionStoreOptions options, StoreDeadline deadline, string stationId)
    {
        options.Validate();
        RequireConfiguredCalibrationSessions(database, options, deadline);
        var sessions = ReadCalibrationSessionRows(database, null, deadline);
        AuditChainDatabase.Require(sessions.Count <= options.MaximumSessions,
            "CalibrationSessionCapacityExceeded");
        var events = ReadCalibrationEventRows(database, null, deadline);
        var manifests = ReadCalibrationFrameManifestRows(database, deadline);

        var sessionIds = new HashSet<Guid>();
        var admissionCorrelations = new HashSet<Guid>();
        var admissionAttempts = new HashSet<Guid>();
        var signedAuditSequences = new HashSet<long>();
        var expectedSessionPosition = 1L;
        foreach (var row in sessions)
        {
            AuditChainDatabase.Require(row.Position == expectedSessionPosition++, "CalibrationSessionPositionGap");
            ValidateCalibrationSessionRow(database, row, stationId, deadline);
            AuditChainDatabase.Require(signedAuditSequences.Add(row.AuditSequence),
                "CalibrationSignedAuditConflict");
            AuditChainDatabase.Require(sessionIds.Add(row.Header.SessionId) &&
                admissionCorrelations.Add(row.Header.Command.CorrelationId) &&
                admissionAttempts.Add(row.Header.AdmissionAttemptId),
                "CalibrationSessionAdmissionConflict");
        }

        var manifestsById = new Dictionary<Guid, CalibrationFrameManifestRow>();
        var expectedManifestPosition = 1L;
        foreach (var manifest in manifests)
        {
            AuditChainDatabase.Require(manifest.Position == expectedManifestPosition++, "CalibrationFramePositionGap");
            ValidateCalibrationManifestRow(manifest, database, deadline);
            AuditChainDatabase.Require(signedAuditSequences.Add(manifest.AuditSequence),
                "CalibrationSignedAuditConflict");
            var frameId = ParseGuidExact(manifest.FrameId, "CalibrationFrameManifestIdentityInvalid");
            AuditChainDatabase.Require(manifestsById.TryAdd(frameId, manifest),
                "CalibrationFrameManifestConflict");
        }

        var eventsBySession = events.GroupBy(item => item.Value.SessionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Sequence).ToArray());
        var expectedEventPosition = 1L;
        foreach (var session in sessions)
        {
            AuditChainDatabase.Require(eventsBySession.TryGetValue(session.Header.SessionId,
                out var sessionEvents) && sessionEvents!.Length > 0,
                "CalibrationSessionAdmissionMissing");
            var frames = new List<CalibrationFrameEvidence>();
            var observations = new List<CalibrationObservationEvidence>();
            var exclusions = new List<CalibrationEvidenceExclusion>();
            var candidates = new List<CalibrationCandidateEvidence>();
            var expectedSequence = 1L;
            string? previousHash = null;
            CalibrationSessionEvent? previousEvent = null;
            CalibrationTemporaryConfigurationEvidence? temporaryConfiguration = null;
            foreach (var item in sessionEvents!)
            {
                AuditChainDatabase.Require(item.Position == expectedEventPosition++,
                    "CalibrationEventPositionGap");
                AuditChainDatabase.Require(item.Sequence == expectedSequence++, "CalibrationEventSequenceGap");
                ValidateCalibrationEventRow(database, session.Header, item, previousHash, stationId, deadline);
                AuditChainDatabase.Require(signedAuditSequences.Add(item.AuditSequence),
                    "CalibrationSignedAuditConflict");
                ValidateCalibrationEventSemantics(item.Value, previousEvent, session.Header,
                    temporaryConfiguration);
                if (item.Value.TemporaryConfiguration is { } applied)
                {
                    AuditChainDatabase.Require(temporaryConfiguration is null &&
                        applied.RequestedHash == session.Header.Command.Plan.TemporaryConfigurationHash,
                        "CalibrationTemporaryConfigurationMismatch");
                    temporaryConfiguration = applied;
                }
                previousHash = item.ContentHash;
                previousEvent = item.Value;

                if (item.Value.Frame is { } frame)
                {
                    AuditChainDatabase.Require(manifestsById.TryGetValue(frame.FrameId, out var manifest) &&
                        manifest!.SessionId == session.Header.SessionId.ToString("D") &&
                        manifest.AuthorizationAuditSequence == item.AuthorizationAuditSequence &&
                        manifest.AuthorizationAuditHash == item.AuthorizationAuditHash &&
                        ManifestMatches(manifest, frame),
                        "CalibrationFrameManifestBindingMismatch");
                    frames.Add(frame);
                }
                if (item.Value.Observation is { } observation) observations.Add(observation);
                if (item.Value.Exclusion is { } exclusion) exclusions.Add(exclusion);
                if (item.Value.Candidate is { } candidate)
                {
                    var selectionAtCandidate = CalibrationEvidenceSelection.Evaluate(session.Header, frames,
                        observations, exclusions);
                    AuditChainDatabase.Require(candidate.SessionHeaderHash == session.Header.ContentHash &&
                        candidate.SelectionHash == selectionAtCandidate.SelectionHash,
                        "CalibrationCandidateSelectionMismatch");
                    candidates.Add(candidate);
                }
            }

            var first = sessionEvents[0];
            AuditChainDatabase.Require(first.Value.IsAdmission &&
                first.Value.EventId == session.Header.AdmissionAttemptId &&
                first.Value.OperationId == session.Header.Command.CorrelationId &&
                first.Value.Phase == CalibrationSessionPhase.Admitted &&
                first.Value.Outcome == CalibrationSessionOutcome.Pending &&
                first.Value.ReasonCode == "CalibrationSessionAdmitted" &&
                first.AuthorizationAuditSequence == session.AdmissionAuditSequence &&
                first.AuthorizationAuditHash == session.AdmissionAuditHash,
                "CalibrationAdmissionEventMismatch");
            AuditChainDatabase.Require(frames.Count <= options.MaximumFramesPerSession,
                "CalibrationFrameCapacityExceeded");
            var totalBytes = frames.Aggregate(0L, (total, frame) => checked(total + frame.ByteLength));
            AuditChainDatabase.Require(frames.All(frame => frame.ByteLength <= options.MaximumFrameBytes) &&
                totalBytes <= options.MaximumTotalFrameBytes, "CalibrationFrameTotalCapacityExceeded");
            _ = CalibrationEvidenceSelection.Evaluate(session.Header, frames, observations, exclusions);
            AuditChainDatabase.Require(candidates.Select(item => item.CandidateId).Distinct().Count() ==
                candidates.Count, "CalibrationCandidateConflict");
        }
        AuditChainDatabase.Require(expectedEventPosition - 1 == events.Count, "CalibrationEventPositionGap");
        AuditChainDatabase.Require(expectedManifestPosition - 1 == manifests.Count, "CalibrationFramePositionGap");
        foreach (var manifest in manifests)
        {
            var frameId = ParseGuidExact(manifest.FrameId, "CalibrationFrameManifestIdentityInvalid");
            AuditChainDatabase.Require(events.Any(item => item.Value.Frame?.FrameId == frameId),
                "CalibrationOrphanFrameManifest");
        }
    }

    private static void ValidateCalibrationSessionRow(sqlite3 database, CalibrationSessionRow row,
        string stationId, StoreDeadline deadline)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationSessionHeaderPayloadInvalid");
        AuditChainDatabase.Require(row.PayloadHash == CalibrationSessionStorageCodec.PayloadHash(payload) &&
            row.Header.SessionId == ParseGuidExact(row.SessionId, "CalibrationSessionIdentityInvalid") &&
            row.Header.Command.CorrelationId == ParseGuidExact(row.AdmissionCorrelationId,
                "CalibrationSessionAdmissionIdentityInvalid") &&
            row.Header.AdmissionAttemptId == ParseGuidExact(row.AdmissionAttemptId,
                "CalibrationSessionAdmissionIdentityInvalid") &&
            row.Header.RuntimeEpoch == ParseGuidExact(row.RuntimeEpoch, "CalibrationSessionIdentityInvalid") &&
            row.Header.ActorPrincipalId == ParseGuidExact(row.ActorPrincipalId, "CalibrationSessionIdentityInvalid") &&
            row.Header.InteractiveSessionId == ParseGuidExact(row.InteractiveSessionId,
                "CalibrationSessionIdentityInvalid") &&
            row.Header.AuthorizationRevision == row.AuthorizationRevision &&
            row.Header.Command.Invocation.StepUpGrantId == ParseGuidExact(row.StepUpGrantId,
                "CalibrationSessionGrantInvalid") && row.Header.Binding.LogicalRole == row.LogicalRole &&
            row.ContentHash == CalibrationSessionStorageCodec.SessionContentHash(row.Header, row.Position,
                payload, row.AdmissionAuditSequence, row.AdmissionAuditHash),
            "CalibrationSessionRowBindingMismatch");
        var audit = ReadIdentityAudit(database, row.AdmissionAuditSequence, deadline);
        AuditChainDatabase.Require(audit is { } && audit.Value.Hash == row.AdmissionAuditHash &&
            IdentityAuditEvent.MatchesCalibrationAuthorization(audit.Value.Payload, audit.Value.Ordinal,
                stationId, row.Header, AuditedCommandKind.StartCalibrationSession,
                row.Header.Command.CorrelationId, row.Header.Command.AuthorizationTarget),
            "CalibrationAdmissionAuditBindingMismatch");
        var signed = ReadCalibrationLedgerAudit(database, row.AuditSequence, deadline);
        AuditChainDatabase.Require(signed is { Kind: "CalibrationSessionHeader" } &&
            signed.Value.SessionPosition == row.Position && signed.Value.EventPosition is null &&
            signed.Value.ManifestPosition is null && signed.Value.Payload == row.Payload &&
            signed.Value.Hash == row.AuditHash,
            "CalibrationSessionSignedAuditBindingMismatch");
    }

    private static void ValidateCalibrationEventRow(sqlite3 database, CalibrationSessionHeader header,
        CalibrationSessionEventRow row, string? previousHash, string stationId, StoreDeadline deadline)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationSessionEventPayloadInvalid");
        var eventId = ParseGuidExact(row.EventId, "CalibrationEventIdentityInvalid");
        var operationId = ParseGuidExact(row.OperationId, "CalibrationEventIdentityInvalid");
        var sessionId = ParseGuidExact(row.SessionId, "CalibrationEventIdentityInvalid");
        AuditChainDatabase.Require(row.Value.EventId == eventId && row.Value.OperationId == operationId &&
            row.Value.SessionId == sessionId && row.Value.SessionId == header.SessionId &&
            row.Kind == row.Value.Kind && row.PayloadHash == CalibrationSessionStorageCodec.PayloadHash(payload) &&
            row.PreviousHash == previousHash &&
            row.ContentHash == CalibrationSessionStorageCodec.EventContentHash(row.Value, row.Sequence,
                previousHash, payload, row.AuthorizationAuditSequence, row.AuthorizationAuditHash),
            "CalibrationEventRowBindingMismatch");
        var audit = ReadIdentityAudit(database, row.AuthorizationAuditSequence, deadline);
        AuditChainDatabase.Require(audit is { } && audit.Value.Hash == row.AuthorizationAuditHash &&
            MatchesCalibrationEventAuthorization(audit.Value.Payload, audit.Value.Ordinal, stationId, header,
                row.Value), "CalibrationAuthorizationBindingMismatch");
        var signed = ReadCalibrationLedgerAudit(database, row.AuditSequence, deadline);
        AuditChainDatabase.Require(signed is { Kind: "CalibrationSessionEvent" } &&
            signed.Value.SessionPosition is null && signed.Value.EventPosition == row.Position &&
            signed.Value.ManifestPosition is null && signed.Value.Payload == row.Payload &&
            signed.Value.Hash == row.AuditHash,
            "CalibrationEventSignedAuditBindingMismatch");
        if (row.Value.AuthorizationCommand is { } command)
            AuditChainDatabase.Require(CommandMatchesCalibrationEvent(command, row.Value),
                "CalibrationEventCommandBindingMismatch");
        if (row.Value.Exclusion is { } exclusion)
            AuditChainDatabase.Require(exclusion.ActorPrincipalId == header.ActorPrincipalId &&
                exclusion.InteractiveSessionId == header.InteractiveSessionId,
                "CalibrationExclusionAuthorizationMismatch");
    }

    private static void ValidateCalibrationManifestRow(CalibrationFrameManifestRow row,
        sqlite3 database, StoreDeadline deadline)
    {
        var payload = DecodeStoredPayload(row.Payload, "CalibrationFrameManifestPayloadInvalid");
        AuditChainDatabase.Require(row.PayloadHash == CalibrationSessionStorageCodec.PayloadHash(payload) &&
            row.Value.FrameId == ParseGuidExact(row.FrameId, "CalibrationFrameManifestIdentityInvalid") &&
            row.Value.SessionId == ParseGuidExact(row.SessionId, "CalibrationFrameManifestIdentityInvalid") &&
            row.Value.SourceHash == row.SourceHash && row.Value.RelativePath == row.RelativePath &&
            row.Value.ByteLength == row.ByteLength && row.Value.PixelHash == row.PixelHash,
            "CalibrationFrameManifestBindingMismatch");
        var audit = ReadIdentityAudit(database, row.AuthorizationAuditSequence, deadline);
        AuditChainDatabase.Require(audit is { } && audit.Value.Hash == row.AuthorizationAuditHash,
            "CalibrationFrameManifestAuthorizationMismatch");
        var signed = ReadCalibrationLedgerAudit(database, row.AuditSequence, deadline);
        AuditChainDatabase.Require(signed is { Kind: "CalibrationFrameManifest" } &&
            signed.Value.SessionPosition is null && signed.Value.EventPosition is null &&
            signed.Value.ManifestPosition == row.Position && signed.Value.Payload == row.Payload &&
            signed.Value.Hash == row.AuditHash,
            "CalibrationFrameManifestSignedAuditBindingMismatch");
    }

    private static bool ManifestMatches(CalibrationFrameManifestRow row, CalibrationFrameEvidence frame) =>
        row.Value.FrameId == frame.FrameId && row.Value.SessionId == frame.SessionId &&
        row.Value.SourceHash == frame.SourceHash && row.Value.ByteLength == frame.ByteLength &&
        row.Value.PixelHash == frame.PixelHash && row.Value.RelativePath == frame.RelativePath;

    private static Guid ParseGuidExact(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
            ? parsed : throw new InvalidOperationException(reason);

    private static long? ParseNullableLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : null;

    private static bool MatchesCalibrationEventAuthorization(byte[] payload, long ordinal,
        string stationId, CalibrationSessionHeader header, CalibrationSessionEvent value)
    {
        foreach (var kind in CalibrationAuthorizationKinds(header, value))
        {
            var target = kind == AuditedCommandKind.StartCalibrationSession
                ? header.Command.AuthorizationTarget
                : value.AuthorizationCommand is { } command ? command.AuthorizationTarget : CalibrationActionTarget(value, kind);
            if (IdentityAuditEvent.MatchesCalibrationAuthorization(payload, ordinal, stationId, header,
                    kind, value.OperationId, target)) return true;
        }
        return false;
    }

    private static IReadOnlyList<AuditedCommandKind> CalibrationAuthorizationKinds(
        CalibrationSessionHeader header, CalibrationSessionEvent value)
    {
        if (value.IsAdmission)
            return new[] { AuditedCommandKind.StartCalibrationSession };
        if (value.AuthorizationCommand is { } command)
            return new[] { AuthorizationCommandKind(command) };
        if (value.OperationId == header.Command.CorrelationId &&
            value.Frame is null && value.Observation is null && value.Exclusion is null &&
            value.Candidate is null)
            return new[] { AuditedCommandKind.StartCalibrationSession };
        if (value.Frame is not null || value.Observation is not null)
            return new[] { AuditedCommandKind.CaptureCalibrationFrame };
        if (value.Exclusion is not null)
            return new[] { AuditedCommandKind.ExcludeCalibrationFrame };
        if (value.Candidate is not null || value.Phase == CalibrationSessionPhase.Computing)
            return new[] { AuditedCommandKind.ComputeCalibrationCandidate };
        if (value.Phase is CalibrationSessionPhase.Restoring or CalibrationSessionPhase.Restored)
            return new[] { AuditedCommandKind.ExitCalibrationSession };
        // A timeout/cancellation can be recorded by the operation that was in flight;
        // the signed identity event disambiguates capture, compute and exit.
        return new[] { AuditedCommandKind.CaptureCalibrationFrame,
            AuditedCommandKind.ComputeCalibrationCandidate, AuditedCommandKind.ExitCalibrationSession };
    }

    private static bool CommandMatchesCalibrationEvent(CalibrationSessionCommand command,
        CalibrationSessionEvent value)
    {
        if (command.CorrelationId != value.OperationId || command.CalibrationSessionId != value.SessionId)
            return false;
        static bool IsRecovery(CalibrationSessionPhase phase) => phase is
            CalibrationSessionPhase.Restoring or CalibrationSessionPhase.RecoveryBlocked or
            CalibrationSessionPhase.Restored;
        static bool IsRestoredFailure(CalibrationSessionEvent value) => value.Phase != CalibrationSessionPhase.Restored ||
            value.Outcome is CalibrationSessionOutcome.Failed or CalibrationSessionOutcome.Cancelled;
        return command switch
        {
            CaptureCalibrationFrameCommand => (value.Phase is CalibrationSessionPhase.Capturing or
                CalibrationSessionPhase.Collecting or CalibrationSessionPhase.Restoring or
                CalibrationSessionPhase.RecoveryBlocked or CalibrationSessionPhase.Restored) &&
                value.Exclusion is null && value.Candidate is null &&
                (value.Phase is not CalibrationSessionPhase.Restored || IsRestoredFailure(value)),
            ExcludeCalibrationFrameCommand exclude =>
                (value.Exclusion is { } exclusion && exclusion.FrameId == exclude.FrameId &&
                 exclusion.Reason == exclude.Reason) ||
                (IsRecovery(value.Phase) && value.Frame is null && value.Observation is null &&
                 value.Exclusion is null && value.Candidate is null && IsRestoredFailure(value)),
            ComputeCalibrationCandidateCommand => (value.Phase is CalibrationSessionPhase.Computing or
                CalibrationSessionPhase.CandidateRetained or CalibrationSessionPhase.Restoring or
                CalibrationSessionPhase.RecoveryBlocked or CalibrationSessionPhase.Restored) &&
                value.Exclusion is null &&
                (value.Phase is not CalibrationSessionPhase.Restored || IsRestoredFailure(value)),
            ExitCalibrationSessionCommand exit => (value.Phase is CalibrationSessionPhase.Restoring or
                CalibrationSessionPhase.Restored or CalibrationSessionPhase.RecoveryBlocked) &&
                (value.Phase != CalibrationSessionPhase.Restored || value.Outcome == CalibrationSessionOutcome.Failed ||
                 (value.Outcome == CalibrationSessionOutcome.Cancelled) == exit.Cancel),
            _ => false
        };
    }

    private static void ValidateCalibrationEventSemantics(CalibrationSessionEvent value,
        CalibrationSessionEvent? previous, CalibrationSessionHeader header,
        CalibrationTemporaryConfigurationEvidence? priorTemporaryConfiguration)
    {
        if (value.Phase == CalibrationSessionPhase.Admitted && !value.IsAdmission)
            throw new InvalidOperationException("CalibrationAdmissionEventMismatch");
        if (value.Frame is not null && value.Phase != CalibrationSessionPhase.Capturing)
            throw new InvalidOperationException("CalibrationCapturePayloadMismatch");
        if (value.Observation is not null && value.Phase != CalibrationSessionPhase.Collecting)
            throw new InvalidOperationException("CalibrationObservationPayloadMismatch");
        if (value.Exclusion is not null && value.Phase != CalibrationSessionPhase.Collecting)
            throw new InvalidOperationException("CalibrationExclusionPayloadMismatch");
        if (value.Candidate is not null && value.Phase != CalibrationSessionPhase.CandidateRetained)
            throw new InvalidOperationException("CalibrationCandidatePayloadMismatch");
        if (value.Phase == CalibrationSessionPhase.CandidateRetained && value.Candidate is null)
            throw new InvalidOperationException("CalibrationCandidatePayloadMissing");
        var temporaryConfiguration = value.TemporaryConfiguration ?? priorTemporaryConfiguration;
        if (value.TemporaryConfiguration is { } applied &&
            (applied.RequestedHash != header.Command.Plan.TemporaryConfigurationHash ||
             applied.ContentHash != AlgorithmContractValidation.HashParts(new[]
             {
                 "sharpinspect-calibration-temporary-configuration-v1", applied.RequestedHash,
                 applied.EffectiveHash
             })))
            throw new InvalidOperationException("CalibrationTemporaryConfigurationMismatch");
        var requiresTemporaryConfiguration = value.Phase is CalibrationSessionPhase.Collecting or
            CalibrationSessionPhase.Capturing or CalibrationSessionPhase.Computing or
            CalibrationSessionPhase.CandidateRetained;
        if (requiresTemporaryConfiguration && temporaryConfiguration is null)
            throw new InvalidOperationException("CalibrationTemporaryConfigurationMissing");
        if (value.Phase == CalibrationSessionPhase.Restored &&
            value.Outcome == CalibrationSessionOutcome.Pending)
            throw new InvalidOperationException("CalibrationRestoredOutcomeInvalid");
        if (value.Phase != CalibrationSessionPhase.Restored &&
            value.Outcome != CalibrationSessionOutcome.Pending)
            throw new InvalidOperationException("CalibrationTerminalPhaseInvalid");
        if (previous is not null && previous.Phase == CalibrationSessionPhase.Restored)
            throw new InvalidOperationException("CalibrationEventAfterRestoration");
        if (value.AuthorizationCommand is { } command && !CommandMatchesCalibrationEvent(command, value))
            throw new InvalidOperationException("CalibrationEventCommandBindingMismatch");
        if (value.Phase == CalibrationSessionPhase.Restored && value.Outcome == CalibrationSessionOutcome.Completed &&
            value.AuthorizationCommand is not ExitCalibrationSessionCommand { Cancel: false })
            throw new InvalidOperationException("CalibrationTerminalPhaseInvalid");
    }

    private static AuditedCommandKind AuthorizationCommandKind(CalibrationSessionCommand command) => command switch
    {
        CaptureCalibrationFrameCommand => AuditedCommandKind.CaptureCalibrationFrame,
        ExcludeCalibrationFrameCommand => AuditedCommandKind.ExcludeCalibrationFrame,
        ComputeCalibrationCandidateCommand => AuditedCommandKind.ComputeCalibrationCandidate,
        ExitCalibrationSessionCommand => AuditedCommandKind.ExitCalibrationSession,
        _ => throw new InvalidOperationException("CalibrationCommandKindInvalid")
    };

    private static string SafeCalibrationReason(Exception exception)
    {
        var reason = exception is InvalidOperationException && !string.IsNullOrWhiteSpace(exception.Message)
            ? exception.Message : "CalibrationSessionUnavailable";
        if (reason.Length > 128 || reason.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            return "CalibrationSessionUnavailable";
        return reason;
    }

    private static bool IsStructuralCalibrationReason(string reason) =>
        reason.StartsWith("Calibration", StringComparison.Ordinal) &&
        !reason.Contains("Capacity", StringComparison.Ordinal) &&
        !reason.Contains("Pending", StringComparison.Ordinal);

    private sealed record PendingCalibrationFactRow(long Position, Guid EventId, Guid AttemptId,
        Guid CorrelationId, Guid RuntimeEpoch, DateTimeOffset OccurredAtUtc,
        AuditedCommandKind CommandKind, CommandSource? Source, string? ClaimedPrincipalId,
        Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId, CommandAuditPhase Phase,
        CommandDisposition? Disposition, string ReasonCode, string? AuthenticatedHumanPrincipalId);
}

}
