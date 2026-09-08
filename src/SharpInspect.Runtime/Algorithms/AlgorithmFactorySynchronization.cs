using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>One physical Factory cannot be invoked concurrently by preparation and Draft validation.</summary>
internal static class AlgorithmFactorySynchronization
{
    private static readonly ConditionalWeakTable<IVisionAlgorithmFactory, SemaphoreSlim> Gates = new();
    internal static SemaphoreSlim For(IVisionAlgorithmFactory factory) => Gates.GetValue(factory, _ => new(1, 1));
}
