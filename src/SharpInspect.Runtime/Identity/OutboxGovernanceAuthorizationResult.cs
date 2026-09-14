using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Identity;

/// <summary>The typed outcome of one governed outbox authorization attempt.</summary>
internal sealed record OutboxGovernanceAuthorizationResult(RuntimeCommandOutcome Outcome,
    ProductionOutboxRecoveryResult? Recovery, ProductionOutboxCorrectionResult? Correction);
