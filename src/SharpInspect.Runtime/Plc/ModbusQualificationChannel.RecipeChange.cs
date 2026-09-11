using System.Buffers.Binary;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Controller-owned Recipe Change request fields read from the dedicated block. Request
/// identity is the controller epoch plus this dedicated request sequence; it never reuses
/// or derives from the cycle sequence. Stability, initial-zero, and duplicate-observation
/// handling remain with the caller.
/// </summary>
internal sealed record ModbusRecipeChangeControllerSignals(
    bool Request,
    bool Acknowledgement,
    uint RequestSequence,
    uint SelectionCode);

/// <summary>
/// Runtime-owned Recipe Change response fields read back from the dedicated block. Outcome
/// and Reason codes are opaque transport values here: the caller owns their meaning, the
/// activation state machine, audit, and recovery behaviour.
/// </summary>
internal sealed record ModbusRecipeChangeRuntimeSignals(
    bool ResponseValid,
    ushort Outcome,
    ushort Reason,
    uint RequestSequence,
    uint SelectionCode);

internal sealed partial class ModbusQualificationChannel
{
    internal Task WriteRecipeChangeOwnedStateAsync(bool ready, bool busy, bool valid, bool fault,
        bool violation, CancellationToken token) => WriteStateCoreAsync(ready, busy, valid, fault, violation, token, requireOwner: true);
    /// <summary>
    /// Reads the controller-owned Recipe Change request block (FC03). The read reports the
    /// current wire fields only; it never resets, latches, or interprets them.
    /// </summary>
    internal Task<ModbusRecipeChangeControllerSignals> ReadRecipeChangeControllerAsync(
        CancellationToken cancellationToken = default) =>
        ReadRecipeChangeControllerCoreAsync(cancellationToken);

    /// <summary>
    /// Reads the runtime-owned Recipe Change response block (FC03) for acknowledgement and
    /// return-to-initial observation. Outcome and Reason stay opaque; no protocol-meaning
    /// check, sequence comparison, or field clearing is performed here.
    /// </summary>
    internal Task<ModbusRecipeChangeRuntimeSignals> ReadRecipeChangeRuntimeAsync(
        CancellationToken cancellationToken = default) =>
        ReadRecipeChangeRuntimeCoreAsync(cancellationToken);

    /// <summary>
    /// Writes the runtime-owned Recipe Change response block (FC16) through the same genuine
    /// owned-request send fence as every other production write. It never borrows cycle write
    /// authority and never writes a controller-owned register; a closed or revoked send
    /// authority fails before any request reaches the wire.
    /// </summary>
    internal Task WriteRecipeChangeResponseAsync(bool responseValid, ushort outcome, ushort reason,
        uint requestSequence, uint selectionCode, CancellationToken cancellationToken) =>
        WriteRecipeChangeResponseCoreAsync(responseValid, outcome, reason, requestSequence,
            selectionCode, cancellationToken);

    private ModbusRecipeChangeBinding RequireRecipeChangeBinding()
    {
        if (!_production || _profile is not ModbusProductionProfile { RecipeChange: { } binding })
            throw new InvalidOperationException("ModbusRecipeChangeBindingRequired");
        return binding;
    }

    private async Task<ModbusRecipeChangeControllerSignals> ReadRecipeChangeControllerCoreAsync(
        CancellationToken cancellationToken)
    {
        var binding = RequireRecipeChangeBinding();
        var body = await ExecuteRequestAsync(
            ReadHoldingRegistersFunction,
            BuildReadRequest(binding.ControllerStartAddress,
                ModbusRecipeChangeBinding.ControllerRegisterCount),
            expectedMbapLength: checked((ushort)(3 + ModbusRecipeChangeBinding.ControllerRegisterCount * 2)),
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (body.Length != 2 + ModbusRecipeChangeBinding.ControllerRegisterCount * 2 ||
                body[0] != ReadHoldingRegistersFunction ||
                body[1] != ModbusRecipeChangeBinding.ControllerRegisterCount * 2)
                throw ProtocolFailure("ModbusRecipeChangeControllerReadResponseInvalid");

            return new ModbusRecipeChangeControllerSignals(
                ReadBooleanRegister(body, 2),
                ReadBooleanRegister(body, 4),
                BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(6, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(10, 4)));
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task<ModbusRecipeChangeRuntimeSignals> ReadRecipeChangeRuntimeCoreAsync(
        CancellationToken cancellationToken)
    {
        var binding = RequireRecipeChangeBinding();
        var body = await ExecuteRequestAsync(
            ReadHoldingRegistersFunction,
            BuildReadRequest(binding.RuntimeStartAddress,
                ModbusRecipeChangeBinding.RuntimeRegisterCount),
            expectedMbapLength: checked((ushort)(3 + ModbusRecipeChangeBinding.RuntimeRegisterCount * 2)),
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (body.Length != 2 + ModbusRecipeChangeBinding.RuntimeRegisterCount * 2 ||
                body[0] != ReadHoldingRegistersFunction ||
                body[1] != ModbusRecipeChangeBinding.RuntimeRegisterCount * 2)
                throw ProtocolFailure("ModbusRecipeChangeRuntimeReadResponseInvalid");

            return new ModbusRecipeChangeRuntimeSignals(
                ReadBooleanRegister(body, 2),
                BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6, 2)),
                BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4)));
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task WriteRecipeChangeResponseCoreAsync(bool responseValid, ushort outcome,
        ushort reason, uint requestSequence, uint selectionCode, CancellationToken cancellationToken)
    {
        var binding = RequireRecipeChangeBinding();
        if (_startOwnedRequest is null)
            throw new InvalidOperationException("ModbusRecipeChangeRequestAuthorityRequired");

        var registers = new ushort[ModbusRecipeChangeBinding.RuntimeRegisterCount];
        registers[0] = responseValid ? (ushort)1 : (ushort)0;
        registers[1] = outcome;
        registers[2] = reason;
        registers[3] = (ushort)(requestSequence >> 16);
        registers[4] = (ushort)requestSequence;
        registers[5] = (ushort)(selectionCode >> 16);
        registers[6] = (ushort)selectionCode;

        var body = await ExecuteRequestAsync(
            WriteMultipleRegistersFunction,
            BuildWriteMultipleRequest(binding.RuntimeStartAddress, registers),
            expectedMbapLength: 6,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWriteMultipleResponse(body, binding.RuntimeStartAddress,
                ModbusRecipeChangeBinding.RuntimeRegisterCount);
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }
}
