using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Images;

/// <summary>Describes pixels only; this value grants no evidence or commit authority.</summary>
internal sealed record CanonicalPngDescriptor(int Width, int Height, VisionPixelFormat PixelFormat,
    int? ValidBits, string CanonicalPixelHash);

/// <summary>Per physical operation caps, including a conservative native zlib working reserve.</summary>
internal sealed record PngCodecLimits(long MaximumCanonicalBytes, long MaximumEncodedBytes,
    int MaximumWorkingBytes, int MaximumChunks = 65_536);

internal sealed record VerifiedCanonicalPng(long EncodedByteLength, string CanonicalPixelHash);
