using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>One bounded diagnostic elevation: a capture session or one support bundle export.</summary>
internal enum DiagnosticOperationKind
{
    Capture = 1,
    Bundle = 2
}

/// <summary>The closed durable lifecycle of one diagnostic operation. No active state is recoverable.</summary>
internal enum DiagnosticOperationPhase
{
    Admitted = 1,
    Sealed = 2,
    Completed = 3,
    Failed = 4,
    Interrupted = 5
}

/// <summary>
/// The fully serialized bounded primitive projection of a <see cref="DiagnosticCaptureProfile"/>.
/// The public abstraction's validating constructor never participates in persistence, so the
/// stored bytes stay independent of later public type changes.
/// </summary>
internal sealed record DiagnosticCaptureProfileFields
{
    public string LoggingPolicyHash { get; init; } = string.Empty;
    public string[] Components { get; init; } = Array.Empty<string>();
    public int MinimumLevel { get; init; }
    public long DurationTicks { get; init; }
    public int MaximumEvents { get; init; }
    public string ContentHash { get; init; } = string.Empty;

    internal static DiagnosticCaptureProfileFields From(DiagnosticCaptureProfile profile) => new()
    {
        LoggingPolicyHash = profile.LoggingPolicyHash,
        Components = profile.Components.ToArray(),
        MinimumLevel = (int)profile.MinimumLevel,
        DurationTicks = profile.Duration.Ticks,
        MaximumEvents = profile.MaximumEvents,
        ContentHash = profile.ContentHash
    };

    internal void Validate()
    {
        if (LoggingPolicyHash is not { Length: 64 } ||
            Components is null || Components.Length is < 1 or > 32 ||
            Components.Any(value => value is not { Length: > 0 and <= 128 }) ||
            Components.Distinct(StringComparer.Ordinal).Count() != Components.Length ||
            !Components.SequenceEqual(Components.OrderBy(value => value, StringComparer.Ordinal)) ||
            MinimumLevel is not ((int)DiagnosticLevel.Trace or (int)DiagnosticLevel.Debug) ||
            DurationTicks < 1 || DurationTicks > TimeSpan.FromHours(1).Ticks ||
            MaximumEvents is < 1 or > 1_000_000 || ContentHash is not { Length: 64 })
            throw new InvalidOperationException("DiagnosticCaptureProfileInvalid");
        var expected = AlgorithmContractValidation.HashParts(new[] { "diagnostic-capture-profile-v1", LoggingPolicyHash,
            ((DiagnosticLevel)MinimumLevel).ToString(), DurationTicks.ToString(CultureInfo.InvariantCulture),
            MaximumEvents.ToString(CultureInfo.InvariantCulture) }.Concat(Components));
        if (expected != ContentHash) throw new InvalidOperationException("DiagnosticCaptureProfileHashMismatch");
    }
}

/// <summary>One fully serialized typed execution correlation of a support-bundle scope.</summary>
internal sealed record SupportBundleExecutionField
{
    public int Kind { get; init; }
    public Guid Value { get; init; }
}

/// <summary>
/// The fully serialized bounded primitive projection of a <see cref="SupportBundleScope"/>.
/// </summary>
internal sealed record SupportBundleScopeFields
{
    public Guid RuntimeEpoch { get; init; }
    public string FromUtc { get; init; } = string.Empty;
    public string ThroughUtc { get; init; } = string.Empty;
    public SupportBundleExecutionField[] Executions { get; init; } = Array.Empty<SupportBundleExecutionField>();
    public string[] CommandCorrelations { get; init; } = Array.Empty<string>();
    public string[] CaptureSessions { get; init; } = Array.Empty<string>();
    public string ContentHash { get; init; } = string.Empty;

    internal static SupportBundleScopeFields From(SupportBundleScope scope) => new()
    {
        RuntimeEpoch = scope.RuntimeEpoch,
        FromUtc = scope.FromUtc.ToString("O", CultureInfo.InvariantCulture),
        ThroughUtc = scope.ThroughUtc.ToString("O", CultureInfo.InvariantCulture),
        Executions = scope.Executions.Select(value => new SupportBundleExecutionField
        {
            Kind = (int)value.Kind, Value = value.Value
        }).ToArray(),
        CommandCorrelations = scope.CommandCorrelations.Select(value => value.ToString("D")).ToArray(),
        CaptureSessions = scope.CaptureSessions.Select(value => value.ToString("D")).ToArray(),
        ContentHash = scope.ContentHash
    };

