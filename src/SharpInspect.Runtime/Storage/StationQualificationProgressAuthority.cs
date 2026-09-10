namespace SharpInspect.Runtime.Storage;

/// <summary>A writer-local authority decision whose session guard lives through commit.</summary>
internal sealed record StationQualificationProgressAuthorization(
    StationQualificationProgressRequest Request, IIdentityTransactionGuard? Guard = null);
