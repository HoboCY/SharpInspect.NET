namespace SharpInspect.Abstractions;

/// <summary>
/// Ownership of one canonical camera frame and its separate audit provenance.
/// The host disposes this token only after all actual consumers finish. Algorithms
/// receive only the borrowed <see cref="VisionFrame"/>, never this ownership token.
/// </summary>
public interface IFrameBufferLease : IDisposable
{
    Guid LeaseId { get; }
    VisionFrame Frame { get; }
    FrameProvenance Provenance { get; }
    bool IsReturned { get; }
}
