using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace SharpInspect.Abstractions;

/// <summary>The five framework-owned fields which every PLC result contract maps exactly once.</summary>
public enum PlcFrameworkResultField : byte
{
    ControllerEpoch = 1,
    ResultSequence = 2,
    ExecutionStatus = 3,
    InspectionDecision = 4,
    ResultReasonCode = 5
}

/// <summary>Exact PLC representation. Values occupy one or more 16-bit registers.</summary>
public enum PlcWireRepresentation : byte
{
    UInt16 = 1,
    Int16 = 2,
    UInt32 = 3,
    Int32 = 4,
    UInt64 = 5,
    Int64 = 6,
    Ieee754Binary32 = 7,
    Ieee754Binary64 = 8
}

/// <summary>Byte order inside each 16-bit register image.</summary>
public enum PlcByteOrder : byte
{
    BigEndian = 1,
    LittleEndian = 2
}

/// <summary>Order of 16-bit registers in a multi-register value.</summary>
public enum PlcWordOrder : byte
{
    NotApplicable = 0,
    HighWordFirst = 1,
    LowWordFirst = 2
}

/// <summary>Exact rounding policy for numeric wire conversion.</summary>
public enum PlcRoundingMode : byte
{
    Exact = 0,
    TowardZero = 1,
    TowardPositiveInfinity = 2,
    TowardNegativeInfinity = 3,
    AwayFromZero = 4,
    ToNearestTiesToEven = 5,
    ToNearestTiesAwayFromZero = 6
}

/// <summary>The only permitted overflow behavior: reject the value as an encoding fault.</summary>
public enum PlcOverflowBehavior : byte
{
    EncodingFault = 1
}

/// <summary>Whether a schema measurement is represented or explicitly omitted.</summary>
public enum PlcMeasurementDisposition : byte
{
    Mapped = 1,
    Excluded = 2
}

/// <summary>How an optional measurement communicates absent data.</summary>
public enum PlcOptionalAbsenceKind : byte
{
    Sentinel = 1,
    ValidityField = 2
}

/// <summary>A normalized signed rational used by an affine conversion.</summary>
public sealed class PlcRational : IEquatable<PlcRational>
{
    public PlcRational(long numerator, long denominator = 1)
    {
        if (denominator == 0)
            throw new ArgumentException("PlcRationalDenominatorZero", nameof(denominator));

        var n = new BigInteger(numerator);
        var d = new BigInteger(denominator);
        if (d.Sign < 0)
        {
            n = -n;
            d = -d;
        }

        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(n), d);
        n /= gcd;
        d /= gcd;
        if (n < long.MinValue || n > long.MaxValue || d < 1 || d > long.MaxValue)
            throw new ArgumentException("PlcRationalNormalizationOverflow", nameof(denominator));

        Numerator = (long)n;
        Denominator = (long)d;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-rational-v1",
            Numerator.ToString(CultureInfo.InvariantCulture),
            Denominator.ToString(CultureInfo.InvariantCulture)
        });
    }

    public long Numerator { get; }
    public long Denominator { get; }
    public bool IsZero => Numerator == 0;
    public string ContentHash { get; }

    public bool Equals(PlcRational? other) => other is not null &&
        Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => Equals(obj as PlcRational);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
}

/// <summary>An exact source-unit to wire-unit affine conversion.</summary>
public sealed class PlcAffineConversion
{
    public PlcAffineConversion(string sourceUnit, string wireUnit, PlcRational multiplier,
        PlcRational offset)
    {
        SourceUnit = AlgorithmConfigurationValidation.Identifier(sourceUnit, nameof(sourceUnit));
        WireUnit = AlgorithmConfigurationValidation.Identifier(wireUnit, nameof(wireUnit));
        Multiplier = multiplier ?? throw new ArgumentNullException(nameof(multiplier));
        Offset = offset ?? throw new ArgumentNullException(nameof(offset));
        if (Multiplier.IsZero)
            throw new ArgumentException("PlcAffineMultiplierZero", nameof(multiplier));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-affine-conversion-v1", SourceUnit, WireUnit,
            Multiplier.ContentHash, Offset.ContentHash
        });
    }

    public string SourceUnit { get; }
    public string WireUnit { get; }
    public PlcRational Multiplier { get; }
    public PlcRational Offset { get; }
    public string ContentHash { get; }
}

