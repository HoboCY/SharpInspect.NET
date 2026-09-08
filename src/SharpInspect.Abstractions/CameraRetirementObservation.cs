namespace SharpInspect.Abstractions;

/// <summary>Actual completion of an owned camera retirement chain, independent of bounded caller shutdown.</summary>
public sealed record CameraRetirementObservation(bool SafeToReplace, string ReasonCode);
