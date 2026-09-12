using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

internal sealed record OutboxSendResult(OutboxFailureCategory? Failure, string? ReasonCode,
    OutboxAcceptanceVerifier.VerifiedAcceptanceClaim? Acceptance = null);

/// <summary>
/// One physical transport invocation. Semantic timeout revokes success authority; the worker
/// must retain this route's slot until PhysicalCompletion, including cancellation callbacks.
/// Other routes have independent bounded slots, so a stuck BestEffort receiver cannot occupy
/// a Required route's sender. No connection, SQLite or runtime lock is held across transport code.
/// </summary>
internal sealed class OutboxSendOwner : IDisposable
{
    private readonly OutboxDelivery _delivery;
    private readonly OutboxTransportBinding _binding;
    private readonly Guid _attempt;
    private readonly Guid _epoch;
    private readonly OutboxAttemptAuthority _authority;
    private readonly object _retirementSync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task<OutboxTransportObservation> _physical;
    private Task _cancellationCompletion = Task.CompletedTask;
    private int _retired;
    private int _disposed;

    internal OutboxSendOwner(OutboxDelivery delivery, OutboxTransportBinding binding, Guid attemptId, Guid epoch,
        TimeSpan timeout, CancellationToken stop)
    {
        if (delivery.Payload is null) throw new ArgumentException("OutboxPayloadMissing", nameof(delivery));
        if (delivery.Route.ContentHash != binding.Route.ContentHash)
            throw new ArgumentException("OutboxTransportRouteMismatch", nameof(binding));
        _delivery = delivery; _binding = binding; _attempt = attemptId; _epoch = epoch;
        _authority = new(timeout, stop); // The fixed deadline starts before physical dispatch.
        // Task.Run also isolates a synchronously blocking or throwing plugin invocation.
        _physical = Task.Run(async () =>
        {
            if (!_authority.IsCurrent)
                return new OutboxTransportObservation(OutboxFailureCategory.UnknownOutcome, "OutboxAttemptExpiredBeforeSend");
            try { _binding.RequireCurrentContract(); }
            catch (InvalidOperationException)
            { return new OutboxTransportObservation(OutboxFailureCategory.Permanent, "OutboxAdapterContractMismatch"); }
            return await _binding.Transport.SendAsync(_delivery, _attempt, _cancellation.Token).ConfigureAwait(false);
        });
    }

    internal Task PhysicalCompletion => ObservePhysicalAsync();
    private async Task ObservePhysicalAsync()
    {
        try { await _physical.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        Task cancellation;
        lock (_retirementSync) cancellation = _cancellationCompletion;
        await cancellation.ConfigureAwait(false);
    }

    internal async Task<OutboxSendResult> ObserveAsync(CancellationToken stop)
    {
        try
        {
            var observed = await _physical.WaitAsync(_authority.Remaining, stop).ConfigureAwait(false);
            if (!_authority.IsCurrent) throw new TimeoutException();
            if (observed is null) return new(OutboxFailureCategory.Permanent, "OutboxTransportObservationMissing");
            if (observed.Failure is { } failure) return new(failure, observed.ReasonCode);
            var bytes = observed.CopyAcceptance();
            if (bytes is null) return new(OutboxFailureCategory.Permanent, "OutboxAcceptanceMissing");
            try
            {
                var claim = OutboxAcceptanceVerifier.CreateClaim(_delivery, _attempt, _epoch, bytes, _authority);
                return new(null, null, claim);
            }
            catch (InvalidOperationException) when (!_authority.IsCurrent)
            { Retire(); return new(OutboxFailureCategory.UnknownOutcome, "OutboxAcceptanceDeadlineExceeded"); }
            catch (InvalidOperationException)
            { return new(OutboxFailureCategory.Permanent, "OutboxAcceptanceRejected"); }
        }
        catch (TimeoutException)
        { Retire(); return new(OutboxFailureCategory.UnknownOutcome, "OutboxSendTimeout"); }
        catch (OperationCanceledException)
        { Retire(); return new(OutboxFailureCategory.UnknownOutcome, "OutboxSendCancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Retire();
            // Exceptions cannot prove that the receiver did not process a request. Keep that
            // uncertainty; only a typed adapter observation can establish a permanent rejection.
            return new(OutboxFailureCategory.UnknownOutcome, "OutboxTransportException");
        }
    }

    internal void Retire()
    {
        _authority.Retire();
        lock (_retirementSync)
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            // User cancellation callbacks may block synchronously. Their task is owned and
            // counts toward physical retirement instead of blocking the worker or Runtime lock.
            _cancellationCompletion = Task.Run(() =>
            {
                try { _cancellation.Cancel(); }
                catch (AggregateException) { }
            });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Retire();
        lock (_retirementSync)
        {
            if (_physical.IsCompleted && _cancellationCompletion.IsCompleted)
            { _cancellation.Dispose(); _authority.Dispose(); }
            else _ = DisposeAfterPhysicalAsync();
        }
    }
    private async Task DisposeAfterPhysicalAsync()
    {
        await PhysicalCompletion.ConfigureAwait(false);
        _cancellation.Dispose();
        _authority.Dispose();
    }
}
