using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private static IdentityUpdate RejectStationQualificationCapacity(IdentityUpdate accepted, string reason)
    {
        var proposed = (StationQualificationTransactionResult)accepted.Result;
        var rejected = proposed with
        {
            Outcome = proposed.Outcome with { Disposition = CommandDisposition.Rejected, ReasonCode = reason },
            Header = proposed.Event!.Phase == StationQualificationSessionPhase.Admitted ? null : proposed.Header,
            Event = null,
            Accepted = false,
            CommandFact = null
        };
        return new(rejected, accepted.Events.Select(value => value with
        {
            Kind = IdentityEventKind.ManagementRejected, ReasonCode = reason, OperationId = null
        }).ToArray(), accepted.CommandFacts!.Select(value => value with
        {
            Disposition = CommandDisposition.Rejected, ReasonCode = reason
        }).ToArray());
    }
}
