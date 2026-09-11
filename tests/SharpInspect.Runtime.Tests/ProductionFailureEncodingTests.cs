using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionFailureEncodingTests
{
    [Theory]
    [InlineData(ExecutionStatus.Error, "CameraAcquisitionError", "000A", "0006")]
    [InlineData(ExecutionStatus.Timeout, "CameraAcquisitionTimeout", "000B", "0007")]
    [InlineData(ExecutionStatus.Cancelled, "CameraAcquisitionCancelled", "000C", "0008")]
    public void V142_E01_TypedProductionFailuresUseUnknownAndDeclaredNonSuccessBytes(
        ExecutionStatus status, string reason, string expectedStatus, string expectedReason)
    {
        var binding = Bind(CreateContract(PlcResultContract.ProductionFailureReasonCatalogV2));
        var inspectionId = Guid.Parse("14200000-0000-0000-0000-000000000001");
        var encoded = new PlcResultPayloadEncoder().EncodeFailure(binding, inspectionId,
            new(0x11223344, 0x55667788), status, reason);

        Assert.True(encoded.Succeeded, encoded.DetailReasonCode);
        var snapshot = Assert.IsType<PlcResultPayloadSnapshot>(encoded.Snapshot);
        Assert.Equal(inspectionId, snapshot.InspectionId);
        Assert.Equal(status, snapshot.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, snapshot.Decision);
        Assert.Equal(reason, snapshot.ReasonCode);
        Assert.Equal(expectedStatus, Hex(snapshot, 14));
        Assert.Equal("0003", Hex(snapshot, 15));
        Assert.Equal(expectedReason, Hex(snapshot, 16));
        Assert.Equal("CAFE", Hex(snapshot, 20));
        Assert.Equal("PlcResultFailurePayloadEncoded", encoded.ReasonCode);
    }

    [Fact]
    public void V142_E02_CatalogVersionIsImmutableAndLegacyContractsRejectNewReasons()
    {
        var catalog = PlcResultContract.ProductionFailureReasonCatalogV2;
        Assert.Equal("SharpInspect.T12.FrameworkReason", catalog.Id);
        Assert.Equal("2", catalog.Version);
        Assert.Equal(new[]
        {
            "AlgorithmHung", "AlgorithmExecutionTimeout", "AlgorithmExecutionCancelled",
            "AlgorithmExecutionError", "AlgorithmResultContractViolation",
            "CameraAcquisitionError", "CameraAcquisitionTimeout", "CameraAcquisitionCancelled"
        }, catalog.Codes);

        var supplied = catalog.Codes.ToArray();
        var copy = new PlcResultReasonCatalog(catalog.Id, catalog.Version, supplied);
        supplied[0] = "Tampered";
        Assert.Equal(catalog.ContentHash, copy.ContentHash);

        var schema = Schema();
        var legacyBinding = Bind(CreateContract(null), schema);
        var rejected = new PlcResultPayloadEncoder().EncodeFailure(legacyBinding, Guid.NewGuid(),
            new(1, 1), ExecutionStatus.Error, "CameraAcquisitionError");
        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.Snapshot);
        Assert.Equal("PlcResultFrameworkReasonCatalogMismatch", rejected.DetailReasonCode);

        var badStatus = new PlcResultPayloadEncoder().EncodeFailure(
            Bind(CreateContract(catalog), schema), Guid.NewGuid(), new(1, 1),
            ExecutionStatus.Success, "CameraAcquisitionError");
        Assert.False(badStatus.Succeeded);
        Assert.Equal("PlcResultFailureEncodingInputsRequired", badStatus.DetailReasonCode);
    }

    [Fact]
    public void V142_E02A_LegacyContractKeepsFormatOneAndVersionedCatalogRoundTripsAsFormatTwo()
    {
        var legacy = Revision(CreateContract(null));
        var versioned = Revision(CreateContract(PlcResultContract.ProductionFailureReasonCatalogV2));

        var legacyBytes = PlcResultContractStorageCodec.Encode(legacy);
        var versionedBytes = PlcResultContractStorageCodec.Encode(versioned);
        Assert.Equal(1, legacyBytes[4]);
        Assert.Equal(2, versionedBytes[4]);
        Assert.Equal(legacy.ContentHash,
            PlcResultContractStorageCodec.Decode(legacyBytes).ContentHash);
        Assert.Equal(versioned.ContentHash,
            PlcResultContractStorageCodec.Decode(versionedBytes).ContentHash);
        Assert.Equal(versionedBytes,
            PlcResultContractStorageCodec.Encode(PlcResultContractStorageCodec.Decode(versionedBytes)));
    }

    [Fact]
    public async Task V142_E03_NoFrameFailureHookCarriesEffectiveProductionReasonAndAcquisitionKind()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production,
            Guid.Parse("14200000-0000-0000-0000-000000000002"));
        var acquisition = new ManualCameraAcquisitionResult(false, "CameraTransportTimedOut",
            correlation, null, null, null, ExecutionStatus.Timeout)
        {
            FailureKind = CameraAcquisitionFailureKind.TimedOut
        };
        var hookCalls = 0;
        string? hookInputReason = null;
        InspectionCycleExecutionResult<string>? committed = null;
        var pipeline = new InspectionCyclePipeline<string>
        {
            Correlation = correlation,
            Prepared = null!,
            Execution = null!,
            ExecutionRequest = null!,
            GuardAsync = () => Task.CompletedTask,
            AcquireAsync = _ => ValueTask.FromResult(acquisition),
            ClaimExecution = () => null!,
            Encode = _ => throw new InvalidOperationException("Algorithm must not run without a frame"),
            EncodeFailure = (status, reason) =>
            {
                hookCalls++;
                Assert.Equal(ExecutionStatus.Timeout, status);
                hookInputReason = reason;
                return ("typed-failure-payload", "CameraAcquisitionTimeout");
            },
            CommitAsync = result =>
            {
                committed = result;
                return Task.FromResult<InspectionCycleCommitReceipt<string>?>(null);
            },
            PublishAsync = (_, _) => Task.CompletedTask
        };

        var coordinator = new InspectionCycleCoordinator<string>();
        coordinator.SetPhase(InspectionCyclePhase.Accepted);
        var result = await coordinator.ExecuteAsync(pipeline, CancellationToken.None);

        Assert.Equal(1, hookCalls);
        Assert.Equal("CameraTransportTimedOut", hookInputReason);
        Assert.Equal(ExecutionStatus.Timeout, result.Status);
        Assert.Equal("CameraAcquisitionTimeout", result.ReasonCode);
        Assert.Equal("typed-failure-payload", result.Payload);
        Assert.Equal(CameraAcquisitionFailureKind.TimedOut, result.AcquisitionFailure!.Kind);
        Assert.Equal("CameraTransportTimedOut", result.AcquisitionFailure.ReasonCode);
        Assert.Same(result, committed);
        Assert.Equal(InspectionCyclePhase.FaultTerminated, coordinator.Phase);
    }

    private static PlcResultContractBinding Bind(PlcResultContract contract,
        AlgorithmResultSchema? schema = null)
    {
        schema ??= Schema();
        var result = new PlcResultContractBinder().Bind(
            new RecipeReference("T42.Recipe", "1", new string('A', 64)),
            new AlgorithmIdentity("T42.Algorithm", "1"), schema, contract);
        Assert.True(result.Bound,
            string.Join(",", result.Checks.Where(value => !value.Passed).Select(value => value.ReasonCode)));
        return Assert.IsType<PlcResultContractBinding>(result.Binding);
    }

    private static PlcResultContract CreateContract(PlcResultReasonCatalog? catalog)
    {
        var effectiveCatalog = catalog ?? new PlcResultReasonCatalog(
            PlcResultContract.FrameworkReasonCatalogId, PlcResultContract.FrameworkReasonCatalogVersion,
            PlcResultContract.FrameworkReasonCodes);
        var schema = Schema();
        var u16 = new PlcWireEncoding(PlcWireRepresentation.UInt16, PlcByteOrder.BigEndian,
            PlcWordOrder.NotApplicable, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var u32 = new PlcWireEncoding(PlcWireRepresentation.UInt32, PlcByteOrder.BigEndian,
            PlcWordOrder.HighWordFirst, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var reasons = effectiveCatalog.Codes.Select((value, index) =>
            new PlcReasonCode(value, index + 1)).Prepend(new PlcReasonCode(null, 0));
        var fields = new[]
        {
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch, new(10, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultSequence, new(12, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ExecutionStatus, new(14, 1), u16,
                executionStatusCodes: Enum.GetValues<ExecutionStatus>()
                    .Select((value, index) => new PlcExecutionStatusCode(value, index + 9))),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.InspectionDecision, new(15, 1), u16,
                inspectionDecisionCodes: Enum.GetValues<InspectionDecision>()
                    .Select((value, index) => new PlcInspectionDecisionCode(value, index + 1))),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultReasonCode, new(16, 1), u16,
                reasonCodes: reasons)
        };
        var mapping = new PlcMeasurementMapping("Value", new(20, 1),
            new("count", "count", new(1), new(0)), u16, new PlcWireLiteral(new byte[] { 0xCA, 0xFE }));
        var map = new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), new[] { mapping });
        return catalog is null
            ? new PlcResultContract("T42.Contract.Legacy", "1", 128, 64, fields, new[] { map })
            : new PlcResultContract("T42.Contract.Production", "1", 128, 64, fields, new[] { map }, catalog);
    }

    private static AlgorithmResultSchema Schema() => new("T42.Result", "1", new[]
    {
        new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Int64, "count", true,
            new(minInt64: 0, maxInt64: 100))
    }, Array.Empty<string>(), new OverlayContract("T42.Overlay", "1"));

    private static string Hex(PlcResultPayloadSnapshot snapshot, int address) =>
        Convert.ToHexString(snapshot.Segments.Single(value => value.StartRegister == address)
            .RegisterBytes.ToArray());

    private static PlcResultContractRevision Revision(PlcResultContract contract)
    {
        var schema = Schema();
        var binding = Bind(contract, schema);
        var reason = "T42 storage compatibility";
        var previous = (RecipeContractReference?)null;
        return new PlcResultContractRevision(1,
            Guid.Parse("14200000-0000-0000-0000-000000000011"),
            Guid.Parse("14200000-0000-0000-0000-000000000012"), contract, previous, 0,
            new[] { binding.Validation }, Array.Empty<PlcReleasedRecipeBinding>(),
            Guid.Parse("14200000-0000-0000-0000-000000000013"),
            Guid.Parse("14200000-0000-0000-0000-000000000014"), 1,
            Guid.Parse("14200000-0000-0000-0000-000000000015"),
            new RecipeContractReference("T42.Policy", "1", new string('B', 64)),
            reason,
            ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, previous, reason),
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
    }
}