/// <summary>A bounded contiguous range of 16-bit PLC registers.</summary>
public sealed class PlcRegisterRange
{
    public const int MaximumRegisterAddress = ushort.MaxValue;
    public const int MaximumRegisterCount = ushort.MaxValue + 1;

    public PlcRegisterRange(int startRegister, int registerCount)
    {
        if (startRegister < 0 || startRegister > MaximumRegisterAddress)
            throw new ArgumentOutOfRangeException(nameof(startRegister));
        if (registerCount < 1 || registerCount > MaximumRegisterCount - startRegister)
            throw new ArgumentOutOfRangeException(nameof(registerCount));
        StartRegister = startRegister;
        RegisterCount = registerCount;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-register-range-v1",
            StartRegister.ToString(CultureInfo.InvariantCulture),
            RegisterCount.ToString(CultureInfo.InvariantCulture)
        });
    }

    public int StartRegister { get; }
    public int RegisterCount { get; }
    public int EndRegisterExclusive => checked(StartRegister + RegisterCount);
    public string ContentHash { get; }
}

/// <summary>Complete byte/word order and rounding policy for one wire field.</summary>
public sealed class PlcWireEncoding
{
    public PlcWireEncoding(PlcWireRepresentation representation, PlcByteOrder byteOrder,
        PlcWordOrder wordOrder, PlcRoundingMode rounding, PlcOverflowBehavior overflow)
    {
        Representation = AlgorithmConfigurationValidation.Enum(representation, nameof(representation));
        ByteOrder = AlgorithmConfigurationValidation.Enum(byteOrder, nameof(byteOrder));
        WordOrder = AlgorithmConfigurationValidation.Enum(wordOrder, nameof(wordOrder));
        Rounding = AlgorithmConfigurationValidation.Enum(rounding, nameof(rounding));
        Overflow = AlgorithmConfigurationValidation.Enum(overflow, nameof(overflow));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-wire-encoding-v1", Representation.ToString(), ByteOrder.ToString(),
            WordOrder.ToString(), Rounding.ToString(), Overflow.ToString()
        });
    }

    public PlcWireRepresentation Representation { get; }
    public PlcByteOrder ByteOrder { get; }
    public PlcWordOrder WordOrder { get; }
    public PlcRoundingMode Rounding { get; }
    public PlcOverflowBehavior Overflow { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable, bounded wire bytes used for sentinels, validity values and constants.</summary>
public sealed class PlcWireLiteral : IEquatable<PlcWireLiteral>
{
    public const int MaximumBytes = 64;
    private readonly byte[] _bytes;
    private readonly ReadOnlyCollection<byte> _readOnlyBytes;

    public PlcWireLiteral(IEnumerable<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var copied = AlgorithmContractValidation.Copy(bytes, nameof(bytes), MaximumBytes);
        if (copied.Count is < 1 or > MaximumBytes)
            throw new ArgumentException("PlcWireLiteralCapacityInvalid", nameof(bytes));
        _bytes = copied.ToArray();
        _readOnlyBytes = new ReadOnlyCollection<byte>(_bytes);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-wire-literal-v1",
            _bytes.Length.ToString(CultureInfo.InvariantCulture), Convert.ToHexString(_bytes)
        });
    }

    public IReadOnlyList<byte> Bytes => _readOnlyBytes;
    /// <summary>Returns a copy so callers cannot mutate the hashed backing bytes.</summary>
    public ReadOnlyMemory<byte> Memory => new(_bytes.ToArray());
    public int Length => _bytes.Length;
    public string ContentHash { get; }
    public byte[] ToArray() => _bytes.ToArray();

    public bool Equals(PlcWireLiteral? other) => other is not null && _bytes.SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => Equals(obj as PlcWireLiteral);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ContentHash);

}

