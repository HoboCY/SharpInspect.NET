using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// A complete, versioned offline password blocklist deployment.
/// </summary>
public sealed record PasswordBlocklist
{
    private const int MaximumEntries = 1_000_000;
    private const int MaximumValueCodeUnits = 4_096;
    internal const long MaximumContentUtf8Bytes = 64L * 1024L * 1024L;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private HashSet<string>? _lookup;

    public PasswordBlocklist(string id, string version, string contentHash, IEnumerable<string> values)
        : this(id, version, contentHash, MaterializeValues(values))
    {
    }

    private PasswordBlocklist(string id, string version, string contentHash, string[] validatedValues)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ContentHash = contentHash ?? throw new ArgumentNullException(nameof(contentHash));
        Values = new ReadOnlyCollection<string>(validatedValues);
        ValidateMaterialized(validatedValues);
    }

    public string Id { get; init; }
    public string Version { get; init; }
    public string ContentHash { get; init; }

    /// <summary>
    /// Complete values supplied by a trusted deployment. They are excluded from default JSON.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Values { get; init; }

    public static PasswordBlocklist Create(string id, string version, IEnumerable<string> values)
    {
        var copy = values is null ? throw new ArgumentNullException(nameof(values)) : MaterializeValues(values);
        return new PasswordBlocklist(id, version, ComputeContentHash(id, version, copy), copy);
    }

    public static string ComputeContentHash(string id, string version, IEnumerable<string> values)
    {
        if (id is null) throw new ArgumentNullException(nameof(id));
        if (version is null) throw new ArgumentNullException(nameof(version));
        if (values is null) throw new ArgumentNullException(nameof(values));

        ValidateIdentifier(id, nameof(id));
        ValidateIdentifier(version, nameof(version));
        return ComputeContentHashValidated(id, version, MaterializeValues(values));
    }

    public void Validate()
    {
        ValidateIdentifier(Id, nameof(Id));
        ValidateIdentifier(Version, nameof(Version));
        if (Values is null)
            throw new ArgumentException("PasswordBlocklistEntriesInvalid", nameof(Values));
        if (ContentHash is null || ContentHash.Length != 64 || !ContentHash.All(IsUpperHex))
            throw new ArgumentException("PasswordBlocklistContentHashInvalid", nameof(ContentHash));

        var validatedValues = MaterializeValues(Values);
        ValidateMaterialized(validatedValues);
    }

    private void ValidateMaterialized(string[] validatedValues)
    {
        _lookup = null;
        var expected = ComputeContentHashValidated(Id, Version, validatedValues);
        if (!string.Equals(ContentHash, expected, StringComparison.Ordinal))
            throw new ArgumentException("PasswordBlocklistContentHashMismatch", nameof(ContentHash));

        _lookup = new HashSet<string>(validatedValues, StringComparer.Ordinal);
    }

    internal bool ContainsExact(string value)
    {
        return (_lookup ??= new HashSet<string>(Values, StringComparer.Ordinal)).Contains(value);
    }

    public override string ToString()
        => $"{nameof(PasswordBlocklist)}(Id={Id},Version={Version},ContentHash={ContentHash},Count={Values.Count})";

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("PasswordBlocklistIdentifierInvalid", parameterName);
        ValidateUnicode(value, parameterName);
    }

    private static void ValidateBlocklistValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumValueCodeUnits)
            throw new ArgumentException("PasswordBlocklistValueInvalid", nameof(Values));
        ValidateUnicode(value, nameof(Values));
        if (!string.Equals(value.Normalize(NormalizationForm.FormC), value, StringComparison.Ordinal))
            throw new ArgumentException("PasswordBlocklistValueMustBeNfc", nameof(Values));
    }

    private static void ValidateUnicode(string value, string parameterName)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("PasswordInvalidUnicode", parameterName);
                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ArgumentException("PasswordInvalidUnicode", parameterName);
            }
        }
    }

    private static bool IsUpperHex(char value)
        => value is >= '0' and <= '9' or >= 'A' and <= 'F';

    private static string[] MaterializeValues(IEnumerable<string> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var materialized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long utf8Bytes = 0;
        foreach (var value in values)
        {
            if (materialized.Count >= MaximumEntries)
                throw new ArgumentException("PasswordBlocklistEntriesInvalid", nameof(values));
            ValidateBlocklistValue(value);
            if (!seen.Add(value))
                throw new ArgumentException("PasswordBlocklistDuplicateValue", nameof(values));

            var valueBytes = StrictUtf8.GetByteCount(value);
            utf8Bytes = checked(utf8Bytes + valueBytes);
            if (utf8Bytes > MaximumContentUtf8Bytes)
                throw new ArgumentException("PasswordBlocklistContentTooLarge", nameof(values));
            materialized.Add(value);
        }
        if (materialized.Count == 0)
            throw new ArgumentException("PasswordBlocklistEntriesInvalid", nameof(values));
        return materialized.ToArray();
    }

    private static string ComputeContentHashValidated(string id, string version, string[] values)
    {
        if (values.Length == 0)
            throw new ArgumentException("PasswordBlocklistEntriesInvalid", nameof(values));
        EnsureContentUtf8Budget(id, version, values);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, id);
        AppendString(hash, version);
        AppendInt32(hash, values.Length);
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
            AppendString(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var encoded = StrictUtf8.GetBytes(value);
        AppendInt32(hash, encoded.Length);
        hash.AppendData(encoded);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void EnsureContentUtf8Budget(string id, string version, IEnumerable<string> values)
    {
        long utf8Bytes = checked(StrictUtf8.GetByteCount(id) + StrictUtf8.GetByteCount(version));
        foreach (var value in values)
        {
            utf8Bytes = checked(utf8Bytes + StrictUtf8.GetByteCount(value));
            if (utf8Bytes > MaximumContentUtf8Bytes)
                throw new ArgumentException("PasswordBlocklistContentTooLarge", nameof(values));
        }
    }
}

