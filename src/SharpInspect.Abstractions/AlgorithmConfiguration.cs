using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Abstractions;

public enum AlgorithmScalarType { Boolean, Int64, Float64, String, Enum }

/// <summary>One typed, immutable algorithm configuration scalar.</summary>
public sealed class AlgorithmScalarValue : IEquatable<AlgorithmScalarValue>
{
    internal const int MaximumTextBytes = 4096;
    private readonly object _value;

    private AlgorithmScalarValue(AlgorithmScalarType type, object value)
    {
        Type = type;
        _value = value;
    }

    public AlgorithmScalarType Type { get; }
    public AlgorithmScalarType ScalarType => Type;

    public static AlgorithmScalarValue FromBoolean(bool value) =>
        new(AlgorithmScalarType.Boolean, value);

    public static AlgorithmScalarValue FromInt64(long value) =>
        new(AlgorithmScalarType.Int64, value);

    public static AlgorithmScalarValue FromFloat64(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException("AlgorithmFloat64MustBeFinite", nameof(value));
        return new(AlgorithmScalarType.Float64, NormalizeFloat(value));
    }

    public static AlgorithmScalarValue FromString(string value) =>
        new(AlgorithmScalarType.String, ValidateText(value, nameof(value), MaximumTextBytes));

    public static AlgorithmScalarValue FromEnum(string value) =>
        new(AlgorithmScalarType.Enum, ValidateText(value, nameof(value), MaximumTextBytes));

    public bool AsBoolean() => Type == AlgorithmScalarType.Boolean
        ? (bool)_value : throw TypeMismatch(AlgorithmScalarType.Boolean);

    public long AsInt64() => Type == AlgorithmScalarType.Int64
        ? (long)_value : throw TypeMismatch(AlgorithmScalarType.Int64);

    public double AsFloat64() => Type == AlgorithmScalarType.Float64
        ? (double)_value : throw TypeMismatch(AlgorithmScalarType.Float64);

    /// <summary>Returns the textual payload for a String scalar.</summary>
    public string AsString() => Type == AlgorithmScalarType.String
        ? (string)_value : throw TypeMismatch(AlgorithmScalarType.String);

    public string AsEnum() => Type == AlgorithmScalarType.Enum
        ? (string)_value : throw TypeMismatch(AlgorithmScalarType.Enum);

    public bool Equals(AlgorithmScalarValue? other)
    {
        if (other is null || Type != other.Type) return false;
        return Type switch
        {
            AlgorithmScalarType.Boolean => AsBoolean() == other.AsBoolean(),
            AlgorithmScalarType.Int64 => AsInt64() == other.AsInt64(),
            AlgorithmScalarType.Float64 => BitConverter.DoubleToInt64Bits(AsFloat64()) ==
                BitConverter.DoubleToInt64Bits(other.AsFloat64()),
            AlgorithmScalarType.String =>
                string.Equals(AsString(), other.AsString(), StringComparison.Ordinal),
            AlgorithmScalarType.Enum =>
                string.Equals(AsEnum(), other.AsEnum(), StringComparison.Ordinal),
            _ => false
        };
    }

    public override bool Equals(object? obj) => Equals(obj as AlgorithmScalarValue);

    public override int GetHashCode() => Type switch
    {
        AlgorithmScalarType.Boolean => HashCode.Combine(Type, AsBoolean()),
        AlgorithmScalarType.Int64 => HashCode.Combine(Type, AsInt64()),
        AlgorithmScalarType.Float64 => HashCode.Combine(Type, BitConverter.DoubleToInt64Bits(AsFloat64())),
        _ => HashCode.Combine(Type, StringComparer.Ordinal.GetHashCode((string)_value))
    };

    internal static byte[] StrictUtf8(string value, string parameterName, int maximumBytes)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length > maximumBytes)
            throw new ArgumentException("AlgorithmTextBoundsInvalid", parameterName);
        try
        {
            var encoding = new UTF8Encoding(false, true);
            var byteCount = encoding.GetByteCount(value);
            if (byteCount > maximumBytes)
                throw new ArgumentException("AlgorithmTextBoundsInvalid", parameterName);
            return encoding.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("AlgorithmUtf8Invalid", parameterName, exception);
        }
    }

    internal static string ValidateText(string value, string parameterName, int maximumBytes) {
        _ = StrictUtf8(value, parameterName, maximumBytes);
        return value;
    }

    /// <summary>Canonical configuration bytes normalize both signed zero values to +0.</summary>
    internal static double NormalizeFloat(double value) => value == 0d ? 0d : value;

    private InvalidOperationException TypeMismatch(AlgorithmScalarType expected) =>
        new("AlgorithmScalarTypeMismatch");
}