/// <summary>Strongly typed scalar code-table entry for Boolean, String or Enum values.</summary>
public sealed class PlcScalarCode : IEquatable<PlcScalarCode>
{
    public PlcScalarCode(AlgorithmScalarValue value, long wireCode)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        if (Value.Type is not (AlgorithmScalarType.Boolean or AlgorithmScalarType.String or AlgorithmScalarType.Enum))
            throw new ArgumentException("PlcScalarCodeTypeInvalid", nameof(value));
        LongWireCode = wireCode;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-scalar-code-v1", ScalarTypeTag(value), ScalarValueTag(value),
            LongWireCode.ToString(CultureInfo.InvariantCulture)
        });
    }

    public AlgorithmScalarValue Value { get; }
    public long LongWireCode { get; }
    public long WireCode => LongWireCode;
    public string ContentHash { get; }

    public bool Equals(PlcScalarCode? other) => other is not null && Value.Equals(other.Value) &&
        LongWireCode == other.LongWireCode;
    public override bool Equals(object? obj) => Equals(obj as PlcScalarCode);
    public override int GetHashCode() => HashCode.Combine(Value, LongWireCode);

    internal static string ScalarTypeTag(AlgorithmScalarValue value) => value.Type.ToString();
    internal static string ScalarValueTag(AlgorithmScalarValue value) => value.Type switch
    {
        AlgorithmScalarType.Boolean => value.AsBoolean() ? "1" : "0",
        AlgorithmScalarType.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
        AlgorithmScalarType.Float64 => BitConverter.DoubleToInt64Bits(value.AsFloat64())
            .ToString("X16", CultureInfo.InvariantCulture),
        AlgorithmScalarType.String => value.AsString(),
        AlgorithmScalarType.Enum => value.AsEnum(),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

/// <summary>Strongly typed code-table entry for framework execution status.</summary>
public sealed class PlcExecutionStatusCode : IEquatable<PlcExecutionStatusCode>
{
    public PlcExecutionStatusCode(ExecutionStatus value, long wireCode)
    {
        if (!Enum.IsDefined(typeof(ExecutionStatus), value))
            throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
        LongWireCode = wireCode;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-execution-status-code-v1", Value.ToString(),
            LongWireCode.ToString(CultureInfo.InvariantCulture)
        });
    }

    public ExecutionStatus Value { get; }
    public long LongWireCode { get; }
    public long WireCode => LongWireCode;
    public string ContentHash { get; }
    public bool Equals(PlcExecutionStatusCode? other) => other is not null &&
        Value == other.Value && LongWireCode == other.LongWireCode;
    public override bool Equals(object? obj) => Equals(obj as PlcExecutionStatusCode);
    public override int GetHashCode() => HashCode.Combine(Value, LongWireCode);
}

/// <summary>Strongly typed code-table entry for framework inspection decision.</summary>
public sealed class PlcInspectionDecisionCode : IEquatable<PlcInspectionDecisionCode>
{
    public PlcInspectionDecisionCode(InspectionDecision value, long wireCode)
    {
        if (!Enum.IsDefined(typeof(InspectionDecision), value))
            throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
        LongWireCode = wireCode;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-inspection-decision-code-v1", Value.ToString(),
            LongWireCode.ToString(CultureInfo.InvariantCulture)
        });
    }

    public InspectionDecision Value { get; }
    public long LongWireCode { get; }
    public long WireCode => LongWireCode;
    public string ContentHash { get; }
    public bool Equals(PlcInspectionDecisionCode? other) => other is not null &&
        Value == other.Value && LongWireCode == other.LongWireCode;
    public override bool Equals(object? obj) => Equals(obj as PlcInspectionDecisionCode);
    public override int GetHashCode() => HashCode.Combine(Value, LongWireCode);
}

