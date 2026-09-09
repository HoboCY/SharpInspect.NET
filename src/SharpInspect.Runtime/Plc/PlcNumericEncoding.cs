using System.Numerics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Exact numeric proof and encoding helpers for the PLC result binder and payload encoder.
/// Every affine and rounding operation uses normalized BigInteger rationals. Host floating
/// point is used only to decompose an already validated AlgorithmScalarValue.
/// </summary>
internal static class PlcNumericEncoding
{
    private const string InputsRequired = "PlcNumericInputsRequired";
    private const string UnsupportedValueType = "PlcNumericValueTypeUnsupported";
    private const string UnsupportedWireType = "PlcNumericWireTypeUnsupported";
    private const string NonFiniteValue = "PlcNumericValueNonFinite";
    private const string MappingIncomplete = "PlcNumericMappingIncomplete";
    private const string SourceUnitMismatch = "PlcNumericSourceUnitMismatch";
    private const string Overflow = "PlcNumericOverflow";
    private const string ExactRepresentationRequired = "PlcNumericExactRepresentationRequired";
    private const string RoundingInvalid = "PlcNumericRoundingInvalid";
    private const string CodeRequiresIntegerWire = "PlcNumericCodeRequiresIntegerWire";
    private const string SentinelSafetyUnprovable = "PlcNumericSentinelSafetyUnprovable";
    private const string SentinelCollision = "PlcResultSentinelCollision";
    private const string LiteralWidthInvalid = "PlcNumericSentinelWidthInvalid";

    /// <summary>Returns the number of 16-bit registers occupied by a representation, or zero if invalid.</summary>
    internal static int RegisterWidth(PlcWireRepresentation representation) => representation switch
    {
        PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16 => 1,
        PlcWireRepresentation.UInt32 or PlcWireRepresentation.Int32 or PlcWireRepresentation.Ieee754Binary32 => 2,
        PlcWireRepresentation.UInt64 or PlcWireRepresentation.Int64 or PlcWireRepresentation.Ieee754Binary64 => 4,
        _ => 0
    };

    /// <summary>Encodes one already validated scalar through an exact affine conversion.</summary>
    internal static bool TryEncode(AlgorithmScalarValue value, PlcAffineConversion conversion,
        PlcWireEncoding encoding, out byte[] bytes, out string reason)
    {
        bytes = Array.Empty<byte>();
        reason = string.Empty;
        if (value is null || conversion is null || encoding is null)
        {
            reason = InputsRequired;
            return false;
        }
        if (encoding.Overflow != PlcOverflowBehavior.EncodingFault)
        {
            reason = Overflow;
            return false;
        }
        if (!WireOrderShapeValid(encoding))
        {
            reason = "PlcNumericWireWidthOrOrderInvalid";
            return false;
        }
        if (!TryGetScalarRational(value, out var source, out reason))
            return false;

        var multiplier = new ExactRational(conversion.Multiplier.Numerator, conversion.Multiplier.Denominator);
        var offset = new ExactRational(conversion.Offset.Numerator, conversion.Offset.Denominator);
        var converted = source.Multiply(multiplier).Add(offset);
        return TryEncodeRational(converted, encoding, out bytes, out reason);
    }

    /// <summary>Encodes a typed code-table integer without applying an affine conversion.</summary>
    internal static bool TryEncodeCode(long wireCode, PlcWireEncoding encoding,
        out byte[] bytes, out string reason)
    {
        bytes = Array.Empty<byte>();
        reason = string.Empty;
        if (encoding is null)
        {
            reason = InputsRequired;
            return false;
        }
        if (encoding.Overflow != PlcOverflowBehavior.EncodingFault)
        {
            reason = Overflow;
            return false;
        }
        if (!WireOrderShapeValid(encoding))
        {
            reason = "PlcNumericWireWidthOrOrderInvalid";
            return false;
        }
        if (!TryGetIntegerFormat(encoding.Representation, out var bits, out var signed))
        {
            reason = CodeRequiresIntegerWire;
            return false;
        }

        var value = new BigInteger(wireCode);
        if (!FitsInteger(value, bits, signed))
        {
            reason = Overflow;
            return false;
        }
        bytes = FormatInteger(value, bits, signed, encoding);
        return true;
    }

