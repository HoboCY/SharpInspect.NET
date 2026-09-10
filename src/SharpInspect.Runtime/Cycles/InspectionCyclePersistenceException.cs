namespace SharpInspect.Runtime.Cycles;

internal sealed class InspectionCyclePersistenceException : InvalidOperationException
{
    internal InspectionCyclePersistenceException(string reason) : base(reason) { }
}
