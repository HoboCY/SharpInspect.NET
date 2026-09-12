using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The persisted current projection of one outbox obligation: exactly the derived result of
/// its immutable facts plus the last fact reference that ties both directions together.
/// </summary>
internal sealed record ProductionOutboxWorkState(OutboxDelivery Delivery,
    OutboxDeliveryState State, int AttemptCount, int NextAttemptNumber, bool RetryEligible,
    bool PermanentBlock, Guid? ActiveAttemptId, Guid? ActiveRuntimeEpoch, string? LastFailureReasonCode,
    OutboxFailureCategory? LastFailureCategory, DateTimeOffset? RetryAfterUtc, long LastEventPosition,
    string? LastEventContentHash);
