using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cycles;

internal enum InspectionCycleDeliveryFact
{
    PublicationPrepared, ResultValidPublished, ResultAckObserved, ResultValidCleared, AckReset
}

internal sealed class InspectionCycleAckTimeoutException : TimeoutException
{
    internal InspectionCycleAckTimeoutException() : base("QualificationResultAckTimeout") { }
}

internal sealed partial class InspectionCycleCoordinator<TPayload> where TPayload : class
{
    internal async Task PublishAndAcknowledgeAsync(InspectionCycleCommitReceipt<TPayload> receipt,
        PlcControllerCycle key, InspectionCycleRequestObserver observer, InspectionCycleOutputLatch output,
        Func<TPayload, CancellationToken, Task> writePayload,
        Func<InspectionCycleDeliveryFact, Task> record, TimeSpan acknowledgementTimeout,
        TimeSpan pollInterval, CancellationToken token)
    {
        if (Phase != InspectionCyclePhase.WritingPayload)
            throw new InvalidOperationException("InspectionCyclePublicationNotPrepared");
        observer.RequireHealthy();
        if (observer.Latest.Signals is not { ResultAck: false })
            throw new InvalidOperationException("QualificationPrematureResultAck");
        await record(InspectionCycleDeliveryFact.PublicationPrepared).ConfigureAwait(false);
        // 先写入完整不可变载荷；最终状态写入同时清除 Busy、升起 ResultValid，在此之前 ResultValid 保持 false。
        await writePayload(receipt.Payload, token).ConfigureAwait(false);
        observer.RequireHealthy();
        if (observer.Latest.Signals is not { ResultAck: false })
            throw new InvalidOperationException("QualificationPrematureResultAck");
        var highWindow = await observer.WriteAcknowledgementStateAsync(false,
            () => output.ChangeAsync(token, ready: false, busy: false, valid: true), token).ConfigureAwait(false);
        SetPhase(InspectionCyclePhase.AwaitAckHigh);
        await record(InspectionCycleDeliveryFact.ResultValidPublished).ConfigureAwait(false);
        await WaitForAckAsync(true, highWindow).ConfigureAwait(false);
        await record(InspectionCycleDeliveryFact.ResultAckObserved).ConfigureAwait(false);
        var lowWindow = await observer.WriteAcknowledgementStateAsync(true,
            () => output.ChangeAsync(token, valid: false), token).ConfigureAwait(false);
        SetPhase(InspectionCyclePhase.AwaitAckLow);
        await record(InspectionCycleDeliveryFact.ResultValidCleared).ConfigureAwait(false);
        await WaitForAckAsync(false, lowWindow).ConfigureAwait(false);
        await record(InspectionCycleDeliveryFact.AckReset).ConfigureAwait(false);

        async Task WaitForAckAsync(bool high, (long Sequence, long StartedAt) window)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                observer.RequireHealthy();
                var observation = observer.Latest;
                var signals = observation.Signals;
                if (signals is not null && signals.ResultAck == high &&
                    signals.ControllerEpoch == key.ControllerEpoch && signals.CycleSequence == key.CycleSequence &&
                    observation.AckSequence > window.Sequence &&
                    Elapsed(observation.AckObservedAt, window.StartedAt) <= acknowledgementTimeout)
                    return;
                if (Elapsed(Stopwatch.GetTimestamp(), window.StartedAt) >= acknowledgementTimeout)
                    throw new InspectionCycleAckTimeoutException();
                await Task.Delay(pollInterval, token).ConfigureAwait(false);
            }
        }

        static TimeSpan Elapsed(long current, long start) =>
            TimeSpan.FromSeconds((current - start) / (double)Stopwatch.Frequency);
    }
}
