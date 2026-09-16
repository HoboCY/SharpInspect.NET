using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;

namespace SharpInspect.Runtime.Performance;

internal sealed partial class RuntimePerformanceMonitor
{
    internal void ObserveFrame(IFrameBufferLease lease)
    {
        // Baseline-only, explicitly represented in the collector profile. Continuous production monitoring
        // never hashes pixels. The fixed byte cap bounds this work; no pixels enter the evidence document.
        if (_capture is not { IsSealed: false } capture) return;
        var metadata = lease.Frame.Metadata;
        var at = Stopwatch.GetTimestamp();
        string? digest = null;
        var reason = "PerformanceFrameHashUnavailable";
        try
        {
            if ((long)metadata.ValidRowBytes * metadata.Height > _options.Contract.Capture.MaximumFrameHashBytes)
                reason = "PerformanceFrameHashBudgetExceeded";
            else
            {
                using var frame = RetainedProductionFrame.Capture(lease);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendBytes(hash, Encoding.UTF8.GetBytes("SharpInspect.VirtualCameraImage.Pixels.v1"));
                AppendInt(hash, metadata.Width); AppendInt(hash, metadata.Height);
                AppendInt(hash, (int)metadata.PixelFormat); AppendInt(hash, metadata.ValidBits ?? -1);
                AppendInt(hash, metadata.ValidRowBytes);
                for (var row = 0; row < metadata.Height; row++) AppendBytes(hash, frame.GetRowSpan(row));
                digest = Convert.ToHexString(hash.GetHashAndReset()); reason = "PerformanceFrameHashObserved";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        capture.Frame(new(metadata.Correlation, at, metadata.Width, metadata.Height, metadata.PixelFormat,
            metadata.ValidBits, digest, digest is null ? PerformanceObservationOutcome.Unknown : PerformanceObservationOutcome.Observed, reason));
    }
    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    { AppendInt(hash, bytes.Length); hash.AppendData(bytes); }
    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value); hash.AppendData(bytes);
    }
}
