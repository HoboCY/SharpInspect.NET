using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Production;

/// <summary>Production structured evidence, without a development archive identity or authority.</summary>
internal sealed record ProductionAlgorithmResultDocument(Guid InspectionId, ExecutionCorrelationId Correlation,
    Guid PreparedInstanceId, AlgorithmIdentity Algorithm, string ConfigurationContentHash,
    string ConfigurationSchemaId, string ConfigurationSchemaVersion, string ConfigurationSchemaContentHash,
    FrameMetadata FrameMetadata, AlgorithmResultSchema ResultSchema, AlgorithmResult Result,
    AlgorithmExecutionTimingSnapshot Timing, long AdmittedMonotonicTimestamp, long MonotonicFrequency,
    string PayloadJson, string PayloadHash);

internal static class ProductionAlgorithmResultCodec
{
    internal static ProductionAlgorithmResultDocument Encode(Guid inspectionId, AlgorithmExecutionOutcome outcome) =>
        AlgorithmResultStorageCodec.EncodeProduction(inspectionId, outcome);

    internal static ProductionAlgorithmResultDocument Decode(Guid inspectionId, string json, string hash) =>
        AlgorithmResultStorageCodec.DecodeProduction(inspectionId, json, hash);
}