/// <summary>Optional, type-specific scalar constraints for one schema field.</summary>
public sealed class AlgorithmScalarConstraints
{
    private readonly ReadOnlyCollection<string>? _allowedValues;

    public AlgorithmScalarConstraints(long? minInt64 = null, long? maxInt64 = null,
        double? minFloat64 = null, double? maxFloat64 = null, int? minLength = null,
        int? maxLength = null, IEnumerable<string>? allowedValues = null)
    {
        if (minInt64 is { } minInteger && maxInt64 is { } maxInteger && minInteger > maxInteger)
            throw new ArgumentException("AlgorithmInt64BoundsInvalid", nameof(maxInt64));
        if (minFloat64 is { } minFloat && (!double.IsFinite(minFloat) ||
            maxFloat64 is { } maxFloatForMin && (!double.IsFinite(maxFloatForMin) || minFloat > maxFloatForMin)))
            throw new ArgumentException("AlgorithmFloat64BoundsInvalid", nameof(minFloat64));
        if (maxFloat64 is { } maxFloat && !double.IsFinite(maxFloat))
            throw new ArgumentException("AlgorithmFloat64BoundsInvalid", nameof(maxFloat64));
        if (minLength is < 0 or > AlgorithmScalarValue.MaximumTextBytes)
            throw new ArgumentOutOfRangeException(nameof(minLength));
        if (maxLength is < 0 or > AlgorithmScalarValue.MaximumTextBytes)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (minLength is { } minText && maxLength is { } maxText && minText > maxText)
            throw new ArgumentException("AlgorithmLengthBoundsInvalid", nameof(maxLength));

        MinInt64 = minInt64;
        MaxInt64 = maxInt64;
        MinFloat64 = minFloat64;
        MaxFloat64 = maxFloat64;
        MinLength = minLength;
        MaxLength = maxLength;
        if (allowedValues is not null)
        {
            var copied = AlgorithmConfigurationValidation.CopyBounded(allowedValues, 256,
                "AlgorithmAllowedValuesCapacityExceeded", nameof(allowedValues));
            foreach (var value in copied)
                AlgorithmScalarValue.StrictUtf8(value, nameof(allowedValues), AlgorithmScalarValue.MaximumTextBytes);
            if (copied.Distinct(StringComparer.Ordinal).Count() != copied.Length)
                throw new ArgumentException("AlgorithmAllowedValuesDuplicate", nameof(allowedValues));
            _allowedValues = new ReadOnlyCollection<string>(copied);
        }
    }

    public long? MinInt64 { get; }
    public long? MaxInt64 { get; }
    public double? MinFloat64 { get; }
    public double? MaxFloat64 { get; }
    public int? MinLength { get; }
    public int? MaxLength { get; }
    public IReadOnlyList<string>? AllowedValues => _allowedValues;

    internal bool IsEmpty => MinInt64 is null && MaxInt64 is null && MinFloat64 is null &&
        MaxFloat64 is null && MinLength is null && MaxLength is null &&
        _allowedValues is null;

    internal void ValidateCompatibility(AlgorithmScalarType type, string parameterName)
    {
        var numericInteger = MinInt64 is not null || MaxInt64 is not null;
        var numericFloat = MinFloat64 is not null || MaxFloat64 is not null;
        var textLength = MinLength is not null || MaxLength is not null;
        var allowed = _allowedValues is not null;
        var compatible = type switch
        {
            AlgorithmScalarType.Boolean => !numericInteger && !numericFloat && !textLength && !allowed,
            AlgorithmScalarType.Int64 => !numericFloat && !textLength && !allowed,
            AlgorithmScalarType.Float64 => !numericInteger && !textLength && !allowed,
            AlgorithmScalarType.String or AlgorithmScalarType.Enum => !numericInteger && !numericFloat,
            _ => false
        };
        if (!compatible) throw new ArgumentException("AlgorithmConstraintTypeMismatch", parameterName);
    }