/// <summary>Strongly typed framework/schema reason code. Null is a distinct catalog key.</summary>
public sealed class PlcReasonCode : IEquatable<PlcReasonCode>
{
    public PlcReasonCode(string? reasonCode, long wireCode)
    {
        ReasonCode = reasonCode is null ? null :
            AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        LongWireCode = wireCode;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-reason-code-v1", ReasonCode is null ? "<null>" : "<value>", ReasonCode,
            LongWireCode.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string? ReasonCode { get; }
    public string? Value => ReasonCode;
    public string? Code => ReasonCode;
    public bool IsNull => ReasonCode is null;
    public long LongWireCode { get; }
    public long WireCode => LongWireCode;
    public string ContentHash { get; }
    public bool Equals(PlcReasonCode? other) => other is not null &&
        string.Equals(ReasonCode, other.ReasonCode, StringComparison.Ordinal) &&
        LongWireCode == other.LongWireCode;
    public override bool Equals(object? obj) => Equals(obj as PlcReasonCode);
    public override int GetHashCode() => HashCode.Combine(ReasonCode, LongWireCode);
}

/// <summary>Independent validity field address and wire representation for optional data.</summary>
public sealed class PlcValidityField
{
    public PlcValidityField(string fieldKey, PlcRegisterRange registerRange, PlcWireEncoding encoding,
        PlcWireLiteral presentData, PlcWireLiteral absentData)
    {
        FieldKey = AlgorithmConfigurationValidation.Identifier(fieldKey, nameof(fieldKey));
        RegisterRange = registerRange ?? throw new ArgumentNullException(nameof(registerRange));
        Encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        PresentData = presentData ?? throw new ArgumentNullException(nameof(presentData));
        AbsentData = absentData ?? throw new ArgumentNullException(nameof(absentData));
        if (PresentData.Equals(AbsentData))
            throw new ArgumentException("PlcValidityPresentAndAbsentDataMustDiffer", nameof(absentData));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-validity-field-v1", FieldKey, RegisterRange.ContentHash, Encoding.ContentHash,
            PresentData.ContentHash, AbsentData.ContentHash
        });
    }

    public string FieldKey { get; }
    public PlcRegisterRange RegisterRange { get; }
    public PlcRegisterRange Address => RegisterRange;
    public PlcWireEncoding Encoding { get; }
    public PlcWireLiteral PresentData { get; }
    public PlcWireLiteral AbsentData { get; }
    public PlcWireLiteral PresentValue => PresentData;
    public PlcWireLiteral AbsentValue => AbsentData;
    public string ContentHash { get; }
}

/// <summary>Sentinel or independently addressed validity declaration for an optional measurement.</summary>
public sealed class PlcOptionalAbsence
{
    public PlcOptionalAbsence(PlcWireLiteral sentinel)
    {
        Sentinel = sentinel ?? throw new ArgumentNullException(nameof(sentinel));
        Kind = PlcOptionalAbsenceKind.Sentinel;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-optional-absence-v1", Kind.ToString(), Sentinel.ContentHash });
    }

    public PlcOptionalAbsence(PlcValidityField validityField, PlcWireLiteral absentData)
    {
        ValidityField = validityField ?? throw new ArgumentNullException(nameof(validityField));
        AbsentData = absentData ?? throw new ArgumentNullException(nameof(absentData));
        Kind = PlcOptionalAbsenceKind.ValidityField;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-optional-absence-v1", Kind.ToString(), ValidityField.ContentHash,
            AbsentData.ContentHash });
    }

    public PlcOptionalAbsenceKind Kind { get; }
    public PlcWireLiteral? Sentinel { get; }
    public PlcValidityField? ValidityField { get; }
    public PlcWireLiteral? AbsentData { get; }
    public static PlcOptionalAbsence ForSentinel(PlcWireLiteral sentinel) => new(sentinel);
    public static PlcOptionalAbsence ForValidity(PlcValidityField field, PlcWireLiteral absentData) =>
        new(field, absentData);
    public string ContentHash { get; }
}

/// <summary>Address and field-specific strongly typed code tables for one framework field.</summary>
public sealed class PlcFrameworkFieldMapping
{
    private readonly ReadOnlyCollection<PlcExecutionStatusCode> _executionStatusCodes;
    private readonly ReadOnlyCollection<PlcInspectionDecisionCode> _inspectionDecisionCodes;
    private readonly ReadOnlyCollection<PlcReasonCode> _reasonCodes;

