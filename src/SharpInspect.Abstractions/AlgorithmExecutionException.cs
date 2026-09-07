namespace SharpInspect.Abstractions;

/// <summary>
/// An unexpected computation failure with a proposed machine reason. Runtime admits
/// that reason only when it belongs to the prepared result schema. Inner details
/// are never part of an execution outcome or ordinary diagnostic projection.
/// </summary>
public sealed class AlgorithmExecutionException : Exception
{
    public AlgorithmExecutionException(string reasonCode, Exception? innerException = null)
        : base("AlgorithmExecutionFailed", innerException)
    {
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
    }

    public string ReasonCode { get; }
}
