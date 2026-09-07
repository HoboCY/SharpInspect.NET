using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>Versioned interpretation of the closed V1 Frame Pixel primitives.</summary>
public static class OverlayRenderingContract
{
    public const string Id = "SharpInspect.FramePixel";
    public const string Version = "1";
    public static string ContentHash { get; } = AlgorithmContractValidation.HashParts(new[]
    {
        Id, Version, "origin:top-left-pixel-center;x:right;y:down;bounds:half-pixel",
        "rotation:clockwise-degrees;painter:collection-order;stroke-and-size:frame-pixels",
        "viewport:dip=(frame-pixel+0.5)*zoom+pan-dip;dpi:rasterization-only",
        "text-anchor:primitive.AnchorKind;style.TextAnchor:non-geometric",
        "marker-size:primitive.Size;style.MarkerSize:non-geometric",
        "clip:display-only;invalid-or-unrepresentable:whole-view-unavailable"
    });
}

/// <summary>
/// Runtime-validated immutable geometry associated with one exact normalized frame.
/// It is a read capability, not an arbitrary Overlay Set accepted from a UI caller.
/// </summary>
public sealed class FrameOverlaySnapshot
{
    internal FrameOverlaySnapshot(Guid overlaySetId, FrameMetadata frameMetadata,
        AlgorithmResultSchema resultSchema, OutputOverlaySet overlaySet, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(frameMetadata);
        ArgumentNullException.ThrowIfNull(resultSchema);
        ArgumentNullException.ThrowIfNull(overlaySet);
        if (overlaySetId == Guid.Empty || string.IsNullOrWhiteSpace(contentHash))
            throw new ArgumentException("OverlaySnapshotIdentityRequired");
        if (overlaySet.ContractId != resultSchema.OverlayContract.Id ||
            overlaySet.ContractVersion != resultSchema.OverlayContract.Version)
            throw new ArgumentException("OverlaySnapshotContractMismatch");
        OverlaySetId = overlaySetId; FrameMetadata = frameMetadata; ResultSchema = resultSchema;
        OverlaySet = overlaySet; ContentHash = contentHash;
    }

    public Guid OverlaySetId { get; }
    public ExecutionCorrelationId Correlation => FrameMetadata.Correlation;
    public FrameMetadata FrameMetadata { get; }
    public AlgorithmResultSchema ResultSchema { get; }
    public OutputOverlaySet OverlaySet { get; }
    public string ContentHash { get; }
    public string RendererContractId => OverlayRenderingContract.Id;
    public string RendererContractVersion => OverlayRenderingContract.Version;
    public string RendererContractContentHash => OverlayRenderingContract.ContentHash;
}

/// <summary>
/// Historical development computation. This is neither a Core Inspection Record nor
/// a completed Manual/Qualification execution record or a result-publication permit.
/// </summary>
public sealed class AlgorithmResultRecord
{
    internal AlgorithmResultRecord(long position, Guid recordId, DateTimeOffset recordedAtUtc,
        string contentHash, Guid preparedInstanceId, AlgorithmIdentity algorithm,
        string configurationContentHash, string configurationSchemaId, string configurationSchemaVersion,
        string configurationSchemaContentHash, FrameMetadata frameMetadata, AlgorithmResultSchema resultSchema,
        AlgorithmResult result, AlgorithmExecutionTimingSnapshot timing,
        long admittedMonotonicTimestamp, long monotonicFrequency)
    {
        Position = position; RecordId = recordId; RecordedAtUtc = recordedAtUtc; ContentHash = contentHash;
        PreparedInstanceId = preparedInstanceId; Algorithm = algorithm;
        ConfigurationContentHash = configurationContentHash; FrameMetadata = frameMetadata;
        ConfigurationSchemaId = configurationSchemaId; ConfigurationSchemaVersion = configurationSchemaVersion;
        ConfigurationSchemaContentHash = configurationSchemaContentHash;
        ResultSchema = resultSchema; Result = result; Timing = timing;
        AdmittedMonotonicTimestamp = admittedMonotonicTimestamp; MonotonicFrequency = monotonicFrequency;
        Overlay = new FrameOverlaySnapshot(recordId, frameMetadata, resultSchema, result.OverlaySet, contentHash);
    }

    public long Position { get; }
    public Guid RecordId { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
    public ExecutionCorrelationId Correlation => FrameMetadata.Correlation;
    public Guid PreparedInstanceId { get; }
    public AlgorithmIdentity Algorithm { get; }
    public string ConfigurationContentHash { get; }
    public string ConfigurationSchemaId { get; }
    public string ConfigurationSchemaVersion { get; }
    public string ConfigurationSchemaContentHash { get; }
    public FrameMetadata FrameMetadata { get; }
    public AlgorithmResultSchema ResultSchema { get; }
    public AlgorithmResult Result { get; }
    public ExecutionStatus ExecutionStatus => ExecutionStatus.Success;
    public InspectionDecision Decision => Result.Decision;
    public string? ReasonCode => Result.ReasonCode;
    public AlgorithmExecutionTimingSnapshot Timing { get; }
    public long AdmittedMonotonicTimestamp { get; }
    public long MonotonicFrequency { get; }
    public FrameOverlaySnapshot Overlay { get; }
}

/// <summary>Bounded keyset query; an explicit high watermark freezes subsequent pages.</summary>
public sealed record AlgorithmResultFilter(ExecutionCorrelationId? Correlation = null,
    long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);

public sealed record AlgorithmResultPage(bool Available, string ReasonCode,
    ReadOnlyCollection<AlgorithmResultRecord> Records, long ThroughPosition, long? NextAfterPosition);

/// <summary>Read-only result history; no connection, mutation or arbitrary query language.</summary>
public interface IAlgorithmResultQuery
{
    ValueTask<AlgorithmResultPage> QueryAsync(AlgorithmResultFilter filter,
        CancellationToken cancellationToken = default);
}