    internal bool TryValidate(AlgorithmScalarValue value, AlgorithmScalarType expectedType,
        out string reasonCode)
    {
        reasonCode = string.Empty;
        if (value.Type != expectedType)
        {
            reasonCode = "AlgorithmFieldTypeMismatch";
            return false;
        }

        switch (expectedType)
        {
            case AlgorithmScalarType.Int64:
                var integer = value.AsInt64();
                if (MinInt64 is { } minInteger && integer < minInteger ||
                    MaxInt64 is { } maxInteger && integer > maxInteger)
                    reasonCode = "AlgorithmFieldConstraintViolation";
                break;
            case AlgorithmScalarType.Float64:
                var floating = value.AsFloat64();
                if (MinFloat64 is { } minFloat && floating < minFloat ||
                    MaxFloat64 is { } maxFloat && floating > maxFloat)
                    reasonCode = "AlgorithmFieldConstraintViolation";
                break;
            case AlgorithmScalarType.String:
            case AlgorithmScalarType.Enum:
                var text = expectedType == AlgorithmScalarType.String
                    ? value.AsString()
                    : value.AsEnum();
                if (MinLength is { } minLength && text.Length < minLength ||
                    MaxLength is { } maxLength && text.Length > maxLength ||
                    _allowedValues is not null && !_allowedValues.Contains(text, StringComparer.Ordinal))
                    reasonCode = "AlgorithmFieldConstraintViolation";
                break;
        }

        return reasonCode.Length == 0;
    }
}

/// <summary>One immutable field declaration in an algorithm configuration schema.</summary>
public sealed class AlgorithmFieldDefinition
{
    public AlgorithmFieldDefinition(string key, AlgorithmScalarType type, string unit, bool required,
        AlgorithmScalarConstraints? constraints = null, AlgorithmScalarValue? authoringDefault = null,
        string? helpText = null)
    {
        Key = AlgorithmConfigurationValidation.Identifier(key, nameof(key));
        Type = AlgorithmConfigurationValidation.Enum(type, nameof(type));
        Unit = AlgorithmConfigurationValidation.Identifier(unit, nameof(unit));
        Required = required;
        constraints?.ValidateCompatibility(Type, nameof(constraints));
        Constraints = constraints is null || constraints.IsEmpty ? null : constraints;
        if (authoringDefault is not null)
        {
            if (authoringDefault.Type != Type)
                throw new ArgumentException("AlgorithmFieldDefaultTypeMismatch", nameof(authoringDefault));
            if (Constraints is not null && !Constraints.TryValidate(authoringDefault, Type, out _))
                throw new ArgumentException("AlgorithmFieldDefaultConstraintViolation", nameof(authoringDefault));
        }
        AuthoringDefault = authoringDefault;
        HelpText = string.IsNullOrEmpty(helpText) ? null :
            AlgorithmConfigurationValidation.Text(helpText, nameof(helpText), 4096);
    }

    public string Key { get; }
    public AlgorithmScalarType Type { get; }
    public string Unit { get; }
    public bool Required { get; }
    public AlgorithmScalarConstraints? Constraints { get; }
    public AlgorithmScalarValue? AuthoringDefault { get; }
    /// <summary>Optional literal authoring help, bound by the configuration schema hash.</summary>
    public string? HelpText { get; }
}

/// <summary>Immutable versioned schema for algorithm configuration values.</summary>
public sealed class AlgorithmConfigurationSchema
{
    public const string CanonicalizationVersion = "sharpinspect-config-v1";
    private readonly ReadOnlyCollection<AlgorithmFieldDefinition> _fields;

    public AlgorithmConfigurationSchema(string id, string version,
        IEnumerable<AlgorithmFieldDefinition> fields)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        ArgumentNullException.ThrowIfNull(fields);
        var copied = AlgorithmConfigurationValidation.CopyBounded(fields, 256,
            "AlgorithmSchemaFieldCapacityInvalid", nameof(fields));
        if (copied.Any(field => field is null))
            throw new ArgumentException("AlgorithmFieldRequired", nameof(fields));
        if (copied.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException("AlgorithmSchemaDuplicateField", nameof(fields));
        _fields = new ReadOnlyCollection<AlgorithmFieldDefinition>(copied);
        ContentHash = AlgorithmConfigurationCanonical.HashSchema(this);
    }

    public string Id { get; }
    public string Version { get; }
    public IReadOnlyList<AlgorithmFieldDefinition> Fields => _fields;
    public string ContentHash { get; }
}

