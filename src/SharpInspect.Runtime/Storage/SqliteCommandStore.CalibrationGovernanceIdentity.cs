using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<IdentityWriteResult> UpdateCalibrationGovernanceCommandAsync(CalibrationGovernanceCommand command,
        Func<IdentityAuthorityState, CalibrationGovernanceCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    private CalibrationGovernanceCommandState ReadCalibrationGovernanceCommandState(sqlite3 database,
        CalibrationGovernanceCommand command, StoreDeadline deadline)
    {
        if (_options.CalibrationGovernance is null)
            return new(Array.Empty<CalibrationGovernanceStoredEvent>(), Array.Empty<object>(), null, false);
        CalibrationSessionEvidence? Load(Guid id)
        {
            var row = ReadCalibrationSessionRows(database, id, deadline).SingleOrDefault();
            return row is null ? null : BuildEvidence(database, row, deadline);
        }
        var stored = ReadCalibrationGovernanceLedger(database, _options.CalibrationGovernance, deadline,
            (entries, loader) =>
            {
                CalibrationGovernanceProjection.ValidateRecords(entries.Select(value =>
                    CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray(), loader);
                return null;
            }, Load);
        var records = stored.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray();
        var candidate = command switch
        {
            EvaluateCalibrationCandidateCommand evaluate => evaluate.Candidate,
            PublishCalibrationProfileCommand publish => publish.Candidate,
            _ => null
        };
        return new(stored, records, candidate is null ? null : Load(candidate.SessionId), true);
    }

    private void AppendGovernanceIdentityMutation(sqlite3 database, IdentityUpdate update,
        CalibrationGovernanceCommandState state, CalibrationGovernanceCommand command, StoreDeadline deadline)
    {
        var mutation = update.CalibrationGovernance!;
        var metadata = CalibrationGovernanceProjection.Metadata(mutation.Record);
        if (update.CommandFacts is not { Count: 1 } || update.Events.Count != 1 ||
            update.CommandFacts[0].Disposition != CommandDisposition.Accepted ||
            update.CommandFacts[0].CorrelationId != metadata.OperationId ||
            update.Events[0].OperationId != metadata.OperationId ||
            metadata.Position != state.Records.Count + 1L)
            throw new InvalidOperationException("CalibrationGovernanceIdentityMutationMismatch");
        ValidateGovernanceCommandBinding(command, mutation.Record, update.Events[0], update.CommandFacts[0]);
        var next = state.Records.Append(mutation.Record).ToArray();
        CalibrationGovernanceProjection.ValidateRecords(next, id =>
        {
            var row = ReadCalibrationSessionRows(database, id, deadline).SingleOrDefault();
            return row is null ? null : BuildEvidence(database, row, deadline);
        });
        _ = AppendCalibrationGovernance(database, new CalibrationGovernanceAppendRequest(
            CalibrationGovernanceCodec.Kind(mutation.Record), metadata.OperationId,
            CalibrationGovernanceCodec.ContentHash(mutation.Record), CalibrationGovernanceCodec.Encode(mutation.Record),
            update.CommandFacts[0].EventId, update.Events[0].EventId, state.Events.LastOrDefault()?.AuditHash), deadline);
    }

    internal static void ValidateGovernanceCommandBinding(CalibrationGovernanceCommand command,
        object record, IdentityAuditEvent authorization, CommandAuditFact fact)
    {
        var metadata = CalibrationGovernanceProjection.Metadata(record);
        var fieldsMatch = (command, record) switch
        {
            (PublishCalibrationAcceptancePolicyCommand value, CalibrationAcceptancePolicyRevision revision) =>
                revision.Policy.Reference == value.Policy.Reference && revision.Previous == value.ExpectedPrevious,
            (EvaluateCalibrationCandidateCommand value, CalibrationPolicyEvaluationRecord evaluation) =>
                evaluation.Candidate == value.Candidate && evaluation.Policy == value.Policy,
            (PublishCalibrationProfileCommand value, PublishedCalibrationProfileVersion profile) =>
                profile.Reference.ProfileId == value.ProfileId && profile.Previous == value.ExpectedHead &&
                profile.SourceCandidate == value.Candidate && profile.Evaluation == value.Evaluation,
            (RecordPhysicalCalibrationVerificationCommand value, PhysicalCalibrationVerificationRecord verification) =>
                verification.Profile == value.Profile && verification.Submission.ContentHash == value.Submission.ContentHash,
            _ => false
        };
        if (!fieldsMatch || metadata.OperationId != command.CorrelationId ||
            authorization.Kind != IdentityEventKind.CalibrationGovernanceActionAuthorized ||
            authorization.ActionTargetId != command.AuthorizationTarget ||
            authorization.OperationId != command.CorrelationId ||
            authorization.CommandCorrelationId != command.CorrelationId ||
            authorization.BoundCommandCorrelationId != command.CorrelationId ||
            authorization.PrincipalId != metadata.Actor.PrincipalId ||
            authorization.ActorPrincipalId != metadata.Actor.PrincipalId ||
            authorization.SessionId != metadata.Actor.SessionId ||
            authorization.AuthorizationRevision != metadata.Actor.AuthorizationRevision ||
            authorization.OccurredAtUtc != metadata.RecordedAtUtc ||
            fact.CorrelationId != command.CorrelationId || fact.Disposition != CommandDisposition.Accepted ||
            fact.Phase != CommandAuditPhase.Outcome ||
            fact.AuthenticatedHumanPrincipalId != metadata.Actor.PrincipalId.ToString("D") ||
            fact.ClaimedSessionId != metadata.Actor.SessionId ||
            fact.ClaimedStepUpGrantId != command.Invocation.StepUpGrantId ||
            authorization.StepUpGrantId != command.Invocation.StepUpGrantId ||
            command.Invocation.StepUpGrantId is null)
            throw new InvalidOperationException("CalibrationGovernanceCommandBindingMismatch");
    }
}

internal sealed record CalibrationGovernanceCommandState(IReadOnlyList<CalibrationGovernanceStoredEvent> Events,
    IReadOnlyList<object> Records, CalibrationSessionEvidence? Session, bool Enabled);
internal sealed record CalibrationGovernanceMutation(object Record);
