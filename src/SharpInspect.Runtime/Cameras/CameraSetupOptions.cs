using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Explicit, bounded registration settings for the Runtime camera setup seam.
/// Camera providers are supplied by DI or by the internal Runtime constructor;
/// this type never discovers or loads provider assemblies.
/// </summary>
public sealed class CameraSetupOptions
{
    private const int MaximumProviders = 4;

    public CameraSetupOptions()
    {
        Providers = new ReadOnlyCollection<ICameraProvider>(Array.Empty<ICameraProvider>());
    }

    public CameraSetupOptions(IEnumerable<ICameraProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var copied = new List<ICameraProvider>(MaximumProviders);
        foreach (var provider in providers)
        {
            if (copied.Count == MaximumProviders)
                throw new ArgumentException("CameraProviderCapacityExceeded", nameof(providers));
            if (provider is null)
                throw new ArgumentException("CameraProviderNull", nameof(providers));
            copied.Add(provider);
        }

        Providers = new ReadOnlyCollection<ICameraProvider>(copied);
    }

    /// <summary>Providers explicitly registered by the host. The list is defensive.</summary>
    public IReadOnlyList<ICameraProvider> Providers { get; }

    /// <summary>Bound for one provider/device operation, including provider I/O.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bound for draining an in-flight provider operation during Runtime shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Explicit station interface for optional network maintenance; never read from command input.</summary>
    public CameraStationNetwork? StationNetwork { get; init; }

    internal void Validate()
    {
        if (OperationTimeout < TimeSpan.FromMilliseconds(100) ||
            OperationTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        if (ShutdownTimeout < TimeSpan.FromMilliseconds(100) ||
            ShutdownTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
    }
}
