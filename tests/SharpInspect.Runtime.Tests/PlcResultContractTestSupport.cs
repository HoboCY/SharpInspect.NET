using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

internal static class PlcResultContractTestSupport
{
    internal static PlcResultContract Contract(AlgorithmResultSchema schema, string version = "1")
    {
        var u16 = new PlcWireEncoding(PlcWireRepresentation.UInt16, PlcByteOrder.BigEndian,
            PlcWordOrder.NotApplicable, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var u32 = new PlcWireEncoding(PlcWireRepresentation.UInt32, PlcByteOrder.BigEndian,
            PlcWordOrder.HighWordFirst, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var reasons = PlcResultContract.FrameworkReasonCodes.Concat(schema.ReasonCodes)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .Select((value, index) => new PlcReasonCode(value, index + 1)).Prepend(new(null, 0));
        return new("V131.Integration.Contract", version, 256, 64, new[]
        {
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch, new(10, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultSequence, new(12, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ExecutionStatus, new(14, 1), u16,
                executionStatusCodes: Enum.GetValues<ExecutionStatus>().Select((value, index) => new PlcExecutionStatusCode(value, index))),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.InspectionDecision, new(15, 1), u16,
                inspectionDecisionCodes: Enum.GetValues<InspectionDecision>().Select((value, index) => new PlcInspectionDecisionCode(value, index))),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultReasonCode, new(16, 1), u16, reasonCodes: reasons)
        }, new[] { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash),
            schema.Measurements.Select(value => new PlcMeasurementMapping(value.Key, PlcMeasurementDisposition.Excluded))) });
    }

    internal static async Task<PlcResultContractRevision> CommitAsync(ProductionStoreOptions options,
        RecipeDraftService drafts, LocalAuthorizationService authorization, StationRuntime runtime,
        CommandInvocation invocation, string password, AlgorithmResultSchema schema)
    {
        var query = new SqlitePlcResultContractQuery(options);
        var service = new PlcResultContractService(drafts, new SqliteReleasedRecipeQuery(options),
            query, authorization, options, runtime.EnterPlcResultContractChangeAsync, () => runtime.GetSnapshotAsync());
        runtime.ConfigurePlcResultContractService(service);
        var command = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation, Contract(schema),
            null, "V131 cross-ledger integration");
        var grant = await authorization.ReauthenticateAsync(new(Guid.NewGuid(), invocation,
            new(Permission.ManagePlcResultContract, command.CorrelationId, command.AuthorizationTarget,
                AuditedCommandKind.ChangePlcResultContract), password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        var outcome = await runtime.SubmitAsync(command with
        { Invocation = invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(outcome.Disposition == CommandDisposition.Accepted, outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        var read = await query.ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        Assert.NotNull(read.Revision);
        Assert.Single(read.Revision!.Bindings);
        return read.Revision;
    }
}