public sealed record AlgorithmConfigurationEntry
{
    public AlgorithmConfigurationEntry(string key, string unit, AlgorithmScalarValue value)
    {
        Key = AlgorithmConfigurationValidation.Identifier(key, nameof(key));
        Unit = AlgorithmConfigurationValidation.Identifier(unit, nameof(unit));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }
    public string Unit { get; }
    public AlgorithmScalarValue Value { get; }
}

public sealed record AlgorithmValidationIssue(string Code, string? FieldKey = null);

/// <summary>Immutable, schema-bound configuration values for a released Recipe.</summary>
public sealed class AlgorithmConfigurationSnapshot
{
    private readonly ReadOnlyCollection<AlgorithmConfigurationEntry> _values;

    public AlgorithmConfigurationSnapshot(string schemaId, string schemaVersion,
        string schemaContentHash, string canonicalizationVersion, string contentHash,
        IEnumerable<AlgorithmConfigurationEntry> values)
    {
        SchemaId = AlgorithmConfigurationValidation.Identifier(schemaId, nameof(schemaId));
        SchemaVersion = AlgorithmConfigurationValidation.Identifier(schemaVersion, nameof(schemaVersion));
        SchemaContentHash = AlgorithmConfigurationValidation.Hash(schemaContentHash, nameof(schemaContentHash));
        CanonicalizationVersion = AlgorithmConfigurationValidation.Text(canonicalizationVersion,
            nameof(canonicalizationVersion), 64);
        if (CanonicalizationVersion.Length == 0)
            throw new ArgumentException("AlgorithmCanonicalizationVersionInvalid", nameof(canonicalizationVersion));
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash));
        ArgumentNullException.ThrowIfNull(values);
        var copied = AlgorithmConfigurationValidation.CopyBounded(values, 256,
            "AlgorithmSnapshotValueCapacityExceeded", nameof(values));
        if (copied.Any(value => value is null))
            throw new ArgumentException("AlgorithmConfigurationEntryRequired", nameof(values));
        _values = new ReadOnlyCollection<AlgorithmConfigurationEntry>(copied);
    }

    public string SchemaId { get; }
    public string SchemaVersion { get; }
    public string SchemaContentHash { get; }
    public string CanonicalizationVersion { get; }
    public string ContentHash { get; }
    public IReadOnlyList<AlgorithmConfigurationEntry> Values => _values;

    public static AlgorithmConfigurationSnapshot Create(AlgorithmConfigurationSchema schema,
        IEnumerable<AlgorithmConfigurationEntry> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        var copied = AlgorithmConfigurationValidation.CopyBounded(values, 256,
            "AlgorithmSnapshotValueCapacityExceeded", nameof(values));
        if (copied.Any(value => value is null))
            throw new ArgumentException("AlgorithmConfigurationEntryRequired", nameof(values));
        var issues = ValidateEntries(schema, copied);
        if (issues.Count != 0)
            throw new ArgumentException(issues[0].Code, nameof(values));
        var contentHash = AlgorithmConfigurationCanonical.HashSnapshot(schema.Id, schema.Version,
            schema.ContentHash, AlgorithmConfigurationSchema.CanonicalizationVersion, copied);
        return new AlgorithmConfigurationSnapshot(schema.Id, schema.Version, schema.ContentHash,
            AlgorithmConfigurationSchema.CanonicalizationVersion, contentHash, copied);
    }

    public IReadOnlyList<AlgorithmValidationIssue> Validate(AlgorithmConfigurationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var issues = new List<AlgorithmValidationIssue>();
        if (!string.Equals(SchemaId, schema.Id, StringComparison.Ordinal))
            issues.Add(new("AlgorithmSchemaIdMismatch"));
        if (!string.Equals(SchemaVersion, schema.Version, StringComparison.Ordinal))
            issues.Add(new("AlgorithmSchemaVersionMismatch"));
        if (!string.Equals(SchemaContentHash, schema.ContentHash, StringComparison.Ordinal))
            issues.Add(new("AlgorithmSchemaContentHashMismatch"));
        if (!string.Equals(CanonicalizationVersion, CanonicalizationVersionValue, StringComparison.Ordinal))
            issues.Add(new("AlgorithmCanonicalizationVersionMismatch"));

        issues.AddRange(ValidateEntries(schema, _values));
        var expectedHash = AlgorithmConfigurationCanonical.HashSnapshot(SchemaId, SchemaVersion,
            SchemaContentHash, CanonicalizationVersion, _values);
        if (!string.Equals(ContentHash, expectedHash, StringComparison.Ordinal))
            issues.Add(new("AlgorithmContentHashMismatch"));
        return new ReadOnlyCollection<AlgorithmValidationIssue>(issues);
    }

    private static string CanonicalizationVersionValue => AlgorithmConfigurationSchema.CanonicalizationVersion;

    private static IReadOnlyList<AlgorithmValidationIssue> ValidateEntries(
        AlgorithmConfigurationSchema schema, IEnumerable<AlgorithmConfigurationEntry> values)
    {
        var issues = new List<AlgorithmValidationIssue>();
        var definitions = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            if (!seen.Add(entry.Key))
                issues.Add(new("AlgorithmFieldDuplicate", entry.Key));
            if (!definitions.TryGetValue(entry.Key, out var definition))
            {
                issues.Add(new("AlgorithmFieldUnknown", entry.Key));
                continue;
            }
            if (!string.Equals(entry.Unit, definition.Unit, StringComparison.Ordinal))
                issues.Add(new("AlgorithmFieldUnitMismatch", entry.Key));
            if (entry.Value.Type != definition.Type)
            {
                issues.Add(new("AlgorithmFieldTypeMismatch", entry.Key));
                continue;
            }
            if (definition.Constraints is not null &&
                !definition.Constraints.TryValidate(entry.Value, definition.Type, out var reasonCode))
                issues.Add(new(reasonCode, entry.Key));
        }

        foreach (var definition in schema.Fields)
        {
            if (definition.Required && !seen.Contains(definition.Key))
                issues.Add(new("AlgorithmFieldMissingRequired", definition.Key));
        }
        return issues;
    }
}