/// <summary>
/// Local password creation policy. It is intentionally independent from authentication
/// storage and does not apply composition or periodic-change rules.
/// </summary>
public sealed record LocalPasswordPolicy
{
    public const int MinimumPasswordCodePoints = 15;
    public const int MaximumPasswordCodePoints = 128;
    public const int MaximumRawPasswordCodeUnits = 1_024;
    public const string DevelopmentVersion = "development-2026-09";

    public string Version { get; init; } = DevelopmentVersion;
    public int MinimumCodePoints { get; init; } = MinimumPasswordCodePoints;
    public int MaximumCodePoints { get; init; } = MaximumPasswordCodePoints;
    public int MaximumRawCodeUnits { get; init; } = MaximumRawPasswordCodeUnits;
    public PasswordBlocklist? Blocklist { get; init; }
    private PasswordBlocklist? _validatedBlocklist;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 128)
            throw new ArgumentException("LocalPasswordPolicyVersionInvalid", nameof(Version));
        if (MinimumCodePoints < MinimumPasswordCodePoints ||
            MinimumCodePoints > MaximumPasswordCodePoints)
            throw new ArgumentOutOfRangeException(nameof(MinimumCodePoints), MinimumCodePoints,
                "LocalPasswordPolicyMinimumInvalid");
        if (MaximumCodePoints < MinimumCodePoints || MaximumCodePoints > MaximumPasswordCodePoints)
            throw new ArgumentOutOfRangeException(nameof(MaximumCodePoints), MaximumCodePoints,
                "LocalPasswordPolicyMaximumInvalid");
        if (MaximumRawCodeUnits < MaximumCodePoints || MaximumRawCodeUnits > 16_384)
            throw new ArgumentOutOfRangeException(nameof(MaximumRawCodeUnits), MaximumRawCodeUnits,
                "LocalPasswordPolicyRawLengthInvalid");
        if (Blocklist is null)
            throw new InvalidOperationException("PasswordBlocklistRequired");
        if (!ReferenceEquals(_validatedBlocklist, Blocklist))
        {
            Blocklist.Validate();
            _validatedBlocklist = Blocklist;
        }
    }

    /// <summary>
    /// Applies NFC normalization and then enforces complete-value blocklist checks.
    /// </summary>
    public string NormalizeAndValidate(
        string password,
        string? username = null,
        string? productName = null,
        string? stationId = null)
    {
        Validate();
        var normalized = NormalizeAndValidateShape(password, MinimumCodePoints, MaximumCodePoints, MaximumRawCodeUnits);
        if (Blocklist!.ContainsExact(normalized) || IsContextValue(normalized, username) ||
            IsContextValue(normalized, productName) || IsContextValue(normalized, stationId))
            throw new InvalidOperationException("PasswordBlocklisted");
        return normalized;
    }

    /// <summary>Alias for callers that use validation terminology.</summary>
    public string ValidatePassword(
        string password,
        string? username = null,
        string? productName = null,
        string? stationId = null)
        => NormalizeAndValidate(password, username, productName, stationId);

    internal static string ValidateNormalizedPassword(string normalizedPassword)
        => NormalizeAndValidateShape(normalizedPassword, 1,
            MaximumPasswordCodePoints, MaximumRawPasswordCodeUnits, requireNfc: true);

    private static string NormalizeAndValidateShape(
        string password,
        int minimumCodePoints,
        int maximumCodePoints,
        int maximumRawCodeUnits,
        bool requireNfc = false)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));
        if (password.Length > maximumRawCodeUnits)
            throw new ArgumentException("PasswordRawLengthExceeded", nameof(password));
        ValidateUnicode(password, nameof(password));

        string normalized;
        try
        {
            normalized = password.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("PasswordInvalidUnicode", nameof(password), exception);
        }

        if (requireNfc && !string.Equals(password, normalized, StringComparison.Ordinal))
            throw new ArgumentException("PasswordMustBeNfcNormalized", nameof(password));

        var codePoints = CountCodePoints(normalized);
        if (codePoints < minimumCodePoints || codePoints > maximumCodePoints)
            throw new ArgumentException("PasswordCodePointLengthInvalid", nameof(password));
        return normalized;
    }

    private static bool IsContextValue(string password, string? context)
    {
        if (string.IsNullOrEmpty(context)) return false;
        ValidateUnicode(context, nameof(context));
        var normalizedContext = context.Normalize(NormalizationForm.FormC);
        foreach (var candidate in ContextCandidates(normalizedContext))
            if (string.Equals(password, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> ContextCandidates(string context)
    {
        // These are complete, bounded candidates. No substring or fuzzy matching is used.
        yield return context;
        yield return context + "1";
        yield return context + "123";
        yield return context + "1234";
        yield return context + "!";
        yield return context + "@123";
        yield return context + "2026";
    }

    private static int CountCodePoints(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++, count++)
            if (char.IsHighSurrogate(value[index])) index++;
        return count;
    }

    private static void ValidateUnicode(string value, string parameterName)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("PasswordInvalidUnicode", parameterName);
                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ArgumentException("PasswordInvalidUnicode", parameterName);
            }
        }
    }
}
