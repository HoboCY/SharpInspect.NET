using System.Buffers.Binary;
using System.Text;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Tests;

internal sealed partial class ModbusQualificationTestServer
{
    private ModbusPartIdentityReadPlan? _partIdentityPlan;
    private byte[]? _partIdentityBlock;
    private int _partIdentityReadCount;
    internal int PartIdentityReadCount => Volatile.Read(ref _partIdentityReadCount);
    internal Action? AfterPartIdentityBlockRead { get; set; }

    internal void MutatePartIdentityBlock(Action<byte[]> mutation)
    {
        lock (_stateSync)
        {
            var copy = (_partIdentityBlock ?? throw new InvalidOperationException("TestPartIdentityBlockMissing")).ToArray();
            mutation(copy);
            _partIdentityBlock = copy;
        }
    }

    internal void SetPartIdentity(uint revision, uint epoch, uint cycle, string? value, ushort state = 1)
    {
        lock (_stateSync)
        {
            var plan = _partIdentityPlan ?? throw new InvalidOperationException("TestPartIdentityPlanMissing");
            var bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            if (bytes.Length > plan.MaximumValueBytes) throw new ArgumentOutOfRangeException(nameof(value));
            var block = new byte[plan.RegisterCount * 2];
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0, 4), revision);
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4, 4), epoch);
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8, 4), cycle);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(12, 2), state);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(14, 2), checked((ushort)bytes.Length));
            bytes.CopyTo(block, 16);
            _partIdentityBlock = block;
        }
    }

    private bool TryReadPartIdentity(ModbusRequest request, out byte[] response)
    {
        response = Array.Empty<byte>();
        Action? after = null;
        lock (_stateSync)
        {
            if (_partIdentityPlan is not { } plan || request.Pdu.Length != 5 || request.Pdu[0] != 3 ||
                ReadUInt16(request.Pdu, 1) != plan.StartAddress) return false;
            var count = ReadUInt16(request.Pdu, 3);
            if (count != 2 && count != plan.RegisterCount)
            { response = ExceptionResponse(request, 3, 3); return true; }
            Interlocked.Increment(ref _partIdentityReadCount);
            var bytes = _partIdentityBlock ?? new byte[plan.RegisterCount * 2];
            var body = new byte[1 + count * 2];
            body[0] = checked((byte)(count * 2));
            Array.Copy(bytes, 0, body, 1, count * 2);
            response = Response(request, 3, body);
            if (count == plan.RegisterCount)
            { after = AfterPartIdentityBlockRead; AfterPartIdentityBlockRead = null; }
        }
        after?.Invoke();
        return true;
    }
}
