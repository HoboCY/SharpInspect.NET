using System.Buffers.Binary;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Runtime-owned Production Arm Status observation written to the dedicated block. Cause,
/// Outcome, Reason, and Blocked Gate stay opaque transport values: the caller owns their
/// meaning, the arm transition, publication, audit, and recovery behaviour. A valid block
/// always identifies the Runtime epoch that published it and the exact arm attempt it
/// reports; an invalid block is exactly zero.
/// </summary>
internal sealed record ModbusProductionArmStatusSignals(
    bool Valid,
    ushort Cause,
    ushort Outcome,
    ushort Reason,
    ushort BlockedGate,
    Guid RuntimeEpoch,
    uint ControllerEpoch,
    uint RequestSequence,
    uint SelectionCode,
    Guid AttemptId);

internal sealed partial class ModbusQualificationChannel
{
    /// <summary>
    /// Writes the Runtime-owned Production Arm Status observation block (FC16) through the
    /// same genuine owned-request send fence and response validation as every other production
    /// write, on the same production connection. The block is a one-way observation: it
    /// carries no acknowledgement, request, activation, or execution authority, never asserts
    /// Ready, and never writes a controller-owned register or the dedicated Recipe Change
    /// response. An invalid block is written only as the exact all-zero value.
    /// </summary>
    internal async Task WriteProductionArmStatusAsync(ModbusProductionArmStatusSignals signals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var binding = RequireProductionArmStatusBinding();
        if (_startOwnedRequest is null)
            throw new InvalidOperationException("ModbusProductionArmStatusRequestAuthorityRequired");
        ValidateProductionArmStatus(signals);
        await WriteProductionArmStatusCoreAsync(binding, EncodeProductionArmStatus(signals),
            cancellationToken).ConfigureAwait(false);
    }

    private ModbusProductionArmStatusBinding RequireProductionArmStatusBinding()
    {
        if (!_production || _profile is not ModbusProductionProfile { ProductionArmStatus: { } binding })
            throw new InvalidOperationException("ModbusProductionArmStatusBindingRequired");
        return binding;
    }

    private static void ValidateProductionArmStatus(ModbusProductionArmStatusSignals signals)
    {
        if (signals.Valid)
        {
            if (signals.RuntimeEpoch == Guid.Empty || signals.AttemptId == Guid.Empty)
                throw new ArgumentException("ModbusProductionArmStatusIdentityRequired");
            return;
        }

        if (signals.Cause != 0 || signals.Outcome != 0 || signals.Reason != 0 ||
            signals.BlockedGate != 0 || signals.RuntimeEpoch != Guid.Empty ||
            signals.ControllerEpoch != 0 || signals.RequestSequence != 0 ||
            signals.SelectionCode != 0 || signals.AttemptId != Guid.Empty)
            throw new ArgumentException("ModbusProductionArmStatusInvalidBlockMustBeZero");
    }

    private static byte[] EncodeProductionArmStatus(ModbusProductionArmStatusSignals signals)
    {
        var bytes = new byte[ModbusProductionArmStatusBinding.RuntimeRegisterCount * 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2),
            signals.Valid ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), signals.Cause);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), signals.Outcome);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), signals.Reason);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), signals.BlockedGate);
        WriteCanonicalUuid(signals.RuntimeEpoch, bytes.AsSpan(10, 16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(26, 4), signals.ControllerEpoch);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(30, 4), signals.RequestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(34, 4), signals.SelectionCode);
        WriteCanonicalUuid(signals.AttemptId, bytes.AsSpan(38, 16));
        return bytes;
    }

    /// <summary>
    /// Writes the RFC 4122 canonical ("N") UUID byte order rather than
    /// <c>Guid.ToByteArray()</c>, whose first three fields are little endian.
    /// </summary>
    private static void WriteCanonicalUuid(Guid value, Span<byte> destination)
    {
        var bytes = value.ToByteArray();
        destination[0] = bytes[3];
        destination[1] = bytes[2];
        destination[2] = bytes[1];
        destination[3] = bytes[0];
        destination[4] = bytes[5];
        destination[5] = bytes[4];
        destination[6] = bytes[7];
        destination[7] = bytes[6];
        for (var index = 8; index < 16; index++)
            destination[index] = bytes[index];
    }

    private async Task WriteProductionArmStatusCoreAsync(ModbusProductionArmStatusBinding binding,
        byte[] bytes, CancellationToken cancellationToken)
    {
        var body = await ExecuteRequestAsync(
            WriteMultipleRegistersFunction,
            BuildWriteMultipleRequest(binding.RuntimeStartAddress, bytes,
                ModbusProductionArmStatusBinding.RuntimeRegisterCount),
            expectedMbapLength: 6,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWriteMultipleResponse(body, binding.RuntimeStartAddress,
                ModbusProductionArmStatusBinding.RuntimeRegisterCount);
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }
}