    /// <summary>
    /// Proves the complete schema-permitted numeric domain can be encoded. The proof checks both
    /// affine extrema because a nonzero affine multiplier is monotonic.
    /// </summary>
    internal static bool TryValidateDomain(AlgorithmFieldDefinition field,
        PlcMeasurementMapping mapping, out string reason)
    {
        reason = string.Empty;
        if (field is null || mapping is null)
        {
            reason = InputsRequired;
            return false;
        }
        var range = mapping.RegisterRange;
        var conversion = mapping.Conversion;
        var encoding = mapping.Encoding;
        if (mapping.Disposition != PlcMeasurementDisposition.Mapped ||
            range is null || conversion is null || encoding is null)
        {
            reason = MappingIncomplete;
            return false;
        }
        if (!string.Equals(mapping.FieldKey, field.Key, StringComparison.Ordinal))
        {
            reason = "PlcNumericFieldKeyMismatch";
            return false;
        }
        if (!string.Equals(conversion.SourceUnit, field.Unit, StringComparison.Ordinal))
        {
            reason = SourceUnitMismatch;
            return false;
        }
        if (encoding.Overflow != PlcOverflowBehavior.EncodingFault)
        {
            reason = Overflow;
            return false;
        }
        if (range.RegisterCount != RegisterWidth(encoding.Representation) ||
            !WireOrderShapeValid(encoding))
        {
            reason = "PlcNumericWireWidthOrOrderInvalid";
            return false;
        }
        var nonSuccessData = mapping.NonSuccessData;
        if (nonSuccessData is null || nonSuccessData.Length != range.RegisterCount * 2)
        {
            reason = "PlcNumericNonSuccessDataRequired";
            return false;
        }
        if (mapping.CodeTable.Count != 0)
        {
            reason = "PlcNumericCodeTableForbidden";
            return false;
        }
        if (!TryGetDomainEndpoints(field, out var first, out var second, out reason))
            return false;

        var multiplier = new ExactRational(conversion.Multiplier.Numerator,
            conversion.Multiplier.Denominator);
        var offset = new ExactRational(conversion.Offset.Numerator,
            conversion.Offset.Denominator);
        var convertedFirst = first.Multiply(multiplier).Add(offset);
        var convertedSecond = second.Multiply(multiplier).Add(offset);
        if (!TryEncodeRational(convertedFirst, encoding, out _, out reason) ||
            !TryEncodeRational(convertedSecond, encoding, out _, out reason))
            return false;
        if (!CanProveCompleteDomain(field, encoding, multiplier, offset,
                convertedFirst, convertedSecond))
        {
            reason = "PlcNumericDomainNotProvable";
            return false;
        }

        if (mapping.OptionalAbsence is { Kind: PlcOptionalAbsenceKind.Sentinel } absence &&
                !TryValidateSentinel(field, mapping, absence.Sentinel!, out reason))
            return false;
        if (mapping.OptionalAbsence is { Kind: PlcOptionalAbsenceKind.ValidityField } validityAbsence)
        {
            var validity = validityAbsence.ValidityField;
            if (validity is null || validity.PresentData.Length != validity.RegisterRange.RegisterCount * 2 ||
                validity.AbsentData.Length != validity.RegisterRange.RegisterCount * 2 ||
                validity.PresentData.Equals(validity.AbsentData))
            {
                reason = "PlcNumericValidityRepresentationInvalid";
                return false;
            }
            if (validityAbsence.AbsentData is null ||
                validityAbsence.AbsentData.Length != range.RegisterCount * 2)
            {
                reason = "PlcNumericValidityAbsentDataInvalid";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Conservatively proves an integer sentinel cannot equal any value in the permitted numeric
    /// range. Floating sentinels are rejected because a complete exact collision proof is not
    /// represented by this bounded helper; use an independent validity field instead.
    /// </summary>
    internal static bool TryValidateSentinel(AlgorithmFieldDefinition field,
        PlcMeasurementMapping mapping, PlcWireLiteral sentinel, out string reason)
    {
        reason = string.Empty;
        if (field is null || mapping is null || sentinel is null)
        {
            reason = InputsRequired;
            return false;
        }
        var range = mapping.RegisterRange;
        var conversion = mapping.Conversion;
        var encoding = mapping.Encoding;
        if (mapping.Disposition != PlcMeasurementDisposition.Mapped || conversion is null ||
            encoding is null || range is null)
        {
            reason = MappingIncomplete;
            return false;
        }
        if (field.Type is not (AlgorithmScalarType.Int64 or AlgorithmScalarType.Float64))
        {
            reason = "PlcNumericSentinelOnlyForNumericField";
            return false;
        }
        if (!string.Equals(field.Key, mapping.FieldKey, StringComparison.Ordinal) ||
            !string.Equals(field.Unit, conversion.SourceUnit, StringComparison.Ordinal))
        {
            reason = SourceUnitMismatch;
            return false;
        }
        if (!TryGetIntegerFormat(encoding.Representation, out var bits, out var signed))
        {
            reason = SentinelSafetyUnprovable;
            return false;
        }
        var expectedLength = RegisterWidth(encoding.Representation) * 2;
        if (sentinel.Length != expectedLength || range.RegisterCount != RegisterWidth(encoding.Representation))
        {
            reason = LiteralWidthInvalid;
            return false;
        }
        if (!TryGetDomainEndpoints(field, out var first, out var second, out reason))
            return false;

        var multiplier = new ExactRational(conversion.Multiplier.Numerator,
            conversion.Multiplier.Denominator);
        var offset = new ExactRational(conversion.Offset.Numerator,
            conversion.Offset.Denominator);
        var low = first.Multiply(multiplier).Add(offset);
        var high = second.Multiply(multiplier).Add(offset);
        // The endpoint values above are source-domain values. Re-using the affine operation here
        // makes the comparison independent of how the caller supplied the source constraints.
        var lowValue = low.CompareTo(high) <= 0 ? low : high;
        var highValue = low.CompareTo(high) <= 0 ? high : low;
        if (!CanProveCompleteDomain(field, encoding, multiplier, offset, low, high))
        {
            reason = SentinelSafetyUnprovable;
            return false;
        }
        if (!TryEncodeRational(lowValue, encoding, out var lowBytes, out reason) ||
            !TryEncodeRational(highValue, encoding, out var highBytes, out reason))
            return false;
        if (!TryDecodeInteger(lowBytes, encoding, bits, signed, out var lowCode) ||
            !TryDecodeInteger(highBytes, encoding, bits, signed, out var highCode))
        {
            reason = SentinelSafetyUnprovable;
            return false;
        }
        var sentinelCode = DecodeInteger(sentinel, encoding, bits, signed);
        var lowerCode = BigInteger.Min(lowCode, highCode);
        var upperCode = BigInteger.Max(lowCode, highCode);
        if (sentinelCode >= lowerCode && sentinelCode <= upperCode)
        {
            reason = SentinelCollision;
            return false;
        }
        return true;
    }

    private static bool CanProveCompleteDomain(AlgorithmFieldDefinition field,
        PlcWireEncoding encoding, ExactRational multiplier, ExactRational offset,
        ExactRational first, ExactRational second)
    {
        if (encoding.Rounding != PlcRoundingMode.Exact)
            return true;

        var constraints = field.Constraints;
        var singleton = field.Type switch
        {
            AlgorithmScalarType.Int64 => constraints?.MinInt64 is { } minimumInteger &&
                constraints?.MaxInt64 is { } maximumInteger && minimumInteger == maximumInteger,
            AlgorithmScalarType.Float64 => constraints?.MinFloat64 is { } minimumFloat &&
                constraints?.MaxFloat64 is { } maximumFloat &&
                minimumFloat == maximumFloat,
            _ => false
        };
        if (singleton)
            return true;

        if (field.Type == AlgorithmScalarType.Float64)
        {
            // Every finite binary64 value is already exactly representable by binary64. An
            // identity conversion therefore proves the entire discrete source domain.
            return encoding.Representation == PlcWireRepresentation.Ieee754Binary64 &&
                multiplier.Numerator == 1 && multiplier.Denominator == 1 &&
                offset.IsZero;
        }

        if (field.Type != AlgorithmScalarType.Int64)
            return false;

        if (TryGetIntegerFormat(encoding.Representation, out _, out _))
        {
            // For an integer source, all adjacent source values map to adjacent affine
            // differences. Exact integrality therefore requires an integral multiplier and
            // one integral endpoint (which also proves the offset is integral).
            return multiplier.Denominator == 1 && first.Denominator == 1 && second.Denominator == 1;
        }

        if (!TryGetFloatingFormat(encoding.Representation, out var precision, out _, out _, out _, out _))
            return false;
        if (multiplier.Denominator != 1 || first.Denominator != 1 || second.Denominator != 1)
            return false;
        var maximumExactlyRepresentableInteger = BigInteger.One << precision;
        return BigInteger.Abs(first.Numerator) <= maximumExactlyRepresentableInteger &&
            BigInteger.Abs(second.Numerator) <= maximumExactlyRepresentableInteger;
    }

    private static bool TryGetScalarRational(AlgorithmScalarValue value, out ExactRational result,
        out string reason)
    {
        reason = string.Empty;
        switch (value.Type)
        {
            case AlgorithmScalarType.Int64:
                result = new ExactRational(value.AsInt64(), BigInteger.One);
                return true;
            case AlgorithmScalarType.Float64:
                return TryDecomposeDouble(value.AsFloat64(), out result, out reason);
            default:
                result = default;
                reason = UnsupportedValueType;
                return false;
        }
    }

    private static bool TryDecomposeDouble(double value, out ExactRational result, out string reason)
    {
        result = default;
        reason = string.Empty;
        if (!double.IsFinite(value))
        {
            reason = NonFiniteValue;
            return false;
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = (bits & long.MinValue) != 0;
        var fraction = (ulong)bits & 0x000F_FFFF_FFFF_FFFFUL;
        var exponent = (int)((ulong)bits >> 52 & 0x7FF);
        BigInteger significand;
        int power;
        if (exponent == 0)
        {
            significand = fraction;
            power = -1074;
        }
        else
        {
            significand = (BigInteger.One << 52) | fraction;
            power = exponent - 1023 - 52;
        }
        if (negative) significand = -significand;
        result = power >= 0
            ? new ExactRational(significand << power, BigInteger.One)
            : new ExactRational(significand, BigInteger.One << -power);
        return true;
    }

    private static bool TryEncodeRational(ExactRational value, PlcWireEncoding encoding,
        out byte[] bytes, out string reason)
    {
        bytes = Array.Empty<byte>();
        reason = string.Empty;
        if (!TryGetIntegerFormat(encoding.Representation, out var bits, out var signed))
        {
            if (!TryGetFloatingFormat(encoding.Representation, out var precision, out var minimumExponent,
                    out var maximumExponent, out var fractionBits, out var totalBits))
            {
                reason = UnsupportedWireType;
                return false;
            }
            if (!TryEncodeFloating(value, encoding.Rounding, precision, minimumExponent, maximumExponent,
                    fractionBits, totalBits, out var floatingBits, out reason))
                return false;
            bytes = FormatWireBits(floatingBits, totalBits, encoding);
            return true;
        }

        if (!TryRound(value, encoding.Rounding, out var rounded, out reason))
            return false;
        if (!FitsInteger(rounded, bits, signed))
        {
            reason = Overflow;
            return false;
        }
        bytes = FormatInteger(rounded, bits, signed, encoding);
        return true;
    }

    private static bool TryRound(ExactRational value, PlcRoundingMode mode,
        out BigInteger rounded, out string reason)
    {
        rounded = default;
        reason = string.Empty;
        if (!Enum.IsDefined(typeof(PlcRoundingMode), mode))
        {
            reason = RoundingInvalid;
            return false;
        }
        var negative = value.Numerator.Sign < 0;
        var magnitude = BigInteger.DivRem(BigInteger.Abs(value.Numerator), value.Denominator, out var remainder);
        if (remainder.IsZero)
        {
            rounded = negative ? -magnitude : magnitude;
            return true;
        }
        if (mode == PlcRoundingMode.Exact)
        {
            reason = ExactRepresentationRequired;
            return false;
        }
        var increment = mode switch
        {
            PlcRoundingMode.TowardZero => false,
            PlcRoundingMode.AwayFromZero => true,
            PlcRoundingMode.TowardPositiveInfinity => !negative,
            PlcRoundingMode.TowardNegativeInfinity => negative,
            PlcRoundingMode.ToNearestTiesToEven => CompareHalf(remainder, value.Denominator) > 0 ||
                CompareHalf(remainder, value.Denominator) == 0 && !magnitude.IsEven,
            PlcRoundingMode.ToNearestTiesAwayFromZero => CompareHalf(remainder, value.Denominator) >= 0,
            _ => false
        };
        rounded = negative ? -(magnitude + (increment ? BigInteger.One : BigInteger.Zero)) :
            magnitude + (increment ? BigInteger.One : BigInteger.Zero);
        return true;
    }

    private static int CompareHalf(BigInteger remainder, BigInteger denominator) =>
        (remainder << 1).CompareTo(denominator);

    private static bool TryEncodeFloating(ExactRational value, PlcRoundingMode mode, int precision,
        int minimumExponent, int maximumExponent, int fractionBits, int totalBits,
        out BigInteger bits, out string reason)
    {
        bits = BigInteger.Zero;
        reason = string.Empty;
        if (!Enum.IsDefined(typeof(PlcRoundingMode), mode))
        {
            reason = RoundingInvalid;
            return false;
        }
        if (value.IsZero)
            return true;

        var negative = value.Numerator.Sign < 0;
        var numerator = BigInteger.Abs(value.Numerator);
        var denominator = value.Denominator;
        var exponent = FloorLog2(numerator, denominator);
        BigInteger significand;
        if (exponent >= minimumExponent)
        {
            var scale = precision - 1 - exponent;
            var scaledNumerator = scale >= 0 ? numerator << scale : numerator;
            var scaledDenominator = scale >= 0 ? denominator : denominator << -scale;
            if (!TryRoundPositive(scaledNumerator, scaledDenominator, mode, negative,
                    out significand, out reason))
                return false;
            var top = BigInteger.One << precision;
            if (significand >= top)
            {
                significand >>= 1;
                exponent++;
            }
            if (exponent > maximumExponent)
            {
                reason = Overflow;
                return false;
            }
            var minimumSignificand = BigInteger.One << (precision - 1);
            if (significand < minimumSignificand)
            {
                reason = SentinelSafetyUnprovable;
                return false;
            }
            var exponentField = exponent + maximumExponent;
            var fraction = significand - minimumSignificand;
            bits = (BigInteger)(negative ? 1 : 0) << (totalBits - 1) |
                (BigInteger)exponentField << fractionBits | fraction;
            return true;
        }

        // A subnormal has a fixed quantum of 2^(minimumExponent - (precision - 1)).
        var subnormalScale = precision - 1 - minimumExponent;
        var subnormalNumerator = numerator << subnormalScale;
        if (!TryRoundPositive(subnormalNumerator, denominator, mode, negative,
                out var subnormal, out reason))
            return false;
        var minimumNormal = BigInteger.One << (precision - 1);
        if (subnormal >= minimumNormal)
        {
            // Rounding a subnormal into the first normal is represented with exponent one.
            bits = (BigInteger)(negative ? 1 : 0) << (totalBits - 1) |
                (BigInteger.One << fractionBits);
            return true;
        }
        // The abstraction normalizes signed zero; retain the sign for a nonzero negative
        // subnormal while emitting canonical +0 when rounding underflows to zero.
        bits = (BigInteger)(negative && !subnormal.IsZero ? 1 : 0) << (totalBits - 1) | subnormal;
        return true;
    }

    private static bool TryRoundPositive(BigInteger numerator, BigInteger denominator,
        PlcRoundingMode mode, bool negative, out BigInteger rounded, out string reason)
    {
        // The exact mode is handled here too: a binary quantum must divide the exact source.
        reason = string.Empty;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder.IsZero)
        {
            rounded = quotient;
            return true;
        }
        if (mode == PlcRoundingMode.Exact)
        {
            rounded = default;
            reason = ExactRepresentationRequired;
            return false;
        }
        var increment = mode switch
        {
            PlcRoundingMode.TowardZero => false,
            PlcRoundingMode.AwayFromZero => true,
            PlcRoundingMode.TowardPositiveInfinity => !negative,
            PlcRoundingMode.TowardNegativeInfinity => negative,
            PlcRoundingMode.ToNearestTiesToEven => CompareHalf(remainder, denominator) > 0 ||
                CompareHalf(remainder, denominator) == 0 && !quotient.IsEven,
            PlcRoundingMode.ToNearestTiesAwayFromZero => CompareHalf(remainder, denominator) >= 0,
            _ => false
        };
        rounded = quotient + (increment ? BigInteger.One : BigInteger.Zero);
        return true;
    }

    private static bool TryGetDomainEndpoints(AlgorithmFieldDefinition field, out ExactRational first,
        out ExactRational second, out string reason)
    {
        first = default;
        second = default;
        reason = string.Empty;
        switch (field.Type)
        {
            case AlgorithmScalarType.Int64:
                first = new ExactRational(field.Constraints?.MinInt64 ?? long.MinValue, BigInteger.One);
                second = new ExactRational(field.Constraints?.MaxInt64 ?? long.MaxValue, BigInteger.One);
                return true;
            case AlgorithmScalarType.Float64:
                var minimum = field.Constraints?.MinFloat64 ?? -double.MaxValue;
                var maximum = field.Constraints?.MaxFloat64 ?? double.MaxValue;
                if (!TryDecomposeDouble(minimum, out first, out reason) ||
                    !TryDecomposeDouble(maximum, out second, out reason))
                    return false;
                return true;
            default:
                first = default;
                second = default;
                reason = UnsupportedValueType;
                return false;
        }
    }

    private static bool TryGetIntegerFormat(PlcWireRepresentation representation, out int bits,
        out bool signed)
    {
        bits = representation switch
        {
            PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16 => 16,
            PlcWireRepresentation.UInt32 or PlcWireRepresentation.Int32 => 32,
            PlcWireRepresentation.UInt64 or PlcWireRepresentation.Int64 => 64,
            _ => 0
        };
        signed = representation is PlcWireRepresentation.Int16 or PlcWireRepresentation.Int32 or
            PlcWireRepresentation.Int64;
        return bits != 0;
    }

    private static bool TryGetFloatingFormat(PlcWireRepresentation representation, out int precision,
        out int minimumExponent, out int maximumExponent, out int fractionBits, out int totalBits)
    {
        switch (representation)
        {
            case PlcWireRepresentation.Ieee754Binary32:
                precision = 24; minimumExponent = -126; maximumExponent = 127;
                fractionBits = 23; totalBits = 32; return true;
            case PlcWireRepresentation.Ieee754Binary64:
                precision = 53; minimumExponent = -1022; maximumExponent = 1023;
                fractionBits = 52; totalBits = 64; return true;
            default:
                precision = minimumExponent = maximumExponent = fractionBits = totalBits = 0;
                return false;
        }
    }

    private static bool FitsInteger(BigInteger value, int bits, bool signed)
    {
        var upper = BigInteger.One << (signed ? bits - 1 : bits);
        return signed ? value >= -upper && value < upper : value >= 0 && value < upper;
    }

    private static byte[] FormatInteger(BigInteger value, int bits, bool signed, PlcWireEncoding encoding)
    {
        var unsigned = value;
        if (signed && unsigned.Sign < 0)
            unsigned += BigInteger.One << bits;
        return FormatWireBits(unsigned, bits, encoding);
    }

    private static byte[] FormatWireBits(BigInteger bits, int totalBits, PlcWireEncoding encoding)
    {
        var byteCount = totalBits / 8;
        var canonical = bits.ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[byteCount];
        if (canonical.Length > byteCount)
            throw new InvalidOperationException(Overflow);
        Buffer.BlockCopy(canonical, 0, bytes, byteCount - canonical.Length, canonical.Length);
        if (encoding.ByteOrder == PlcByteOrder.LittleEndian)
        {
            for (var index = 0; index < bytes.Length; index += 2)
                (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
        }
        if (bytes.Length > 2 && encoding.WordOrder == PlcWordOrder.LowWordFirst)
        {
            var words = bytes.Length / 2;
            var reordered = new byte[bytes.Length];
            for (var word = 0; word < words; word++)
                Buffer.BlockCopy(bytes, (words - 1 - word) * 2, reordered, word * 2, 2);
            bytes = reordered;
        }
        return bytes;
    }

    private static bool WireOrderShapeValid(PlcWireEncoding encoding)
    {
        var width = RegisterWidth(encoding.Representation);
        return width > 0 && (width == 1
            ? encoding.WordOrder == PlcWordOrder.NotApplicable
            : encoding.WordOrder is PlcWordOrder.HighWordFirst or PlcWordOrder.LowWordFirst);
    }

    private static bool TryDecodeInteger(byte[] bytes, PlcWireEncoding encoding, int bits, bool signed,
        out BigInteger value)
    {
        if (bytes.Length != bits / 8)
        {
            value = default;
            return false;
        }
        value = DecodeInteger(new PlcWireLiteral(bytes), encoding, bits, signed);
        return true;
    }

    private static BigInteger DecodeInteger(PlcWireLiteral literal, PlcWireEncoding encoding, int bits,
        bool signed)
    {
        var canonical = literal.ToArray();
        if (canonical.Length > 2 && encoding.WordOrder == PlcWordOrder.LowWordFirst)
        {
            var words = canonical.Length / 2;
            var reordered = new byte[canonical.Length];
            for (var word = 0; word < words; word++)
                Buffer.BlockCopy(canonical, (words - 1 - word) * 2, reordered, word * 2, 2);
            canonical = reordered;
        }
        if (encoding.ByteOrder == PlcByteOrder.LittleEndian)
        {
            for (var index = 0; index < canonical.Length; index += 2)
                (canonical[index], canonical[index + 1]) = (canonical[index + 1], canonical[index]);
        }
        var unsigned = new BigInteger(canonical, isUnsigned: true, isBigEndian: true);
        if (signed && (unsigned & (BigInteger.One << (bits - 1))) != 0)
            return unsigned - (BigInteger.One << bits);
        return unsigned;
    }

    private static int FloorLog2(BigInteger numerator, BigInteger denominator)
    {
        var exponent = BitLength(numerator) - BitLength(denominator);
        while (CompareWithPowerOfTwo(numerator, denominator, exponent) < 0) exponent--;
        while (CompareWithPowerOfTwo(numerator, denominator, exponent + 1) >= 0) exponent++;
        return exponent;
    }

    private static int CompareWithPowerOfTwo(BigInteger numerator, BigInteger denominator, int exponent)
    {
        return exponent >= 0
            ? numerator.CompareTo(denominator << exponent)
            : (numerator << -exponent).CompareTo(denominator);
    }

    private static int BitLength(BigInteger value)
    {
        value = BigInteger.Abs(value);
        if (value.IsZero) return 0;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var first = bytes[0];
        var leading = 0;
        while ((first & (0x80 >> leading)) == 0) leading++;
        return bytes.Length * 8 - leading;
    }

    private readonly struct ExactRational : IComparable<ExactRational>
    {
        internal ExactRational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new ArgumentException("PlcNumericDenominatorZero");
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / gcd;
            Denominator = denominator / gcd;
        }

        internal BigInteger Numerator { get; }
        internal BigInteger Denominator { get; }
        internal bool IsZero => Numerator.IsZero;
        internal ExactRational Multiply(ExactRational other) =>
            new(Numerator * other.Numerator, Denominator * other.Denominator);
        internal ExactRational Add(ExactRational other) =>
            new(Numerator * other.Denominator + other.Numerator * Denominator,
                Denominator * other.Denominator);
        public int CompareTo(ExactRational other) =>
            (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    }
}
