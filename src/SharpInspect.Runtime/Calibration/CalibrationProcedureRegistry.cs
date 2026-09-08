using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Explicit registrations only. No assembly scan, filesystem lookup, or latest-version fallback.</summary>
public sealed class CalibrationProcedureRegistry
{
    private readonly IReadOnlyDictionary<(string Id, string Version), IRegisteredCalibrationProcedure> _procedures;
    private readonly SemaphoreSlim _inputValidationSlots = new(2, 2);
    private int _pendingInputValidations;
    internal CalibrationProcedureRegistry(IEnumerable<IRegisteredCalibrationProcedure> procedures)
    {
        var registered = new Dictionary<(string, string), IRegisteredCalibrationProcedure>();
        foreach (var procedure in procedures)
        {
            if (registered.Count == 16 || procedure is null ||
                !registered.TryAdd((procedure.Descriptor.Procedure.Id,
                    procedure.Descriptor.Procedure.Version), procedure))
                throw new ArgumentException("CalibrationProcedureRegistrationInvalid", nameof(procedures));
        }
        _procedures = registered;
    }

    internal IRegisteredCalibrationProcedure Resolve(CalibrationProcedureDescriptor expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!_procedures.TryGetValue((expected.Procedure.Id, expected.Procedure.Version), out var procedure))
            throw new InvalidOperationException("CalibrationProcedureExactVersionMissing");
        if (procedure.Descriptor.ContentHash != expected.ContentHash)
            throw new InvalidOperationException("CalibrationProcedureContentHashMismatch");
        return procedure;
    }

    internal async Task ValidateInputAsync(IRegisteredCalibrationProcedure procedure,
        CalibrationProcedureInputPayload payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _pendingInputValidations) > 16)
        {
            Interlocked.Decrement(ref _pendingInputValidations);
            throw new InvalidOperationException("CalibrationInputValidationCapacityExceeded");
        }
        var entered = false;
        var transferred = false;
        try
        {
            entered = await _inputValidationSlots.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("CalibrationInputValidationDeadlineExceeded");
            var actual = Task.Run(() => procedure.ValidateInput(payload), CancellationToken.None);
            _ = actual.ContinueWith(completed =>
            {
                _ = completed.Exception;
                _inputValidationSlots.Release();
                Interlocked.Decrement(ref _pendingInputValidations);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            transferred = true;
            await actual.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!transferred)
            {
                if (entered) _inputValidationSlots.Release();
                Interlocked.Decrement(ref _pendingInputValidations);
            }
        }
    }
}

public static class CalibrationProcedureServiceCollectionExtensions
{
    /// <summary>Registers a consumer-supplied typed procedure. Registration does not publish a calibration.</summary>
    public static IServiceCollection AddSharpInspectCalibrationProcedure<TInput>(
        this IServiceCollection services, ICalibrationProcedure<TInput> procedure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(procedure);
        services.AddSingleton<IRegisteredCalibrationProcedure>(new RegisteredCalibrationProcedure<TInput>(procedure));
        services.TryAddSingleton(provider => new CalibrationProcedureRegistry(
            provider.GetServices<IRegisteredCalibrationProcedure>()));
        return services;
    }
}

internal interface IRegisteredCalibrationProcedure
{
    CalibrationProcedureDescriptor Descriptor { get; }
    void ValidateInput(CalibrationProcedureInputPayload payload);
    ValueTask<CalibrationExtractionResult> ExtractAsync(CalibrationProcedureInputPayload payload,
        VisionFrame frame, Guid sessionId, Guid frameId, string sourceHash, CancellationToken cancellationToken);
    ValueTask<CalibrationProcedureComputationResult> ComputeAsync(CalibrationProcedureInputPayload payload,
        IReadOnlyList<CalibrationObservationInput> observations, CancellationToken cancellationToken);
}

internal sealed class RegisteredCalibrationProcedure<TInput> : IRegisteredCalibrationProcedure
{
    private readonly ICalibrationProcedure<TInput> _procedure;
    private readonly ICalibrationInputCodec<TInput> _codec;

    internal RegisteredCalibrationProcedure(ICalibrationProcedure<TInput> procedure)
    {
        _procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        Descriptor = procedure.Descriptor ?? throw new ArgumentException("CalibrationProcedureDescriptorMissing");
        _codec = procedure.InputCodec ?? throw new ArgumentException("CalibrationInputCodecMissing");
        ValidateRegistration();
    }
    public CalibrationProcedureDescriptor Descriptor { get; }

    public void ValidateInput(CalibrationProcedureInputPayload payload) => _ = Decode(payload);

    public async ValueTask<CalibrationExtractionResult> ExtractAsync(CalibrationProcedureInputPayload payload,
        VisionFrame frame, Guid sessionId, Guid frameId, string sourceHash, CancellationToken cancellationToken)
    {
        var input = Decode(payload);
        var result = await _procedure.ExtractAsync(new CalibrationExtractionContext<TInput>(
            frame, input, sessionId, frameId, sourceHash), cancellationToken).ConfigureAwait(false);
        ValidateUnchanged(payload, input);
        return result ?? throw new InvalidOperationException("CalibrationExtractionResultMissing");
    }

    public async ValueTask<CalibrationProcedureComputationResult> ComputeAsync(CalibrationProcedureInputPayload payload,
        IReadOnlyList<CalibrationObservationInput> observations, CancellationToken cancellationToken)
    {
        var input = Decode(payload);
        var result = await _procedure.ComputeAsync(new CalibrationComputationContext<TInput>(input, observations),
            cancellationToken).ConfigureAwait(false);
        ValidateUnchanged(payload, input);
        return result ?? throw new InvalidOperationException("CalibrationComputationResultMissing");
    }

    private TInput Decode(CalibrationProcedureInputPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateRegistration();
        if (payload.InputContract != Descriptor.InputContract)
            throw new InvalidOperationException("CalibrationProcedureInputContractMismatch");
        var input = _codec.Decode(payload.GetBytes());
        if (input is null) throw new InvalidOperationException("CalibrationProcedureInputMissing");
        ValidateUnchanged(payload, input);
        return input;
    }

    private void ValidateUnchanged(CalibrationProcedureInputPayload payload, TInput input)
    {
        ValidateRegistration();
        var canonical = _codec.Encode(input);
        if (!canonical.Span.SequenceEqual(payload.GetBytes()))
            throw new InvalidOperationException("CalibrationProcedureInputNotCanonical");
    }

    private void ValidateRegistration()
    {
        if (_procedure.Descriptor?.ContentHash != Descriptor.ContentHash ||
            !ReferenceEquals(_procedure.InputCodec, _codec) || _codec.InputContract != Descriptor.InputContract)
            throw new InvalidOperationException("CalibrationProcedureRegistrationChanged");
    }
}
