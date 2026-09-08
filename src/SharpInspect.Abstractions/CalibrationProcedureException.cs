namespace SharpInspect.Abstractions;

/// <summary>A bounded mathematical failure that Runtime retains as a failed session
/// reason. It never authorizes a result or exposes native exception details.</summary>
public sealed class CalibrationProcedureException : InvalidOperationException
{
    public CalibrationProcedureException(string reasonCode)
        : base(AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode))) => ReasonCode = reasonCode;
    public string ReasonCode { get; }
}
