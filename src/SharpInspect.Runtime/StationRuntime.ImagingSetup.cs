using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    public ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(string logicalCameraRole,
        CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.GetImagingSetupAsync(logicalCameraRole, invocation, cancellationToken);

    public ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(
        ImagingSetupChangeRequest request, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.DeclareImagingSetupAsync(request, cancellationToken);

    public ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(string logicalCameraRole,
        CommandInvocation invocation, long afterPosition = 0, long? throughPosition = null,
        int pageSize = 50, CancellationToken cancellationToken = default) =>
        _cameraSetupRuntime.QueryImagingSetupHistoryAsync(logicalCameraRole, invocation, afterPosition,
            throughPosition, pageSize, cancellationToken);
}
