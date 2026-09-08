namespace SharpInspect.Abstractions;

/// <summary>
/// Requests one new cycle for an exhausted, exactly identified bound-camera
/// recovery cycle. ReasonCode is a bounded machine-readable inspection reason.
/// Invocation supplies attribution only; Runtime revalidates authority and
/// commits an audit before starting. This never acknowledges, resets, or arms.
/// </summary>
public sealed record StartCameraRecoveryCycleCommand(Guid CorrelationId,
    CommandInvocation Invocation, string LogicalRole, Guid ExpectedCycleId,
    string ReasonCode) : RuntimeCommand(CorrelationId, Invocation);
