using System.Globalization;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Transfer-only shapes for calibration export. These payloads are independent of
/// the session ledger: decoding rebuilds evidence values, but never creates an
/// admission, event, or authorization history row.
/// </summary>
internal static partial class CalibrationSessionStorageCodec
{
    internal const int MaximumTransferEvidenceBytes = 16 * 1024 * 1024;
    internal const int MaximumTransferPolicyBytes = 512 * 1024;

    internal static byte[] EncodeTransferEvidence(CalibrationSessionEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateTransferEvidence(value);
        var payload = UsesTransferEvidenceV2(value)
            ? JsonSerializer.SerializeToUtf8Bytes(TransferEvidenceV2Dto.From(value), Json)
            : JsonSerializer.SerializeToUtf8Bytes(TransferEvidenceDto.From(value), Json);
        if (payload.Length is < 1 or > MaximumTransferEvidenceBytes)
            throw new InvalidOperationException("CalibrationTransferEvidencePayloadOversized");
        return payload;
    }

    internal static CalibrationSessionEvidence DecodeTransferEvidence(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumTransferEvidenceBytes)
            throw new ArgumentException("CalibrationTransferEvidencePayloadInvalid", nameof(payload));

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("FormatVersion", out var version) ||
                !version.TryGetInt32(out var formatVersion))
                throw Invalid("CalibrationTransferEvidencePayloadInvalid");

            var evidence = formatVersion switch
            {
                FormatVersion => JsonSerializer.Deserialize<TransferEvidenceDto>(payload.Span, Json)
                    ?.ToValue() ?? throw Invalid("CalibrationTransferEvidencePayloadInvalid"),
                EvidenceEventFormatVersion =>
                    JsonSerializer.Deserialize<TransferEvidenceV2Dto>(payload.Span, Json)
                        ?.ToValue() ?? throw Invalid("CalibrationTransferEvidencePayloadInvalid"),
                _ => throw Invalid("CalibrationTransferEvidenceFormatUnsupported")
            };

            var canonical = EncodeTransferEvidence(evidence);
            if (!payload.Span.SequenceEqual(canonical))
                throw Invalid("CalibrationTransferEvidenceCanonicalMismatch");
            return evidence;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not ArgumentException)
        {
            throw new ArgumentException("CalibrationTransferEvidencePayloadInvalid", nameof(payload),
                exception);
        }
    }

    internal static byte[] EncodeTransferPolicy(CalibrationAcceptancePolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(TransferPolicyDto.From(value), Json);
        if (payload.Length is < 1 or > MaximumTransferPolicyBytes)
            throw new InvalidOperationException("CalibrationTransferPolicyPayloadOversized");
        return payload;
    }

    internal static CalibrationAcceptancePolicy DecodeTransferPolicy(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumTransferPolicyBytes)
            throw new ArgumentException("CalibrationTransferPolicyPayloadInvalid", nameof(payload));
        try
        {
            var dto = JsonSerializer.Deserialize<TransferPolicyDto>(payload.Span, Json)
                ?? throw Invalid("CalibrationTransferPolicyPayloadInvalid");
            var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
            if (!payload.Span.SequenceEqual(canonical))
                throw Invalid("CalibrationTransferPolicyCanonicalMismatch");
            return dto.ToValue();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not ArgumentException)
        {
            throw new ArgumentException("CalibrationTransferPolicyPayloadInvalid", nameof(payload),
                exception);
        }
    }

    private static bool UsesTransferEvidenceV2(CalibrationSessionEvidence value) =>
        value.Candidate?.Result.Evidence is not null ||
        value.Observations.Any(observation => observation.Result.Receipt is not null);

    private static void ValidateTransferEvidence(CalibrationSessionEvidence value)
    {
        if (value.Header is null || value.State is null || value.Selection is null ||
            value.Header.SessionId != value.State.SessionId)
            throw new InvalidOperationException("CalibrationTransferEvidenceIdentityInvalid");

        if (value.Frames.Count is < 1 or > 64 ||
            value.Frames.Select(frame => frame.FrameId).Distinct().Count() != value.Frames.Count ||
            value.Frames.Any(frame => frame.SessionId != value.Header.SessionId) ||
            value.State.FrameCount != value.Frames.Count ||
            value.State.ObservationCount != value.Observations.Count ||
            value.State.ExcludedFrameCount != value.Exclusions.Count)
            throw new InvalidOperationException("CalibrationTransferEvidenceFramesInvalid");

        var frameIds = value.Frames.Select(frame => frame.FrameId).ToHashSet();
        if (value.Observations.Any(observation =>
                observation.Frame.SessionId != value.Header.SessionId ||
                !frameIds.Contains(observation.Frame.FrameId)))
            throw new InvalidOperationException("CalibrationTransferEvidenceObservationFrameMissing");

        if (value.Exclusions.Any(exclusion => !frameIds.Contains(exclusion.FrameId)))
            throw new InvalidOperationException("CalibrationTransferEvidenceExclusionFrameMissing");

        var recalculatedSelection = CalibrationEvidenceSelection.Evaluate(value.Header,
            value.Frames, value.Observations, value.Exclusions);
        if (value.Selection.Sufficient != recalculatedSelection.Sufficient ||
            value.Selection.ReasonCode != recalculatedSelection.ReasonCode ||
            value.Selection.IncludedFrameCount != recalculatedSelection.IncludedFrameCount ||
            value.Selection.SufficientFeatureFrameCount != recalculatedSelection.SufficientFeatureFrameCount ||
            value.Selection.ImageCoverage != recalculatedSelection.ImageCoverage ||
            value.Selection.SelectionHash != recalculatedSelection.SelectionHash)
            throw new InvalidOperationException("CalibrationTransferEvidenceSelectionMismatch");

        if (value.Candidate is { } candidate &&
            (candidate.SessionId != value.Header.SessionId ||
             candidate.SessionHeaderHash != value.Header.ContentHash ||
             candidate.SelectionHash != recalculatedSelection.SelectionHash))
            throw new InvalidOperationException("CalibrationTransferEvidenceCandidateBindingInvalid");
        if ((value.State.CandidateId is null) != (value.Candidate is null) ||
            value.State.CandidateId != value.Candidate?.CandidateId)
            throw new InvalidOperationException("CalibrationTransferEvidenceCandidateIdentityInvalid");

        if (value.TemporaryConfiguration is { } temporary &&
            value.Header.Command.Plan.TemporaryConfigurationHash != temporary.RequestedHash)
            throw new InvalidOperationException("CalibrationTransferEvidenceTemporaryConfigurationInvalid");
    }

    internal static void ValidateTransferEvidenceForExport(CalibrationSessionEvidence value)
    {
        ValidateTransferEvidence(value);
        if (value.TemporaryConfiguration is null)
            throw new InvalidOperationException("CalibrationTransferEvidenceTemporaryConfigurationMissing");
    }

    private sealed class TransferEvidenceDto
    {
        public int FormatVersion { get; set; }
        public HeaderDto? Header { get; set; }
        public int Phase { get; set; }
        public int Outcome { get; set; }
        public int FrameCount { get; set; }
        public int ObservationCount { get; set; }
        public int ExcludedFrameCount { get; set; }
        public string? CandidateId { get; set; }
        public string? ReasonCode { get; set; }
        public bool RestorationVerified { get; set; }
        public bool OperationInProgress { get; set; }
        public List<FrameDto>? Frames { get; set; }
        public List<ObservationDto>? Observations { get; set; }
        public List<ExclusionDto>? Exclusions { get; set; }
        public CandidateDto? Candidate { get; set; }
        public bool Sufficient { get; set; }
        public string? SelectionReasonCode { get; set; }
        public int IncludedFrameCount { get; set; }
        public int SufficientFeatureFrameCount { get; set; }
        public double ImageCoverage { get; set; }
        public string? SelectionHash { get; set; }
        public TemporaryConfigurationDto? TemporaryConfiguration { get; set; }

        internal static TransferEvidenceDto From(CalibrationSessionEvidence value) => new()
        {
            FormatVersion = CalibrationSessionStorageCodec.FormatVersion,
            Header = HeaderDto.From(value.Header),
            Phase = (int)value.State.Phase, Outcome = (int)value.State.Outcome,
            FrameCount = value.State.FrameCount, ObservationCount = value.State.ObservationCount,
            ExcludedFrameCount = value.State.ExcludedFrameCount,
            CandidateId = value.State.CandidateId?.ToString("D"), ReasonCode = value.State.ReasonCode,
            RestorationVerified = value.State.RestorationVerified,
            OperationInProgress = value.State.OperationInProgress,
            Frames = value.Frames.Select(frame => FrameDto.From(frame)!).ToList(),
            Observations = value.Observations.Select(observation => ObservationDto.From(observation)!).ToList(),
            Exclusions = value.Exclusions.Select(exclusion => ExclusionDto.From(exclusion)!).ToList(),
            Candidate = CandidateDto.From(value.Candidate),
            Sufficient = value.Selection.Sufficient, SelectionReasonCode = value.Selection.ReasonCode,
            IncludedFrameCount = value.Selection.IncludedFrameCount,
            SufficientFeatureFrameCount = value.Selection.SufficientFeatureFrameCount,
            ImageCoverage = value.Selection.ImageCoverage, SelectionHash = value.Selection.SelectionHash,
            TemporaryConfiguration = TemporaryConfigurationDto.From(value.TemporaryConfiguration)
        };

        internal CalibrationSessionEvidence ToValue()
        {
            if (FormatVersion != CalibrationSessionStorageCodec.FormatVersion || Header is null ||
                Frames is null || Observations is null || Exclusions is null ||
                SelectionReasonCode is null || SelectionHash is null ||
                !TryGuid(CandidateId, out var candidateId, allowNull: true))
                throw Invalid("CalibrationTransferEvidencePayloadInvalid");

            var header = Header.ToValue();
            var state = new CalibrationSessionState(header.SessionId, EnumValue<CalibrationSessionPhase>(Phase),
                EnumValue<CalibrationSessionOutcome>(Outcome), FrameCount, ObservationCount,
                ExcludedFrameCount, candidateId, ReasonCode ?? string.Empty, RestorationVerified,
                OperationInProgress);
            var evidence = new CalibrationSessionEvidence(header, state,
                Values(Frames, value => value?.ToValue(), "CalibrationTransferFrameMissing"),
                Values(Observations, value => value?.ToValue(), "CalibrationTransferObservationMissing"),
                Values(Exclusions, value => value?.ToValue(), "CalibrationTransferExclusionMissing"),
                Candidate?.ToValue(),
                new CalibrationSelectionEvaluation(Sufficient, SelectionReasonCode, IncludedFrameCount,
                    SufficientFeatureFrameCount, ImageCoverage, SelectionHash),
                TemporaryConfiguration?.ToValue());
            ValidateTransferEvidence(evidence);
            return evidence;
        }
    }

    private sealed class TransferEvidenceV2Dto
    {
        public int FormatVersion { get; set; }
        public HeaderDto? Header { get; set; }
        public int Phase { get; set; }
        public int Outcome { get; set; }
        public int FrameCount { get; set; }
        public int ObservationCount { get; set; }
        public int ExcludedFrameCount { get; set; }
        public string? CandidateId { get; set; }
        public string? ReasonCode { get; set; }
        public bool RestorationVerified { get; set; }
        public bool OperationInProgress { get; set; }
        public List<FrameDto>? Frames { get; set; }
        public List<ObservationV2Dto>? Observations { get; set; }
        public List<ExclusionDto>? Exclusions { get; set; }
        public CandidateV2Dto? Candidate { get; set; }
        public bool Sufficient { get; set; }
        public string? SelectionReasonCode { get; set; }
        public int IncludedFrameCount { get; set; }
        public int SufficientFeatureFrameCount { get; set; }
        public double ImageCoverage { get; set; }
        public string? SelectionHash { get; set; }
        public TemporaryConfigurationDto? TemporaryConfiguration { get; set; }

        internal static TransferEvidenceV2Dto From(CalibrationSessionEvidence value) => new()
        {
            FormatVersion = EvidenceEventFormatVersion,
            Header = HeaderDto.From(value.Header),
            Phase = (int)value.State.Phase, Outcome = (int)value.State.Outcome,
            FrameCount = value.State.FrameCount, ObservationCount = value.State.ObservationCount,
            ExcludedFrameCount = value.State.ExcludedFrameCount,
            CandidateId = value.State.CandidateId?.ToString("D"), ReasonCode = value.State.ReasonCode,
            RestorationVerified = value.State.RestorationVerified,
            OperationInProgress = value.State.OperationInProgress,
            Frames = value.Frames.Select(frame => FrameDto.From(frame)!).ToList(),
            Observations = value.Observations.Select(observation => ObservationV2Dto.From(observation)!).ToList(),
            Exclusions = value.Exclusions.Select(exclusion => ExclusionDto.From(exclusion)!).ToList(),
            Candidate = CandidateV2Dto.From(value.Candidate),
            Sufficient = value.Selection.Sufficient, SelectionReasonCode = value.Selection.ReasonCode,
            IncludedFrameCount = value.Selection.IncludedFrameCount,
            SufficientFeatureFrameCount = value.Selection.SufficientFeatureFrameCount,
            ImageCoverage = value.Selection.ImageCoverage, SelectionHash = value.Selection.SelectionHash,
            TemporaryConfiguration = TemporaryConfigurationDto.From(value.TemporaryConfiguration)
        };

        internal CalibrationSessionEvidence ToValue()
        {
            if (FormatVersion != EvidenceEventFormatVersion || Header is null ||
                Frames is null || Observations is null || Exclusions is null ||
                SelectionReasonCode is null || SelectionHash is null ||
                !TryGuid(CandidateId, out var candidateId, allowNull: true))
                throw Invalid("CalibrationTransferEvidencePayloadInvalid");

            var header = Header.ToValue();
            var state = new CalibrationSessionState(header.SessionId, EnumValue<CalibrationSessionPhase>(Phase),
                EnumValue<CalibrationSessionOutcome>(Outcome), FrameCount, ObservationCount,
                ExcludedFrameCount, candidateId, ReasonCode ?? string.Empty, RestorationVerified,
                OperationInProgress);
            var evidence = new CalibrationSessionEvidence(header, state,
                Values(Frames, value => value?.ToValue(), "CalibrationTransferFrameMissing"),
                Values(Observations, value => value?.ToValue(), "CalibrationTransferObservationMissing"),
                Values(Exclusions, value => value?.ToValue(), "CalibrationTransferExclusionMissing"),
                Candidate?.ToValue(),
                new CalibrationSelectionEvaluation(Sufficient, SelectionReasonCode, IncludedFrameCount,
                    SufficientFeatureFrameCount, ImageCoverage, SelectionHash),
                TemporaryConfiguration?.ToValue());
            ValidateTransferEvidence(evidence);
            return evidence;
        }
    }

    private sealed class TransferPolicyDto
    {
        public int FormatVersion { get; set; }
        public string? Id { get; set; }
        public string? Version { get; set; }
        public int Kind { get; set; }
        public string? LogicalPurpose { get; set; }
        public ContractDto? ProcedureContract { get; set; }
        public ContractDto? InputContract { get; set; }
        public ContractDto? CoefficientContract { get; set; }
        public ContractDto? ExtractionReceiptContract { get; set; }
        public ContractDto? ComputationEvidenceContract { get; set; }
        public TransferPolicySectionDto? Sample { get; set; }
        public TransferPolicySectionDto? Coverage { get; set; }
        public TransferPolicySectionDto? PoseDiversity { get; set; }
        public TransferPolicySectionDto? MaximumPerImageResidual { get; set; }
        public TransferPolicySectionDto? MaximumPerPointResidual { get; set; }
        public TransferPolicySectionDto? InvalidObservation { get; set; }
        public TransferPhysicalRequirementDto? PhysicalVerification { get; set; }
        public string? ContentHash { get; set; }

        internal static TransferPolicyDto From(CalibrationAcceptancePolicy value) => new()
        {
            FormatVersion = 1, Id = value.Id, Version = value.Version, Kind = (int)value.Kind,
            LogicalPurpose = value.LogicalPurpose,
            ProcedureContract = ContractDto.From(value.ProcedureContract),
            InputContract = ContractDto.From(value.InputContract),
            CoefficientContract = ContractDto.From(value.CoefficientContract),
            ExtractionReceiptContract = ContractDto.From(value.ExtractionReceiptContract),
            ComputationEvidenceContract = ContractDto.From(value.ComputationEvidenceContract),
            Sample = TransferPolicySectionDto.From(value.Sample),
            Coverage = TransferPolicySectionDto.From(value.Coverage),
            PoseDiversity = TransferPolicySectionDto.From(value.PoseDiversity),
            MaximumPerImageResidual = TransferPolicySectionDto.From(value.MaximumPerImageResidual),
            MaximumPerPointResidual = TransferPolicySectionDto.From(value.MaximumPerPointResidual),
            InvalidObservation = TransferPolicySectionDto.From(value.InvalidObservation),
            PhysicalVerification = TransferPhysicalRequirementDto.From(value.PhysicalVerification),
            ContentHash = value.ContentHash
        };

        internal CalibrationAcceptancePolicy ToValue()
        {
            if (FormatVersion != 1 || Id is null || Version is null || LogicalPurpose is null ||
                ProcedureContract is null || InputContract is null || CoefficientContract is null ||
                ExtractionReceiptContract is null || ComputationEvidenceContract is null ||
                Sample is null || Coverage is null || PoseDiversity is null ||
                MaximumPerImageResidual is null || MaximumPerPointResidual is null ||
                InvalidObservation is null || PhysicalVerification is null || ContentHash is null)
                throw Invalid("CalibrationTransferPolicyPayloadInvalid");

            var value = new CalibrationAcceptancePolicy(Id, Version, EnumValue<CalibrationKind>(Kind),
                LogicalPurpose, ProcedureContract.ToValue(), InputContract.ToValue(),
                CoefficientContract.ToValue(), ExtractionReceiptContract.ToValue(),
                ComputationEvidenceContract.ToValue(),
                Sample.ToValue(CalibrationAcceptanceGateCategory.Sample),
                Coverage.ToValue(CalibrationAcceptanceGateCategory.Coverage),
                PoseDiversity.ToValue(CalibrationAcceptanceGateCategory.PoseDiversity),
                MaximumPerImageResidual.ToValue(CalibrationAcceptanceGateCategory.MaximumPerImageResidual),
                MaximumPerPointResidual.ToValue(CalibrationAcceptanceGateCategory.MaximumPerPointResidual),
                InvalidObservation.ToValue(CalibrationAcceptanceGateCategory.InvalidObservation),
                PhysicalVerification.ToValue());
            if (value.ContentHash != ContentHash)
                throw Invalid("CalibrationTransferPolicyHashMismatch");
            return value;
        }
    }

    private sealed class TransferPolicySectionDto
    {
        public int Category { get; set; }
        public int Applicability { get; set; }
        public List<TransferPolicyGateDto>? Gates { get; set; }
        public string? NotApplicableReason { get; set; }

        internal static TransferPolicySectionDto From(CalibrationGateSection value) => new()
        {
            Category = (int)value.Category, Applicability = (int)value.Applicability,
            Gates = value.Gates.Select(TransferPolicyGateDto.From).ToList(),
            NotApplicableReason = value.NotApplicableReason
        };

        internal CalibrationGateSection ToValue(CalibrationAcceptanceGateCategory expected)
        {
            if (EnumValue<CalibrationAcceptanceGateCategory>(Category) != expected || Gates is null)
                throw Invalid("CalibrationTransferPolicySectionInvalid");
            return new(expected, EnumValue<CalibrationPolicyApplicability>(Applicability),
                Values(Gates, value => value?.ToValue(), "CalibrationTransferPolicyGateMissing"),
                NotApplicableReason);
        }
    }

    private sealed class TransferPolicyGateDto
    {
        public string? GateId { get; set; }
        public int FactKind { get; set; }
        public string? FactKey { get; set; }
        public string? FactUnit { get; set; }
        public int Comparison { get; set; }
        public double Threshold { get; set; }

        internal static TransferPolicyGateDto From(CalibrationMetricGate value) => new()
        {
            GateId = value.GateId, FactKind = (int)value.Fact.Kind,
            FactKey = value.Fact.Key, FactUnit = value.Fact.Unit,
            Comparison = (int)value.Comparison, Threshold = value.Threshold
        };

        internal CalibrationMetricGate ToValue()
        {
            if (GateId is null || FactKey is null)
                throw Invalid("CalibrationTransferPolicyGateInvalid");
            var factKind = EnumValue<CalibrationPolicyFactKind>(FactKind);
            var fact = factKind switch
            {
                CalibrationPolicyFactKind.IncludedFrameCount =>
                    ExactFact(CalibrationPolicyFactReference.IncludedFrameCount),
                CalibrationPolicyFactKind.SufficientFeatureFrameCount =>
                    ExactFact(CalibrationPolicyFactReference.SufficientFeatureFrameCount),
                CalibrationPolicyFactKind.SelectionImageCoverage =>
                    ExactFact(CalibrationPolicyFactReference.SelectionImageCoverage),
                CalibrationPolicyFactKind.ExcludedFrameCount =>
                    ExactFact(CalibrationPolicyFactReference.ExcludedFrameCount),
                CalibrationPolicyFactKind.ProcedureMetric =>
                    CalibrationPolicyFactReference.ProcedureMetric(FactKey, FactUnit),
                CalibrationPolicyFactKind.PhysicalVerificationMetric =>
                    CalibrationPolicyFactReference.PhysicalVerificationMetric(FactKey, FactUnit),
                _ => throw Invalid("CalibrationTransferPolicyFactUnsupported")
            };
            return new CalibrationMetricGate(GateId, fact,
                EnumValue<CalibrationGateComparison>(Comparison), Threshold);
        }

        private CalibrationPolicyFactReference ExactFact(CalibrationPolicyFactReference expected) =>
            expected.Key == FactKey && expected.Unit == FactUnit
                ? expected
                : throw Invalid("CalibrationTransferPolicyFactCanonicalMismatch");
    }

    private sealed class TransferPhysicalRequirementDto
    {
        public int Applicability { get; set; }
        public ContractDto? ProcedureContract { get; set; }
        public ContractDto? EvidenceContract { get; set; }
        public ContractDto? IndependentReference { get; set; }
        public long? ValidityTicks { get; set; }
        public List<TransferPolicyGateDto>? Gates { get; set; }
        public string? NotApplicableReason { get; set; }

        internal static TransferPhysicalRequirementDto From(
            PhysicalCalibrationVerificationRequirement value) => new()
        {
            Applicability = (int)value.Applicability,
            ProcedureContract = value.ProcedureContract is null ? null : ContractDto.From(value.ProcedureContract),
            EvidenceContract = value.EvidenceContract is null ? null : ContractDto.From(value.EvidenceContract),
            IndependentReference = value.IndependentReference is null ? null : ContractDto.From(value.IndependentReference),
            ValidityTicks = value.ValidityInterval?.Ticks,
            Gates = value.Gates.Select(TransferPolicyGateDto.From).ToList(),
            NotApplicableReason = value.NotApplicableReason
        };

        internal PhysicalCalibrationVerificationRequirement ToValue()
        {
            if (Gates is null)
                throw Invalid("CalibrationTransferPhysicalRequirementInvalid");
            TimeSpan? validity = null;
            if (ValidityTicks is { } ticks)
            {
                try { validity = new TimeSpan(ticks); }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw Invalid("CalibrationTransferPhysicalRequirementInvalid", exception);
                }
            }
            return new(EnumValue<CalibrationPolicyApplicability>(Applicability),
                ProcedureContract?.ToValue(), EvidenceContract?.ToValue(), IndependentReference?.ToValue(),
                validity, Values(Gates, value => value?.ToValue(), "CalibrationTransferPolicyGateMissing"),
                NotApplicableReason);
        }
    }

    private static T EnumValue<T>(int value) where T : struct, Enum
    {
        var parsed = Enum.ToObject(typeof(T), value);
        if (!Enum.IsDefined(typeof(T), parsed) || parsed is not T typed)
            throw Invalid("CalibrationTransferEnumInvalid");

        long parsedNumeric;
        try
        {
            parsedNumeric = Convert.ToInt64(parsed);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            throw Invalid("CalibrationTransferEnumInvalid", exception);
        }

        return parsedNumeric == value
            ? typed
            : throw Invalid("CalibrationTransferEnumInvalid");
    }

    private static bool TryGuid(string? text, out Guid value, bool allowNull)
    {
        if (text is null && allowNull)
        {
            value = default;
            return true;
        }
        return Guid.TryParseExact(text, "D", out value) && value != Guid.Empty;
    }

    private static IEnumerable<TValue> Values<TSource, TValue>(IEnumerable<TSource> values, Func<TSource, TValue?> convert,
        string reason) where TValue : class
    {
        foreach (var value in values)
        {
            var converted = convert(value);
            if (converted is null)
                throw Invalid(reason);
            yield return converted;
        }
    }

    private static InvalidOperationException Invalid(string reason, Exception? inner = null) =>
        new(reason, inner);
}