    internal void Validate()
    {
        if (RuntimeEpoch == Guid.Empty || !Utc(FromUtc) || !Utc(ThroughUtc) ||
            !DateTimeOffset.TryParseExact(FromUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateTimeOffset.TryParseExact(ThroughUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var through) ||
            from.Offset != TimeSpan.Zero || through.Offset != TimeSpan.Zero || through <= from ||
            through - from > TimeSpan.FromDays(31))
            throw new InvalidOperationException("DiagnosticSupportScopeInvalid");
        var executions = Executions ?? throw new InvalidOperationException("DiagnosticSupportScopeInvalid");
        var commands = CommandCorrelations ?? throw new InvalidOperationException("DiagnosticSupportScopeInvalid");
        var captures = CaptureSessions ?? throw new InvalidOperationException("DiagnosticSupportScopeInvalid");
        if (executions.Length + commands.Length + captures.Length > 64 ||
            executions.Any(value => value is null || !Enum.IsDefined(typeof(ExecutionKind), value.Kind) ||
                value.Value == Guid.Empty) ||
            commands.Distinct(StringComparer.Ordinal).Count() != commands.Length ||
            captures.Distinct(StringComparer.Ordinal).Count() != captures.Length ||
            commands.Any(value => !Guid.TryParseExact(value, "D", out _)) ||
            captures.Any(value => !Guid.TryParseExact(value, "D", out _)) ||
            executions.Select(value => (value.Kind, value.Value)).Distinct().Count() != executions.Length ||
            ContentHash is not { Length: 64 })
            throw new InvalidOperationException("DiagnosticSupportScopeInvalid");
        var expected = AlgorithmContractValidation.HashParts(new[] { "support-bundle-scope-v1", RuntimeEpoch.ToString("D"),
            FromUtc, ThroughUtc, "executions" }
            .Concat(executions.OrderBy(value => value.Kind).ThenBy(value => value.Value)
                .SelectMany(value => new[] { ((ExecutionKind)value.Kind).ToString(), value.Value.ToString("D") }))
            .Concat(new[] { "commands" }).Concat(commands.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(new[] { "captures" }).Concat(captures.OrderBy(value => value, StringComparer.Ordinal)));
        if (expected != ContentHash) throw new InvalidOperationException("DiagnosticSupportScopeHashMismatch");
    }

    private static bool Utc(string? value) => value is { Length: > 0 and <= 64 } && value.EndsWith("+00:00", StringComparison.Ordinal);
}

/// <summary>One exact signed audit reference of the authorizing command or identity event.</summary>
internal sealed record DiagnosticAuditReference
{
    public Guid EventId { get; init; }
    public long Sequence { get; init; }
    public string Hash { get; init; } = string.Empty;

    internal void Validate()
    {
        if (EventId == Guid.Empty || Sequence <= 0 || Hash is not { Length: 64 })
            throw new InvalidOperationException("DiagnosticSupportAuditReferenceInvalid");
    }
}

/// <summary>
/// One durable immutable diagnostic-operation fact. Every field is a bounded primitive or a
/// bounded primitive projection; no public abstraction type is serialized directly.
/// </summary>
internal sealed record DiagnosticOperationPayload
{
    public int Version { get; init; } = 1;
    public Guid EventId { get; init; }
    public Guid OperationId { get; init; }
    public Guid CommandCorrelationId { get; init; }
    public Guid RuntimeEpoch { get; init; }
    public DiagnosticOperationKind Kind { get; init; }
    public DiagnosticOperationPhase Phase { get; init; }
    public int AggregateSequence { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DiagnosticSupportReason Reason { get; init; }
    public string ReasonCode { get; init; } = string.Empty;
    public Guid ActorPrincipalId { get; init; }
    public Guid SessionId { get; init; }
    public Guid StepUpGrantId { get; init; }
    public long AuthorizationRevision { get; init; }
    public string AuthorizationTarget { get; init; } = string.Empty;
    public string AuthorizationPolicyId { get; init; } = string.Empty;
    public string AuthorizationPolicyVersion { get; init; } = string.Empty;
    public string AuthorizationPolicyHash { get; init; } = string.Empty;
    public string LoggingPolicyHash { get; init; } = string.Empty;
    public string SupportPolicyHash { get; init; } = string.Empty;
    public string ConfigurationHash { get; init; } = string.Empty;
    public DiagnosticAuditReference Command { get; init; } = new();
    public DiagnosticAuditReference Authorization { get; init; } = new();
    public DateTimeOffset? AdmittedDeadlineUtc { get; init; }
    public int? AdmittedMaximumEvents { get; init; }
    public int? ObservedEvents { get; init; }
    public DiagnosticSupportReason? StopReason { get; init; }
    public DiagnosticCaptureProfileFields? CaptureProfile { get; init; }
    public SupportBundleScopeFields? SupportScope { get; init; }
    public Guid? BundleId { get; init; }
    public string? BundleContentHash { get; init; }
    public long? BundleBytes { get; init; }
    public DateTimeOffset? BundleExpiresAtUtc { get; init; }
    public DiagnosticAuditReference? StopCommand { get; init; }
    public DiagnosticAuditReference? StopAuthorization { get; init; }
    public string? StopAuthorizationTarget { get; init; }
}

internal sealed record DiagnosticOperationStoredRow(long Position, DiagnosticOperationPayload Payload,
    byte[] PayloadBytes, string ContentHash, long AuditSequence, string AuditHash)
{
    internal DiagnosticOperationState ToState() => new(Payload.OperationId, Payload.Kind, Payload.Phase,
        Payload.AggregateSequence, Payload.ObservedAtUtc, Payload.ReasonCode, Payload);
}

internal sealed record DiagnosticOperationState(Guid OperationId, DiagnosticOperationKind Kind,
    DiagnosticOperationPhase Phase, int AggregateSequence, DateTimeOffset ObservedAtUtc,
    string ReasonCode, DiagnosticOperationPayload Payload);

internal sealed record DiagnosticSupportConfiguration(string OptionsHash, string SupportPolicyHash,
    string PolicyId, string PolicyVersion, string ApprovalReference, string SupportRootBindingHash,
    string LoggingPolicyHash, string TracePolicyStoreHash, string RetentionConfigurationHash,
    long MaximumScopeTicks, int MaximumSourceRecords, int MaximumSourceBytes, int MaximumBundleBytes,
    long ExportTimeoutTicks, long AuthorizationCheckIntervalTicks, long PolicyRetentionTicks,
    int MaximumOperationFacts, int MaximumAuditPayloadBytes, long MaximumAuditBytes,
    int MaximumOperations, long MaximumTotalBytes)
{
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(Encode()));
}

internal static class DiagnosticOperationStorageCodec
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };
    private static readonly JsonSerializerOptions ConfigurationJson = new() { MaxDepth = 12 };

    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException("DiagnosticSupport" + reason);
    }

    internal static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    internal static void ValidateConfiguration(DiagnosticSupportConfiguration value)
    {
        Require(value is not null && Hash(value.OptionsHash) && Hash(value.SupportPolicyHash) &&
            Hash(value.SupportRootBindingHash) && Hash(value.LoggingPolicyHash) &&
            Hash(value.TracePolicyStoreHash) && Hash(value.RetentionConfigurationHash) &&
            value.PolicyId is { Length: > 0 and <= 128 } && value.PolicyVersion is { Length: > 0 and <= 128 } &&
            value.ApprovalReference is { Length: > 0 and <= 128 } &&
            value.MaximumScopeTicks >= 1 && value.MaximumScopeTicks <= TimeSpan.FromDays(31).Ticks &&
            value.MaximumSourceRecords is >= 1 and <= 1000 &&
            value.MaximumSourceBytes is >= 256 and <= 4 * 1024 * 1024 &&
            value.MaximumBundleBytes is >= 4096 and <= 16 * 1024 * 1024 &&
            value.ExportTimeoutTicks >= TimeSpan.FromSeconds(1).Ticks && value.ExportTimeoutTicks <= TimeSpan.FromMinutes(5).Ticks &&
            value.AuthorizationCheckIntervalTicks >= TimeSpan.FromMilliseconds(10).Ticks && value.AuthorizationCheckIntervalTicks <= TimeSpan.FromSeconds(1).Ticks &&
            value.PolicyRetentionTicks >= TimeSpan.FromSeconds(1).Ticks && value.PolicyRetentionTicks <= TimeSpan.FromDays(3650).Ticks &&
            value.MaximumOperationFacts is >= 16 and <= 100_000 &&
            value.MaximumAuditPayloadBytes is >= 4096 and <= 64 * 1024 &&
            value.MaximumAuditBytes >= value.MaximumAuditPayloadBytes && value.MaximumAuditBytes <= 512L * 1024 * 1024 &&
            value.MaximumOperations is >= 4 and <= 100_000 && value.MaximumTotalBytes >= 4096 &&
            value.MaximumTotalBytes <= 512L * 1024 * 1024, "ConfigurationInvalid");
    }

    internal static byte[] EncodeConfiguration(DiagnosticSupportConfiguration value)
    {
        ValidateConfiguration(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ConfigurationJson);
        Require(bytes.Length is >= 2 and <= 65536, "ConfigurationCapacityExceeded");
        return bytes;
    }

    internal static DiagnosticSupportConfiguration DecodeConfiguration(byte[] bytes)
    {
        Require(bytes.Length is >= 2 and <= 65536, "ConfigurationSizeInvalid");
        DiagnosticSupportConfiguration? value;
        try { value = JsonSerializer.Deserialize<DiagnosticSupportConfiguration>(bytes, ConfigurationJson); }
        catch (JsonException) { throw new InvalidOperationException("DiagnosticSupportConfigurationInvalid"); }
        Require(value is not null, "ConfigurationMissing");
        ValidateConfiguration(value!);
        Require(bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value!, ConfigurationJson)),
            "ConfigurationNoncanonical");
        return value!;
    }

    internal static void ValidatePayload(DiagnosticOperationPayload value, DiagnosticSupportConfiguration configuration)
    {
        Require(value is not null && value.Version == 1 && value.EventId != Guid.Empty &&
            value.OperationId != Guid.Empty && value.CommandCorrelationId != Guid.Empty &&
            value.RuntimeEpoch != Guid.Empty && Enum.IsDefined(value.Kind) && Enum.IsDefined(value.Phase) &&
            value.AggregateSequence is >= 1 and <= 4 && Utc(value.ObservedAtUtc) && Enum.IsDefined(value.Reason) &&
            value.ActorPrincipalId != Guid.Empty && value.SessionId != Guid.Empty && value.StepUpGrantId != Guid.Empty &&
            value.AuthorizationRevision >= 0 && Hash(value.AuthorizationTarget) &&
            value.AuthorizationPolicyId is { Length: > 0 and <= 128 } &&
            value.AuthorizationPolicyVersion is { Length: > 0 and <= 128 } &&
            Hash(value.AuthorizationPolicyHash) &&
            Hash(value.LoggingPolicyHash) &&
            Hash(value.SupportPolicyHash) && Hash(value.ConfigurationHash) &&
            value.Command is not null && value.Authorization is not null &&
            value.LoggingPolicyHash == configuration.LoggingPolicyHash &&
            value.SupportPolicyHash == configuration.SupportPolicyHash &&
            value.ConfigurationHash == configuration.OptionsHash, "PayloadIdentityInvalid");
        value.Command.Validate();
        value.Authorization.Validate();
        Require(value.ReasonCode is { Length: > 0 and <= 128 } &&
            value.ReasonCode.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')),
            "PayloadReasonInvalid");
        Require(value.AdmittedDeadlineUtc is { } deadline && Utc(deadline), "PayloadDeadlineInvalid");
        if (value.Kind == DiagnosticOperationKind.Capture)
        {
            Require(value.CaptureProfile is not null && value.SupportScope is null, "PayloadKindInvalid");
            value.CaptureProfile!.Validate();
            Require(value.ObservedEvents is null || value.ObservedEvents >= 0 &&
                value.ObservedEvents <= value.AdmittedMaximumEvents, "CaptureObservedEventsInvalid");
            Require(value.StopReason is null || Enum.IsDefined(value.StopReason.Value), "CaptureStopReasonInvalid");
            Require(value.AdmittedMaximumEvents == value.CaptureProfile.MaximumEvents, "PayloadCapInvalid");
            Require(value.CaptureProfile.LoggingPolicyHash == value.LoggingPolicyHash, "PayloadLoggingPolicyMismatch");
        }
        else
        {
            Require(value.SupportScope is not null && value.CaptureProfile is null, "PayloadKindInvalid");
            value.SupportScope!.Validate();
            Require(value.SupportScope.RuntimeEpoch == value.RuntimeEpoch && value.AdmittedMaximumEvents is null,
                "PayloadScopeMismatch");
            Require(value.ObservedEvents is null && value.StopReason is null, "BundleCaptureFieldsForbidden");
        }
        Require((value.BundleId is null || value.BundleId != Guid.Empty) &&
            (value.BundleContentHash is null || Hash(value.BundleContentHash)) &&
            (value.BundleBytes is null or >= 0) &&
            (value.BundleExpiresAtUtc is null || Utc(value.BundleExpiresAtUtc.Value)) &&
            (value.StopCommand is null || value.StopCommand.EventId != Guid.Empty) &&
            (value.StopAuthorization is null || value.StopAuthorization.EventId != Guid.Empty) &&
            (value.StopAuthorizationTarget is null || Hash(value.StopAuthorizationTarget)), "PayloadBundleFieldsInvalid");
        value.StopCommand?.Validate();
        value.StopAuthorization?.Validate();
        if (value.Phase == DiagnosticOperationPhase.Admitted)
        {
            Require(value.AggregateSequence == 1 && value.BundleId is null && value.BundleContentHash is null &&
                value.BundleBytes is null && value.BundleExpiresAtUtc is null &&
                value.StopCommand is null && value.StopAuthorization is null &&
                value.StopAuthorizationTarget is null, "PayloadPhaseInvalid");
        }
        else if (value.AggregateSequence == 1)
        {
            throw new InvalidOperationException("DiagnosticSupportPayloadPhaseInvalid");
        }
    }

    internal static byte[] Encode(DiagnosticOperationPayload value, DiagnosticSupportConfiguration configuration)
    {
        ValidatePayload(value, configuration);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= configuration.MaximumAuditPayloadBytes, "PayloadCapacityExceeded");
        return bytes;
    }

    internal static DiagnosticOperationPayload Decode(byte[] bytes, DiagnosticSupportConfiguration configuration)
    {
        Require(bytes.Length is >= 2 && bytes.Length <= configuration.MaximumAuditPayloadBytes, "PayloadSizeInvalid");
        DiagnosticOperationPayload? value;
        try { value = JsonSerializer.Deserialize<DiagnosticOperationPayload>(bytes, Json); }
        catch (JsonException) { throw new InvalidOperationException("DiagnosticSupportPayloadInvalid"); }
        Require(value is not null && bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value!, Json)),
            "PayloadNoncanonical");
        ValidatePayload(value!, configuration);
        return value!;
    }

    internal static string ContentHash(long position, string configurationHash, byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("DiagnosticOperationRecordV1",
            position.ToString(CultureInfo.InvariantCulture), configurationHash, Convert.ToBase64String(payload))));

    internal static byte[] AuditBinding(long position, string configurationHash, byte[] payload) =>
        AuditCanonical.Encode("DiagnosticOperationAuditV1", SystemPrincipalId.Runtime,
            position.ToString(CultureInfo.InvariantCulture), configurationHash,
            ContentHash(position, configurationHash, payload), Convert.ToBase64String(payload));
}
