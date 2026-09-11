using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Controller-side observation of the Runtime-owned Production Arm Status block. The peer only
/// decodes the exact wire value the Runtime writes; it never fabricates an attempt, an
/// acknowledgement or a Ready. The optional hold gate lets one test freeze the physical
/// Runtime-ready register write inside its response so the boundary itself can be observed.
/// </summary>
internal sealed partial class ModbusQualificationTestServer
{
    private ModbusProductionArmStatusBinding? _productionArmStatusBinding;
    private readonly ConcurrentQueue<ModbusQualificationProductionArmStatusWrite> _productionArmStatusWrites = new();
    private bool _holdProductionReadyWrite;
    private TaskCompletionSource<bool> _productionReadyWriteEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _productionReadyWriteReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<ModbusQualificationProductionArmStatusWrite> ProductionArmStatusWrites =>
        _productionArmStatusWrites.ToArray();

    /// <summary>Arms the gate for the next physical Runtime-ready write (register 5 = 1).</summary>
    internal void HoldNextProductionReadyWrite()
    {
        lock (_stateSync)
        {
            _holdProductionReadyWrite = true;
            _productionReadyWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _productionReadyWriteReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal Task WaitForProductionReadyWriteHeldAsync(CancellationToken cancellationToken = default) =>
        _productionReadyWriteEntered.Task.WaitAsync(cancellationToken);

    internal void ReleaseProductionReadyWrite() => _productionReadyWriteReleased.TrySetResult(true);

    private bool TryWriteProductionArmStatus(ushort address, ushort count, byte[] data)
    {
        ModbusProductionArmStatusBinding? binding;
        lock (_stateSync) binding = _productionArmStatusBinding;
        if (binding is null || address != binding.RuntimeStartAddress ||
            count != ModbusProductionArmStatusBinding.RuntimeRegisterCount ||
            data.Length != ModbusProductionArmStatusBinding.RuntimeRegisterCount * 2)
            return false;
        var write = new ModbusQualificationProductionArmStatusWrite(address,
            ReadUInt16(data, 0) == 1, ReadUInt16(data, 2), ReadUInt16(data, 4), ReadUInt16(data, 6),
            ReadUInt16(data, 8), ReadCanonicalUuid(data, 10), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(26, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(30, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(34, 4)), ReadCanonicalUuid(data, 38),
            PhysicalProductionReady, data);
        _productionArmStatusWrites.Enqueue(write);
        return true;
    }

    private async Task HoldProductionReadyWriteAsync(bool productionReady, CancellationToken token)
    {
        TaskCompletionSource<bool>? entered = null;
        TaskCompletionSource<bool>? released = null;
        if (productionReady)
            lock (_stateSync)
            {
                if (!_holdProductionReadyWrite) return;
                _holdProductionReadyWrite = false;
                entered = _productionReadyWriteEntered;
                released = _productionReadyWriteReleased;
            }
        if (released is null) return;
        entered?.TrySetResult(true);
        await released.Task.WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the RFC 4122 canonical ("N") UUID byte order the Runtime writes, rather than
    /// <c>new Guid(byte[])</c>, whose first three fields assume little endian.
    /// </summary>
    private static Guid ReadCanonicalUuid(byte[] data, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = data[offset + 3];
        bytes[1] = data[offset + 2];
        bytes[2] = data[offset + 1];
        bytes[3] = data[offset];
        bytes[4] = data[offset + 5];
        bytes[5] = data[offset + 4];
        bytes[6] = data[offset + 7];
        bytes[7] = data[offset + 6];
        for (var index = 8; index < 16; index++) bytes[index] = data[offset + index];
        return new Guid(bytes);
    }

    internal sealed record ModbusQualificationProductionArmStatusWrite(ushort StartAddress,
        bool Valid, ushort Cause, ushort Outcome, ushort Reason, ushort BlockedGate, Guid RuntimeEpoch,
        uint ControllerEpoch, uint RequestSequence, uint SelectionCode, Guid AttemptId,
        bool PhysicalProductionReady, byte[] RegisterBytes);
}
