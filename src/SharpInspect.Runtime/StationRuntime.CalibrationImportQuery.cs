using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    public async ValueTask<CalibrationGovernanceQueryResult<CalibrationImportRecord>> ReadImportOperationAsync(
        Guid operationId, CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) return new(false, "CalibrationImportOperationIdRequired");
        if (Interlocked.Increment(ref _calibrationImportQueries) > 8)
        {
            Interlocked.Decrement(ref _calibrationImportQueries);
            return new(false, "CalibrationImportQueryCapacityExceeded");
        }
        try
        {
            if (_authorization is null || _audit is not SqliteCommandStore store || _calibrationTransfers is null)
                return new(false, "CalibrationImportUnavailable");
            var reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            if (reason is not null) return new(false, reason);
            var query = await store.ReadCalibrationImportStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!query.Available || query.State is null) return new(false, query.ReasonCode);
            var record = query.State.Records.SingleOrDefault(value => value.OperationId == operationId);
            reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            return reason is not null ? new(false, reason) : record is null ? new(false, "CalibrationImportRecordNotFound") :
                new(true, "CalibrationImportRecordAvailable", record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "CalibrationImportQueryUnavailable"); }
        finally { Interlocked.Decrement(ref _calibrationImportQueries); }
    }

    public async ValueTask<CalibrationExportResult> ExportCalibrationAsync(CalibrationCandidateReference candidate,
        RecipeContractReference policy, CommandInvocation invocation, CalibrationProfileReference? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        if (Interlocked.Increment(ref _calibrationImportQueries) > 8)
        {
            Interlocked.Decrement(ref _calibrationImportQueries);
            return new(false, "CalibrationImportQueryCapacityExceeded");
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var token = bounded.Token;
        try
        {
            if (_authorization is null || _audit is not SqliteCommandStore store || _calibrationImages is null ||
                _calibrationStoreOptions?.LocalIdentity is not { } identity)
                return new(false, "CalibrationExportUnavailable");
            var reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, token).ConfigureAwait(false);
            if (reason is not null) return new(false, reason);
            var session = await store.ReadCalibrationSessionAsync(candidate.SessionId, token).ConfigureAwait(false);
            var governance = await store.ReadCalibrationGovernanceAsync(token).ConfigureAwait(false);
            if (!session.Available || session.Evidence?.Candidate is not { } computed || !governance.Available ||
                computed.CandidateId != candidate.CandidateId || computed.ContentHash != candidate.CandidateContentHash)
                return new(false, "CalibrationExportSourceUnavailable");
            var records = governance.Records.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray();
            var sourcePolicy = CalibrationGovernanceProjection.Policy(records, policy);
            if (sourcePolicy is null) return new(false, "CalibrationExportPolicyUnavailable");
            var sourceProfile = profile is null ? null : CalibrationGovernanceProjection.Profile(records, profile);
            if (profile is not null && sourceProfile is null) return new(false, "CalibrationExportProfileUnavailable");
            if (session.Evidence.Frames.Sum(value => value.ByteLength) > CalibrationExportPackage.MaximumBytes)
                return new(false, "CalibrationExportCapacityExceeded");
            var images = new List<CalibrationFrameImage>();
            foreach (var frame in session.Evidence.Frames)
                images.Add(await _calibrationImages.ReadAsync(frame, token).ConfigureAwait(false));
            var package = await RunCalibrationTransferCodecAsync(() => CalibrationExportPackageCodec.Encode(identity.StationId,
                session.Evidence, sourcePolicy, images, sourceProfile), token).ConfigureAwait(false);
            reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, token).ConfigureAwait(false);
            return reason is null ? new(true, "CalibrationExportAvailable", package) : new(false, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "CalibrationExportEvidenceUnavailable"); }
        finally { Interlocked.Decrement(ref _calibrationImportQueries); }
    }
}