internal static class AlgorithmConfigurationValidation
{
    internal static T[] CopyBounded<T>(IEnumerable<T> values, int maximum,
        string capacityCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new List<T>(Math.Min(maximum, 16));
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copied.Count == maximum)
                throw new ArgumentException(capacityCode, parameterName);
            copied.Add(enumerator.Current);
        }
        return copied.ToArray();
    }

    internal static string Identifier(string value, string parameterName)
    {
        var result = Text(value, parameterName, 64);
        if (result.Length == 0)
            throw new ArgumentException("AlgorithmIdentifierInvalid", parameterName);
        if (result.Any(character =>
            !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("AlgorithmIdentifierInvalid", parameterName);
        return result;
    }

    internal static string Text(string value, string parameterName, int maximumBytes)
    {
        _ = AlgorithmScalarValue.StrictUtf8(value, parameterName, maximumBytes);
        return value;
    }

    internal static string Hash(string value, string parameterName)
    {
        var result = Text(value, parameterName, 64);
        if (result.Length != 64 || result.Any(character =>
            !(character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')))
            throw new ArgumentException("AlgorithmSha256Invalid", parameterName);
        return result;
    }

    internal static T Enum<T>(T value, string parameterName) where T : struct, System.Enum
    {
        if (!System.Enum.IsDefined(typeof(T), value))
            throw new ArgumentException("AlgorithmEnumInvalid", parameterName);
        return value;
    }
}

internal static class AlgorithmConfigurationCanonical
{
    internal static string HashSchema(AlgorithmConfigurationSchema schema)
    {
        using var writer = new CanonicalWriter();
        writer.String("sharpinspect-algorithm-schema-v1");
        writer.String(AlgorithmConfigurationSchema.CanonicalizationVersion);
        writer.String(schema.Id);
        writer.String(schema.Version);
        writer.Int32(schema.Fields.Count);
        foreach (var field in schema.Fields.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.String(field.Key);
            writer.Byte((byte)field.Type);
            writer.String(field.Unit);
            writer.Bool(field.Required);
            writer.Constraints(field.Constraints);
            writer.Scalar(field.AuthoringDefault);
        }
        // Preserve historical hashes when no authoring-help extension was declared.
        var helpFields = schema.Fields.Where(field => field.HelpText is not null)
            .OrderBy(field => field.Key, StringComparer.Ordinal).ToArray();
        if (helpFields.Length != 0)
        {
            writer.String("sharpinspect-configuration-authoring-help-v1");
            writer.Int32(helpFields.Length);
            foreach (var field in helpFields) { writer.String(field.Key); writer.String(field.HelpText!); }
        }
        return Convert.ToHexString(SHA256.HashData(writer.ToArray()));
    }

    internal static string HashSnapshot(string schemaId, string schemaVersion, string schemaHash,
        string canonicalizationVersion, IEnumerable<AlgorithmConfigurationEntry> values)
    {
        using var writer = new CanonicalWriter();
        writer.String("sharpinspect-algorithm-configuration-v1");
        writer.String(canonicalizationVersion);
        writer.String(schemaId);
        writer.String(schemaVersion);
        writer.String(schemaHash);
        var entries = values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Unit, StringComparer.Ordinal)
            .ThenBy(item => (int)item.Value.Type)
            .ThenBy(item => ValueSortText(item.Value), StringComparer.Ordinal)
            .ToArray();
        writer.Int32(entries.Length);
        foreach (var entry in entries)
        {
            writer.String(entry.Key);
            writer.String(entry.Unit);
            writer.Scalar(entry.Value);
        }
        return Convert.ToHexString(SHA256.HashData(writer.ToArray()));
    }

    private static string ValueSortText(AlgorithmScalarValue value) => value.Type switch
    {
        AlgorithmScalarType.Boolean => value.AsBoolean() ? "1" : "0",
        AlgorithmScalarType.Int64 => value.AsInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
        AlgorithmScalarType.Float64 => BitConverter.DoubleToInt64Bits(value.AsFloat64())
            .ToString(System.Globalization.CultureInfo.InvariantCulture),
        AlgorithmScalarType.String => value.AsString(),
        AlgorithmScalarType.Enum => value.AsEnum(),
        _ => string.Empty
    };

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void Dispose() => _stream.Dispose();
        public byte[] ToArray() => _stream.ToArray();
        public void Byte(byte value) => _stream.WriteByte(value);
        public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

        public void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void String(string value)
        {
            var bytes = AlgorithmScalarValue.StrictUtf8(value, nameof(value), AlgorithmScalarValue.MaximumTextBytes);
            Int32(bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void OptionalInt64(long? value)
        {
            Byte(value.HasValue ? (byte)1 : (byte)0);
            if (value.HasValue) Int64(value.Value);
        }

        public void OptionalInt32(int? value)
        {
            Byte(value.HasValue ? (byte)1 : (byte)0);
            if (value.HasValue) Int32(value.Value);
        }

        public void OptionalDouble(double? value)
        {
            Byte(value.HasValue ? (byte)1 : (byte)0);
            if (value.HasValue) Int64(BitConverter.DoubleToInt64Bits(AlgorithmScalarValue.NormalizeFloat(value.Value)));
        }

        public void Constraints(AlgorithmScalarConstraints? constraints)
        {
            Byte(constraints is null ? (byte)0 : (byte)1);
            if (constraints is null) return;
            OptionalInt64(constraints.MinInt64);
            OptionalInt64(constraints.MaxInt64);
            OptionalDouble(constraints.MinFloat64);
            OptionalDouble(constraints.MaxFloat64);
            OptionalInt32(constraints.MinLength);
            OptionalInt32(constraints.MaxLength);
            if (constraints.AllowedValues is null)
            {
                Int32(-1);
            }
            else
            {
                var values = constraints.AllowedValues.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                Int32(values.Length);
                foreach (var value in values) String(value);
            }
        }

        public void Scalar(AlgorithmScalarValue? value)
        {
            Byte(value is null ? (byte)0 : (byte)1);
            if (value is null) return;
            Byte((byte)((int)value.Type + 1));
            switch (value.Type)
            {
                case AlgorithmScalarType.Boolean:
                    Bool(value.AsBoolean());
                    break;
                case AlgorithmScalarType.Int64:
                    Int64(value.AsInt64());
                    break;
                case AlgorithmScalarType.Float64:
                    Int64(BitConverter.DoubleToInt64Bits(AlgorithmScalarValue.NormalizeFloat(value.AsFloat64())));
                    break;
                case AlgorithmScalarType.String:
                    String(value.AsString());
                    break;
                case AlgorithmScalarType.Enum:
                    String(value.AsEnum());
                    break;
                default:
                    throw new InvalidOperationException("AlgorithmScalarTypeInvalid");
            }
        }
    }
}