    public PlcFrameworkFieldMapping(PlcFrameworkResultField field, PlcRegisterRange registerRange,
        PlcWireEncoding encoding, IEnumerable<PlcExecutionStatusCode>? executionStatusCodes = null,
        IEnumerable<PlcInspectionDecisionCode>? inspectionDecisionCodes = null,
        IEnumerable<PlcReasonCode>? reasonCodes = null)
    {
        Field = AlgorithmConfigurationValidation.Enum(field, nameof(field));
        RegisterRange = registerRange ?? throw new ArgumentNullException(nameof(registerRange));
        Encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        if ((Field is PlcFrameworkResultField.ControllerEpoch or PlcFrameworkResultField.ResultSequence) &&
            (executionStatusCodes is not null || inspectionDecisionCodes is not null || reasonCodes is not null))
            throw new ArgumentException("PlcFrameworkIdentityFieldCannotHaveCodeTable", nameof(field));
        if (Field != PlcFrameworkResultField.ExecutionStatus && executionStatusCodes is not null)
            throw new ArgumentException("PlcFrameworkExecutionStatusTableFieldMismatch", nameof(executionStatusCodes));
        if (Field != PlcFrameworkResultField.InspectionDecision && inspectionDecisionCodes is not null)
            throw new ArgumentException("PlcFrameworkInspectionDecisionTableFieldMismatch", nameof(inspectionDecisionCodes));
        if (Field != PlcFrameworkResultField.ResultReasonCode && reasonCodes is not null)
            throw new ArgumentException("PlcFrameworkReasonTableFieldMismatch", nameof(reasonCodes));

        _executionStatusCodes = CopyAndSort(executionStatusCodes,
            value => ((int)value.Value).ToString(CultureInfo.InvariantCulture), 256);
        _inspectionDecisionCodes = CopyAndSort(inspectionDecisionCodes,
            value => ((int)value.Value).ToString(CultureInfo.InvariantCulture), 256);
        _reasonCodes = CopyAndSort(reasonCodes, value => value.ReasonCode is null ? "" : value.ReasonCode,
            64 * 256 + 6);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-framework-field-mapping-v2", Field.ToString(), RegisterRange.ContentHash,
            Encoding.ContentHash, _executionStatusCodes.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(_executionStatusCodes.Select(value => value.ContentHash))
            .Concat(new[] { _inspectionDecisionCodes.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(_inspectionDecisionCodes.Select(value => value.ContentHash))
            .Concat(new[] { _reasonCodes.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(_reasonCodes.Select(value => value.ContentHash)));
    }

    public PlcFrameworkResultField Field { get; }
    public PlcFrameworkResultField FrameworkField => Field;
    public PlcRegisterRange RegisterRange { get; }
    public PlcRegisterRange Address => RegisterRange;
    public PlcWireEncoding Encoding { get; }
    public ReadOnlyCollection<PlcExecutionStatusCode> ExecutionStatusCodes => _executionStatusCodes;
    public ReadOnlyCollection<PlcInspectionDecisionCode> InspectionDecisionCodes => _inspectionDecisionCodes;
    public ReadOnlyCollection<PlcReasonCode> ReasonCodes => _reasonCodes;
    public string ContentHash { get; }

    private static ReadOnlyCollection<T> CopyAndSort<T>(IEnumerable<T>? values, Func<T, string> key,
        int maximumCount)
        where T : class
    {
        return new ReadOnlyCollection<T>(AlgorithmContractValidation.Copy(values, "codes", maximumCount)
            .OrderBy(key, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>One schema measurement mapping, including explicit exclusion and non-success bytes.</summary>
public sealed class PlcMeasurementMapping
{
    private readonly ReadOnlyCollection<PlcScalarCode> _codeTable;

    public PlcMeasurementMapping(string fieldKey, PlcMeasurementDisposition disposition,
        PlcRegisterRange? registerRange = null, PlcAffineConversion? conversion = null,
        PlcWireEncoding? encoding = null, PlcWireLiteral? nonSuccessData = null,
        PlcOptionalAbsence? optionalAbsence = null, IEnumerable<PlcScalarCode>? codeTable = null)
    {
        FieldKey = AlgorithmConfigurationValidation.Identifier(fieldKey, nameof(fieldKey));
        Disposition = AlgorithmConfigurationValidation.Enum(disposition, nameof(disposition));
        RegisterRange = registerRange;
        Conversion = conversion;
        Encoding = encoding;
        NonSuccessData = nonSuccessData;
        OptionalAbsence = optionalAbsence;
        if (Disposition == PlcMeasurementDisposition.Excluded &&
            (registerRange is not null || conversion is not null || encoding is not null ||
             nonSuccessData is not null || optionalAbsence is not null || codeTable is not null))
            throw new ArgumentException("PlcExcludedMeasurementMustNotHaveWireMapping", nameof(disposition));
        if (optionalAbsence is not null && Disposition != PlcMeasurementDisposition.Mapped)
            throw new ArgumentException("PlcOptionalAbsenceRequiresMappedMeasurement", nameof(optionalAbsence));
        _codeTable = new ReadOnlyCollection<PlcScalarCode>(AlgorithmContractValidation.Copy(
            codeTable, nameof(codeTable), 256).OrderBy(code => PlcScalarCode.ScalarTypeTag(code.Value),
            StringComparer.Ordinal).ThenBy(code => PlcScalarCode.ScalarValueTag(code.Value),
            StringComparer.Ordinal).ThenBy(code => code.LongWireCode).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-measurement-mapping-v2", FieldKey, Disposition.ToString(),
            RegisterRange?.ContentHash, Conversion?.ContentHash, Encoding?.ContentHash,
            NonSuccessData?.ContentHash, OptionalAbsence?.ContentHash,
            _codeTable.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(_codeTable.Select(code => code.ContentHash)));
    }

    public PlcMeasurementMapping(string fieldKey, PlcRegisterRange registerRange,
        PlcAffineConversion conversion, PlcWireEncoding encoding, PlcWireLiteral? nonSuccessData = null,
        PlcOptionalAbsence? optionalAbsence = null, IEnumerable<PlcScalarCode>? codeTable = null)
        : this(fieldKey, PlcMeasurementDisposition.Mapped, registerRange, conversion, encoding,
            nonSuccessData, optionalAbsence, codeTable) { }

    public PlcMeasurementMapping(string fieldKey, PlcMeasurementDisposition disposition)
        : this(fieldKey, disposition, null, null, null, null, null, null) { }

    public string FieldKey { get; }
    public string SchemaFieldKey => FieldKey;
    public PlcMeasurementDisposition Disposition { get; }
    public PlcRegisterRange? RegisterRange { get; }
    public PlcRegisterRange? Address => RegisterRange;
    public PlcAffineConversion? Conversion { get; }
    public PlcAffineConversion? AffineConversion => Conversion;
    public PlcWireEncoding? Encoding { get; }
    public PlcWireLiteral? NonSuccessData { get; }
    public PlcWireLiteral? NonSuccessValue => NonSuccessData;
    public PlcOptionalAbsence? OptionalAbsence { get; }
    public PlcOptionalAbsence? Absence => OptionalAbsence;
    public ReadOnlyCollection<PlcScalarCode> CodeTable => _codeTable;
    public ReadOnlyCollection<PlcScalarCode> Codes => _codeTable;
    public string ContentHash { get; }
}

/// <summary>One explicit constant field in a schema branch.</summary>
public sealed class PlcConstantField
{
    public PlcConstantField(string fieldKey, PlcRegisterRange registerRange, PlcWireEncoding encoding,
        PlcWireLiteral value)
    {
        FieldKey = AlgorithmConfigurationValidation.Identifier(fieldKey, nameof(fieldKey));
        RegisterRange = registerRange ?? throw new ArgumentNullException(nameof(registerRange));
        Encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-constant-field-v1", FieldKey, RegisterRange.ContentHash,
            Encoding.ContentHash, Value.ContentHash
        });
    }

    public string FieldKey { get; }
    public PlcRegisterRange RegisterRange { get; }
    public PlcRegisterRange Address => RegisterRange;
    public PlcWireEncoding Encoding { get; }
    public PlcWireLiteral Value { get; }
    public PlcWireLiteral WireValue => Value;
    public string ContentHash { get; }
}

/// <summary>One exact Algorithm Result Schema branch of a PLC result contract.</summary>
public sealed class PlcResultSchemaMap
{
    private readonly ReadOnlyCollection<PlcMeasurementMapping> _measurements;
    private readonly ReadOnlyCollection<PlcConstantField> _constantFields;

    public PlcResultSchemaMap(RecipeContractReference resultSchema,
        IEnumerable<PlcMeasurementMapping>? measurements = null,
        IEnumerable<PlcConstantField>? constantFields = null)
    {
        ResultSchema = resultSchema ?? throw new ArgumentNullException(nameof(resultSchema));
        _measurements = new ReadOnlyCollection<PlcMeasurementMapping>(AlgorithmContractValidation.Copy(
            measurements, nameof(measurements), 256).OrderBy(mapping => mapping.FieldKey,
            StringComparer.Ordinal).ToArray());
        _constantFields = new ReadOnlyCollection<PlcConstantField>(AlgorithmContractValidation.Copy(
            constantFields, nameof(constantFields), 256).OrderBy(field => field.FieldKey,
            StringComparer.Ordinal).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-result-schema-map-v2", ResultSchema.Id, ResultSchema.Version,
            ResultSchema.ContentHash, _measurements.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(_measurements.Select(mapping => mapping.ContentHash))
            .Concat(new[] { _constantFields.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(_constantFields.Select(field => field.ContentHash)));
    }

    public RecipeContractReference ResultSchema { get; }
    public ReadOnlyCollection<PlcMeasurementMapping> Measurements => _measurements;
    public ReadOnlyCollection<PlcMeasurementMapping> MeasurementMappings => _measurements;
    public ReadOnlyCollection<PlcConstantField> ConstantFields => _constantFields;
    public ReadOnlyCollection<PlcConstantField> Constants => _constantFields;
    public string ContentHash { get; }
}

/// <summary>
/// Immutable PLC result contract. It describes deployment bytes and mappings only; it does not
/// perform binding, production admission, result validity or PLC I/O.
/// </summary>
public sealed class PlcResultContract
{
    public const string FrameworkReasonCatalogId = "SharpInspect.T12.FrameworkReason";
    public const string FrameworkReasonCatalogVersion = "1";
    private static readonly ReadOnlyCollection<string> FrameworkReasonCatalogValues =
        new(new[]
        {
            "AlgorithmHung",
            "AlgorithmExecutionTimeout",
            "AlgorithmExecutionCancelled",
            "AlgorithmExecutionError",
            "AlgorithmResultContractViolation"
        });
    private readonly ReadOnlyCollection<PlcFrameworkFieldMapping> _frameworkFields;
    private readonly ReadOnlyCollection<PlcResultSchemaMap> _schemaMaps;

    public PlcResultContract(string id, string version, int maximumPayloadBytes,
        int maximumRegisterCount, IEnumerable<PlcFrameworkFieldMapping> frameworkFields,
        IEnumerable<PlcResultSchemaMap> schemaMaps)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        if (maximumPayloadBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (maximumRegisterCount is < 1 or > PlcRegisterRange.MaximumRegisterCount)
            throw new ArgumentOutOfRangeException(nameof(maximumRegisterCount));
        MaximumPayloadBytes = maximumPayloadBytes;
        MaximumRegisterCount = maximumRegisterCount;
        _frameworkFields = new ReadOnlyCollection<PlcFrameworkFieldMapping>(AlgorithmContractValidation.Copy(
            frameworkFields, nameof(frameworkFields), 64).OrderBy(field => field.Field).ToArray());
        _schemaMaps = new ReadOnlyCollection<PlcResultSchemaMap>(AlgorithmContractValidation.Copy(
            schemaMaps, nameof(schemaMaps), 64).OrderBy(map => map.ResultSchema.Id,
            StringComparer.Ordinal).ThenBy(map => map.ResultSchema.Version, StringComparer.Ordinal)
            .ThenBy(map => map.ResultSchema.ContentHash, StringComparer.Ordinal).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-result-contract-v2", Id, Version,
            MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            MaximumRegisterCount.ToString(CultureInfo.InvariantCulture),
            FrameworkReasonCatalogId, FrameworkReasonCatalogVersion,
            FrameworkReasonCatalogValues.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(FrameworkReasonCatalogValues)
            .Concat(new[] { _frameworkFields.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(_frameworkFields.Select(field => field.ContentHash))
            .Concat(new[] { _schemaMaps.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(_schemaMaps.Select(map => map.ContentHash)));
        Reference = new(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public int MaximumPayloadBytes { get; }
    public int MaximumRegisterCount { get; }
    public int MaximumRegisters => MaximumRegisterCount;
    public ReadOnlyCollection<PlcFrameworkFieldMapping> FrameworkFields => _frameworkFields;
    public ReadOnlyCollection<PlcResultSchemaMap> SchemaMaps => _schemaMaps;
    public IReadOnlyList<string> FrameworkReasonCatalog => FrameworkReasonCatalogValues;
    public IReadOnlyList<string> FrameworkReasonCatalogCodes => FrameworkReasonCatalogValues;
    public RecipeContractReference Reference { get; }
    public string ContentHash { get; }
    public static IReadOnlyList<string> FrameworkReasonCodes => FrameworkReasonCatalogValues;
}
