using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private readonly PhysicalCalibrationVerificationRegistry? _physicalCalibrationVerifiers;
    private int _calibrationGovernanceQueries;

    private async Task<CalibrationGovernancePreparation> PrepareCalibrationGovernanceAsync(
        CalibrationGovernanceCommand command, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        if (_authorization is null || _audit is not SqliteCommandStore store)
            return new(SourceImagesFailure: "CalibrationSourceImagesUnavailable");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(PositiveRemaining(deadline));
        var token = bounded.Token;
        var authorized = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(command.Invocation, token)
            .ConfigureAwait(false);
        if (authorized is not null) return new(SourceImagesFailure: "CalibrationSourceImagesUnavailable");
        var candidate = command switch
        {
            EvaluateCalibrationCandidateCommand evaluate => evaluate.Candidate,
            PublishCalibrationProfileCommand publish => publish.Candidate,
            _ => null
        };
        if (candidate is not null)
        {
            try
            {
                var query = await store.ReadCalibrationSessionAsync(candidate.SessionId, token).ConfigureAwait(false);
                if (query.Evidence?.Candidate is not { } current || current.CandidateId != candidate.CandidateId ||
                    current.ContentHash != candidate.CandidateContentHash || _calibrationImages is null)
                    return new(SourceImagesFailure: "CalibrationSourceImagesUnavailable");
                foreach (var frame in query.Evidence.Frames)
                    _ = await _calibrationImages.ReadAsync(frame, token).ConfigureAwait(false);
                return new(candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return new(SourceImagesFailure: "CalibrationSourceImagesUnavailable"); }
        }
        if (command is RecordPhysicalCalibrationVerificationCommand verify)
        {
            var query = await store.ReadCalibrationGovernanceAsync(token).ConfigureAwait(false);
            if (!query.Available) return new();
            var records = query.Records.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray();
            var profile = CalibrationGovernanceProjection.Profile(records, verify.Profile);
            if (profile is null) return new();
            var result = _physicalCalibrationVerifiers is null ? new PhysicalVerificationComputation(profile.ContentHash,
                verify.Submission.ContentHash, Array.Empty<CalibrationQualityMetric>(), "PhysicalVerificationProcedureUnavailable") :
                await _physicalCalibrationVerifiers.EvaluateAsync(profile, verify.Submission,
                    PositiveRemaining(deadline), token).ConfigureAwait(false);
            return new(Verification: result);
        }
        return new();
    }

    public async ValueTask<CalibrationPolicyPublishResult> PublishPolicyAsync(PublishCalibrationAcceptancePolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        var record = await ReadCommittedGovernanceOperationAsync<CalibrationAcceptancePolicyRevision>(command, outcome, cancellationToken)
            .ConfigureAwait(false);
        return new(outcome, record);
    }
    public async ValueTask<CalibrationCandidateEvaluationResult> EvaluateCandidateAsync(EvaluateCalibrationCandidateCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        var record = await ReadCommittedGovernanceOperationAsync<CalibrationPolicyEvaluationRecord>(command, outcome, cancellationToken)
            .ConfigureAwait(false);
        return new(outcome, record);
    }
    public async ValueTask<CalibrationProfilePublishResult> PublishProfileAsync(PublishCalibrationProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        var record = await ReadCommittedGovernanceOperationAsync<PublishedCalibrationProfileVersion>(command, outcome, cancellationToken)
            .ConfigureAwait(false);
        return new(outcome, record);
    }
    public async ValueTask<PhysicalCalibrationVerificationResult> RecordPhysicalVerificationAsync(
        RecordPhysicalCalibrationVerificationCommand command, CancellationToken cancellationToken = default)
    {
        var outcome = await SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        var record = await ReadCommittedGovernanceOperationAsync<PhysicalCalibrationVerificationRecord>(command, outcome, cancellationToken)
            .ConfigureAwait(false);
        return new(outcome, record);
    }

    private async Task<T?> ReadCommittedGovernanceOperationAsync<T>(CalibrationGovernanceCommand command,
        RuntimeCommandOutcome outcome, CancellationToken cancellationToken) where T : class
    {
        if (outcome.Disposition != CommandDisposition.Accepted) return null;
        var query = await ReadGovernanceAsync(command.Invocation, values => values.OfType<T>()
            .SingleOrDefault(value => CalibrationGovernanceProjection.Metadata(value).OperationId == command.CorrelationId),
            cancellationToken).ConfigureAwait(false);
        return query.Value;
    }

    public ValueTask<CalibrationGovernanceQueryResult<CalibrationAcceptancePolicyRevision>> ReadPolicyAsync(
        RecipeContractReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        ReadGovernanceAsync(invocation, values => values.OfType<CalibrationAcceptancePolicyRevision>()
            .SingleOrDefault(value => value.Policy.Reference == reference), cancellationToken);
    public ValueTask<CalibrationGovernanceQueryResult<CalibrationPolicyEvaluationRecord>> ReadEvaluationAsync(
        CalibrationPolicyEvaluationReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        ReadGovernanceAsync(invocation, values => values.OfType<CalibrationPolicyEvaluationRecord>()
            .SingleOrDefault(value => value.Reference == reference), cancellationToken);
    public ValueTask<CalibrationGovernanceQueryResult<PublishedCalibrationProfileVersion>> ReadProfileAsync(
        CalibrationProfileReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        ReadGovernanceAsync(invocation, values => values.OfType<PublishedCalibrationProfileVersion>()
            .SingleOrDefault(value => value.Reference == reference), cancellationToken);
    public ValueTask<CalibrationGovernanceQueryResult<PhysicalCalibrationVerificationRecord>> ReadVerificationAsync(
        PhysicalCalibrationVerificationReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default) =>
        ReadGovernanceAsync(invocation, values => values.OfType<PhysicalCalibrationVerificationRecord>()
            .SingleOrDefault(value => value.Reference == reference), cancellationToken);

    private async ValueTask<CalibrationGovernanceQueryResult<T>> ReadGovernanceAsync<T>(CommandInvocation invocation,
        Func<IReadOnlyList<object>, T?> project, CancellationToken cancellationToken) where T : class
    {
        if (Interlocked.Increment(ref _calibrationGovernanceQueries) > 16)
        {
            Interlocked.Decrement(ref _calibrationGovernanceQueries);
            return new(false, "CalibrationGovernanceQueryCapacityExceeded");
        }
        try
        {
            if (_authorization is null || _audit is not SqliteCommandStore store)
                return new(false, "CalibrationGovernanceUnavailable");
            var reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            if (reason is not null) return new(false, reason);
            var query = await store.ReadCalibrationGovernanceAsync(cancellationToken).ConfigureAwait(false);
            if (!query.Available) return new(false, query.ReasonCode);
            var records = query.Records.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray();
            var value = project(records);
            reason = await _authorization.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
            return reason is not null ? new(false, reason) : value is null ? new(false, "CalibrationGovernanceRecordNotFound") :
                new(true, "CalibrationGovernanceRecordAvailable", value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "CalibrationGovernanceQueryUnavailable"); }
        finally { Interlocked.Decrement(ref _calibrationGovernanceQueries); }
    }

    public async ValueTask<CalibrationGovernanceQueryResult<CalibrationProfileValiditySnapshot>> GetValidityAsync(
        CalibrationProfileReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        var query = await ReadGovernanceAsync(invocation, values =>
        {
            var profile = CalibrationGovernanceProjection.Profile(values, reference);
            var policy = profile is null ? null : CalibrationGovernanceProjection.Policy(values, profile.AcceptancePolicy);
            return profile is null || policy is null ? null : new ValidityInputs(profile, policy,
                values.OfType<PhysicalCalibrationVerificationRecord>().Where(value => value.Profile == reference).ToArray());
        }, cancellationToken).ConfigureAwait(false);
        if (!query.Available || query.Value is null) return new(false, query.ReasonCode);
        var inputs = query.Value;
        var role = inputs.Profile.Content.Requirement.LogicalCameraRole;
        var camera = await GetSetupAsync(role, invocation, cancellationToken).ConfigureAwait(false);
        var imaging = await GetImagingSetupAsync(role, invocation, cancellationToken).ConfigureAwait(false);
        // A second read prevents joining an old binding with a newer physical revision.
        var cameraAfter = await GetSetupAsync(role, invocation, cancellationToken).ConfigureAwait(false);
        var compatibility = camera.Available && imaging.Available && cameraAfter.Available &&
            camera.Snapshot?.Binding == cameraAfter.Snapshot?.Binding &&
            camera.Snapshot?.Requested == cameraAfter.Snapshot?.Requested &&
            camera.Snapshot?.Effective == cameraAfter.Snapshot?.Effective
            ? CalibrationValidityProjector.Compatibility(inputs.Profile.Content, cameraAfter.Snapshot, imaging.Current)
            : CalibrationCompatibilityState.Unavailable;
        var authorized = await _authorization!.CheckCalibrationGovernanceQueryAuthorizationAsync(invocation, cancellationToken)
            .ConfigureAwait(false);
        if (authorized is not null) return new(false, authorized);
        return new(true, "CalibrationValidityAvailable", CalibrationValidityProjector.Project(inputs.Profile, inputs.Policy,
            inputs.Verifications, compatibility, _authorization.CalibrationGovernanceUtcNow));
    }

    private sealed record ValidityInputs(PublishedCalibrationProfileVersion Profile, CalibrationAcceptancePolicy Policy,
        IReadOnlyList<PhysicalCalibrationVerificationRecord> Verifications);
}
