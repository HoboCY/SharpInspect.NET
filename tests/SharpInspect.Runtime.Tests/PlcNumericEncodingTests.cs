using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcNumericEncodingTests
{
    [Fact]
    public void V131_N01_IntegerAffineAndRegisterOrderUseExactBytes()
    {
        var conversion = new PlcAffineConversion("count", "wire", new(3, 2), new(1, 2));
        var encoding = Wire(PlcWireRepresentation.UInt32, PlcRoundingMode.Exact,
            PlcByteOrder.LittleEndian, PlcWordOrder.LowWordFirst);

        var succeeded = PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromInt64(3), conversion,
            encoding, out var bytes, out var reason);

        Assert.True(succeeded, reason);
        Assert.Equal("05000000", Convert.ToHexString(bytes));
    }

    [Theory]
    [InlineData(PlcRoundingMode.Exact, false, "")]
    [InlineData(PlcRoundingMode.TowardZero, true, "0000")]
    [InlineData(PlcRoundingMode.TowardPositiveInfinity, true, "0001")]
    [InlineData(PlcRoundingMode.TowardNegativeInfinity, true, "0000")]
    [InlineData(PlcRoundingMode.AwayFromZero, true, "0001")]
    [InlineData(PlcRoundingMode.ToNearestTiesToEven, true, "0000")]
    [InlineData(PlcRoundingMode.ToNearestTiesAwayFromZero, true, "0001")]
    public void V131_N02_DirectedAndTieRoundingIsExplicit(PlcRoundingMode mode, bool expected,
        string expectedHex)
    {
        var conversion = Identity("count");
        var succeeded = PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(0.5), conversion,
            Wire(PlcWireRepresentation.Int16, mode), out var bytes, out var reason);

        Assert.Equal(expected, succeeded);
        if (expected)
            Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        else
            Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData(PlcRoundingMode.TowardZero, "0000")]
    [InlineData(PlcRoundingMode.TowardPositiveInfinity, "0000")]
    [InlineData(PlcRoundingMode.TowardNegativeInfinity, "FFFF")]
    [InlineData(PlcRoundingMode.AwayFromZero, "FFFF")]
    [InlineData(PlcRoundingMode.ToNearestTiesToEven, "0000")]
    [InlineData(PlcRoundingMode.ToNearestTiesAwayFromZero, "FFFF")]
    public void V131_N03_NegativeDirectedRoundingUsesMathematicalDirection(PlcRoundingMode mode,
        string expectedHex)
    {
        var succeeded = PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(-0.5),
            Identity("count"), Wire(PlcWireRepresentation.Int16, mode), out var bytes, out var reason);

        Assert.True(succeeded, reason);
        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public void V131_N04_IeeeBoundariesRoundExactlyAndRejectInfinity()
    {
        var identity = Identity("value");
        var exactMinimumSubnormal = (double)BitConverter.Int32BitsToSingle(1);
        var minimum = Wire(PlcWireRepresentation.Ieee754Binary32, PlcRoundingMode.Exact);

        Assert.True(PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(exactMinimumSubnormal),
            identity, minimum, out var minimumBytes, out var minimumReason), minimumReason);
        Assert.Equal("00000001", Convert.ToHexString(minimumBytes));

        Assert.True(PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(-exactMinimumSubnormal),
            identity, minimum, out var negativeMinimumBytes, out var negativeMinimumReason), negativeMinimumReason);
        Assert.Equal("80000001", Convert.ToHexString(negativeMinimumBytes));

        Assert.True(PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(-double.Epsilon), identity,
            Wire(PlcWireRepresentation.Ieee754Binary64, PlcRoundingMode.Exact), out var binary64Bytes,
            out var binary64Reason), binary64Reason);
        Assert.Equal("8000000000000001", Convert.ToHexString(binary64Bytes));

        Assert.True(PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(1.5), identity,
            minimum, out var onePointFiveBytes, out var onePointFiveReason), onePointFiveReason);
        Assert.Equal("3FC00000", Convert.ToHexString(onePointFiveBytes));

        var overflow = PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(double.MaxValue),
            identity, Wire(PlcWireRepresentation.Ieee754Binary32, PlcRoundingMode.TowardZero),
            out var overflowBytes, out var overflowReason);
        Assert.False(overflow);
        Assert.Empty(overflowBytes);
        Assert.NotEmpty(overflowReason);
    }

    [Fact]
    public void V131_N05_CodeEncodingIsIntegerOnlyAndNeverPartiallyWrites()
    {
        Assert.True(PlcNumericEncoding.TryEncodeCode(0x1234,
            Wire(PlcWireRepresentation.UInt16), out var integerBytes, out var integerReason), integerReason);
        Assert.Equal("1234", Convert.ToHexString(integerBytes));

        var rejected = PlcNumericEncoding.TryEncodeCode(1,
            Wire(PlcWireRepresentation.Ieee754Binary32), out var floatingBytes, out var floatingReason);
        Assert.False(rejected);
        Assert.Empty(floatingBytes);
        Assert.NotEmpty(floatingReason);
    }

    [Fact]
    public void V131_N06_DomainAndSentinelProofsRejectUnboundedOrCollidingLayouts()
    {
        var boundedField = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", false,
            new(minInt64: 0, maxInt64: 100));
        var boundedMapping = new PlcMeasurementMapping("Value", new(10, 1),
            new("count", "count", new(1), new(0)), Wire(PlcWireRepresentation.UInt16),
            new PlcWireLiteral(new byte[] { 0xEE, 0xEE }),
            new PlcOptionalAbsence(new PlcWireLiteral(new byte[] { 0xFF, 0xFF })));

        Assert.True(PlcNumericEncoding.TryValidateDomain(boundedField, boundedMapping, out var boundedReason),
            boundedReason);
        Assert.True(PlcNumericEncoding.TryValidateSentinel(boundedField, boundedMapping,
            new PlcWireLiteral(new byte[] { 0xFF, 0xFF }), out var safeReason), safeReason);
        Assert.False(PlcNumericEncoding.TryValidateSentinel(boundedField, boundedMapping,
            new PlcWireLiteral(new byte[] { 0x00, 0x64 }), out var collisionReason));
        Assert.NotEmpty(collisionReason);

        var unboundedField = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true);
        var unboundedMapping = new PlcMeasurementMapping("Value", new(10, 1),
            new("count", "count", new(1), new(0)), Wire(PlcWireRepresentation.UInt16),
            new PlcWireLiteral(new byte[] { 0xEE, 0xEE }));
        Assert.False(PlcNumericEncoding.TryValidateDomain(unboundedField, unboundedMapping,
            out var unboundedReason));
        Assert.NotEmpty(unboundedReason);

        var fractionalAffineField = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true,
            new(minInt64: 0, maxInt64: 2));
        var fractionalAffineMapping = new PlcMeasurementMapping("Value", new(10, 1),
            new("count", "count", new(1, 2), new(0)), Wire(PlcWireRepresentation.Int16),
            new PlcWireLiteral(new byte[] { 0xEE, 0xEE }));
        Assert.False(PlcNumericEncoding.TryValidateDomain(fractionalAffineField, fractionalAffineMapping,
            out var fractionalAffineReason));
        Assert.NotEmpty(fractionalAffineReason);

        var binaryInteriorField = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true,
            new(minInt64: 0, maxInt64: 1L << 54));
        var binaryInteriorMapping = new PlcMeasurementMapping("Value", new(10, 4),
            new("count", "count", new(1), new(0)), Wire(PlcWireRepresentation.Ieee754Binary64),
            new PlcWireLiteral(new byte[8]));
        Assert.False(PlcNumericEncoding.TryValidateDomain(binaryInteriorField, binaryInteriorMapping,
            out var binaryInteriorReason));
        Assert.NotEmpty(binaryInteriorReason);
    }

    private static PlcAffineConversion Identity(string unit) => new(unit, unit, new(1), new(0));

    [Fact]
    public void V131_N07_WholeDomainProofAppliesScaleAndOffsetBeforeCheckingWireBounds()
    {
        var field = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true,
            new(minInt64: 0, maxInt64: 1000));
        var expanded = new PlcMeasurementMapping("Value", new PlcRegisterRange(10, 1),
            new("count", "centi_count", new(100), new(0)), Wire(PlcWireRepresentation.UInt16), new PlcWireLiteral(new byte[2]));
        Assert.False(PlcNumericEncoding.TryValidateDomain(field, expanded, out var overflow));
        Assert.Equal("PlcNumericOverflow", overflow);
        var compressedSource = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true,
            new(minInt64: 100000, maxInt64: 200000));
        var compressed = new PlcMeasurementMapping("Value", new PlcRegisterRange(10, 1),
            new("count", "hundred_count", new(1, 100), new(0)), Wire(PlcWireRepresentation.UInt16, PlcRoundingMode.TowardZero),
            new PlcWireLiteral(new byte[2]));
        Assert.True(PlcNumericEncoding.TryValidateDomain(compressedSource, compressed, out var compressedReason), compressedReason);
        var shifted = new PlcMeasurementMapping("Value", new PlcRegisterRange(10, 1),
            new("count", "count", new(1), new(-1)), Wire(PlcWireRepresentation.UInt16), new PlcWireLiteral(new byte[2]));
        Assert.False(PlcNumericEncoding.TryValidateDomain(field, shifted, out var shiftedReason));
        Assert.Equal("PlcNumericOverflow", shiftedReason);
    }

    [Fact]
    public void V131_N08_ExactModeDoesNotInferInteriorRepresentabilityFromEndpointSuccess()
    {
        var field = new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Float64, "value", true,
            new(minFloat64: -1, maxFloat64: 1));
        var integer = new PlcMeasurementMapping("Value", new PlcRegisterRange(10, 1), Identity("value"),
            Wire(PlcWireRepresentation.Int16), new PlcWireLiteral(new byte[2]));
        Assert.False(PlcNumericEncoding.TryValidateDomain(field, integer, out var reason));
        Assert.Equal("PlcNumericDomainNotProvable", reason);
        var binary64 = new PlcMeasurementMapping("Value", new PlcRegisterRange(10, 4), Identity("value"),
            Wire(PlcWireRepresentation.Ieee754Binary64), new PlcWireLiteral(new byte[8]));
        Assert.True(PlcNumericEncoding.TryValidateDomain(new("Value", AlgorithmScalarType.Float64, "value", true), binary64,
            out var identityReason), identityReason);
        var negativeZero = PlcNumericEncoding.TryEncode(AlgorithmScalarValue.FromFloat64(-double.Epsilon), Identity("value"),
            Wire(PlcWireRepresentation.Ieee754Binary32, PlcRoundingMode.TowardZero), out var zero, out var zeroReason);
        Assert.True(negativeZero, zeroReason);
        Assert.Equal("00000000", Convert.ToHexString(zero));
    }

    private static PlcWireEncoding Wire(PlcWireRepresentation representation,
        PlcRoundingMode rounding = PlcRoundingMode.Exact,
        PlcByteOrder byteOrder = PlcByteOrder.BigEndian,
        PlcWordOrder? wordOrder = null)
    {
        var width = PlcNumericEncoding.RegisterWidth(representation);
        var words = wordOrder ?? (width == 1 ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst);
        return new(representation, byteOrder, words, rounding, PlcOverflowBehavior.EncodingFault);
    }
}
