using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Single-use authority issued by the owning station after a non-waiting reservation.
/// Neither a public command nor a decoded audit context can create this capability.</summary>
internal sealed class PlcRecipeActivationCapability
{
    private int _state; // 0 issued, 1 consumed, 2 commit claimed, 3 revoked
    private readonly Action? _notifyRevocation;
    internal PlcRecipeActivationCapability(RecipeChangeRequestEvidence request, RecipeSelectionRevision selection,
        RecipeActivationRuntimeLease runtime, RecipeActivationReference? expectedActive, Action? notifyRevocation = null)
    {
        if (!runtime.Available || runtime.RuntimeEpoch != request.RuntimeEpoch ||
            selection.Reference != request.SelectionRevision || selection.Policy.Mode != RecipeSelectionMode.PlcRequestedActivation ||
            selection.Map is null || selection.Map.Reference != request.SelectionMap || selection.Policy.Reference != request.SelectionPolicy ||
            !selection.Map.Entries.Any(entry => entry.ContentHash == request.Target?.ContentHash))
            throw new InvalidOperationException("RecipeChangeCapabilityBindingInvalid");
        Request = request; Runtime = runtime; ExpectedActive = expectedActive;
        Context = request.ActivationContext();
        _notifyRevocation = notifyRevocation;
    }
    internal RecipeChangeRequestEvidence Request { get; }
    internal PlcRecipeActivationRequestContext Context { get; }
    internal RecipeActivationRuntimeLease Runtime { get; }
    internal RecipeActivationReference? ExpectedActive { get; }
    internal void Revoke()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is 2 or 3) return;
            if (Interlocked.CompareExchange(ref _state, 3, state) == state)
            { _notifyRevocation?.Invoke(); return; }
        }
    }
    internal string? RevocationFailure => Volatile.Read(ref _state) == 3 || Runtime.Token.IsCancellationRequested
        ? "RecipeActivationCancelled" : null;
    internal bool TryConsume(ActivateRecipeCommand command) => RevocationFailure is null && Matches(command) &&
        Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    internal string? TryClaimCommit(Func<string?> claimRuntime)
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1) return "RecipeActivationCancelled";
        // This is the cancellation linearization point, immediately before the existing
        // station commit claim. A later protocol fault cannot rewrite durable success.
        return claimRuntime();
    }
    private bool Matches(ActivateRecipeCommand command) => command.PlcRequestContext is { } context &&
        context.Matches(Context) && command.Invocation.Source == CommandSource.Integration &&
        command.Invocation.PrincipalId == SystemPrincipalId.PlcAdapter && command.Invocation.SessionId is null &&
        command.Invocation.StepUpGrantId is null && command.OperationId == Request.OperationId &&
        command.CorrelationId == Request.OperationId && command.ExpectedActive == ExpectedActive &&
        command.HistoricalSelection is null && command.CalibrationSelections.Count == 0;

    internal string? Check(ActivateRecipeCommand command, Guid epoch, RecipeActivationCommandState state)
    {
        if (RevocationFailure is { } revoked) return revoked;
        if (Volatile.Read(ref _state) != 1 || !Matches(command) || epoch != Context.RuntimeEpoch ||
            Runtime.RuntimeEpoch != epoch) return "RecipeChangeCapabilityInvalid";
        if (Runtime.GetBlocker() is { } blocker) return blocker;
        var selection = state.Selection;
        if (selection is null || selection.Reference != Request.SelectionRevision ||
            selection.Policy.Mode != RecipeSelectionMode.PlcRequestedActivation ||
            selection.Policy.Reference != Context.SelectionPolicy || selection.Map is null || selection.Map.Reference != Context.SelectionMap ||
            !selection.Map.Entries.Any(entry => entry.ContentHash == Request.Target!.ContentHash))
            return "RecipeChangeSelectionChanged";
        var events = state.RecipeChanges?.Where(value => value.Request.RequestIdentityHash == Request.RequestIdentityHash).ToArray();
        if (events is null || events.Count(value => value.Kind == RecipeChangeEventKind.RequestObserved) != 1 ||
            events.Single(value => value.Kind == RecipeChangeEventKind.RequestObserved).Request.ContentHash != Request.ContentHash ||
            events.Any(value => value.Kind is RecipeChangeEventKind.DecisionCommitted or RecipeChangeEventKind.ProtocolFault))
            return "RecipeChangeRequestNotAdmitted";
        return null;
    }
}
