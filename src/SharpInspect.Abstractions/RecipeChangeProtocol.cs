namespace SharpInspect.Abstractions;

/// <summary>Closed outcome values in the dedicated PLC Recipe Change response block.</summary>
public enum RecipeChangeOutcome : ushort
{
    Succeeded = 1,
    RejectedBusy = 2,
    RejectedUnknownCode = 3,
    FailedActivation = 4,
    ProtocolFault = 5
}

public enum RecipeChangeReason : ushort
{
    None = 0,
    RuntimeBusy = 1,
    LocalOperatorOnly = 2,
    UnknownCode = 3,
    RecipeRetired = 4,
    RequestChanged = 5,
    DuplicateRequest = 6,
    InitialStateInvalid = 7,
    UnexpectedAcknowledgement = 8,
    HandshakeDeadlineExceeded = 9,
    ActivationFailed = 10,
    RestorationFailed = 11,
    AuditUnavailable = 12,
    CommunicationLost = 13,
    Cancelled = 14
}

public enum RecipeChangeEventKind
{
    RequestObserved = 1,
    DecisionCommitted = 2,
    ResponsePublished = 3,
    AcknowledgementObserved = 4,
    ResponseCleared = 5,
    ResetObserved = 6,
    ProtocolFault = 7
}
