using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Plc;

/// <summary>Latched controller-owned values read from the qualification channel.</summary>
internal sealed record ModbusControllerSignals(
    bool Trigger,
    bool ResultAck,
    uint ControllerEpoch,
    uint CycleSequence);

/// <summary>Runtime-owned handshake state read from the qualification controller.</summary>
internal sealed record ModbusRuntimeSignals(
    bool QualificationReady,
    bool Busy,
    bool ResultValid,
    bool CycleFault,
    bool ProtocolViolation,
    bool ProductionReady);

/// <summary>
/// Narrow Modbus TCP transport for a registered qualification facility. The channel owns only
/// protocol I/O. It does not advance Runtime state, execute algorithms, or persist results.
/// </summary>
internal sealed class ModbusQualificationChannel : IAsyncDisposable
{
    private const byte ReadHoldingRegistersFunction = 0x03;
    private const byte WriteSingleRegisterFunction = 0x06;
    private const byte WriteMultipleRegistersFunction = 0x10;
    private const int MaximumTcpAduBytes = 260;
    private const int MaximumPduBytes = MaximumTcpAduBytes - 7;
    private const int MaximumWriteRegisters = 123;
    private const int ControlRegisterCount = 6;

    private readonly ModbusQualificationProfile _profile;
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly object _stateGate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _faulted;
    private bool _disposed;
    private ushort _transactionId;

    internal ModbusQualificationChannel(ModbusQualificationProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    internal Task ConnectAsync(CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(cancellationToken);

    internal Task<ModbusControllerSignals> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(cancellationToken);

    internal Task<ModbusRuntimeSignals> ReadRuntimeStateAsync(CancellationToken cancellationToken = default) =>
        ReadRuntimeStateCoreAsync(cancellationToken);

    internal Task WriteStateAsync(bool qualificationReady, bool busy, bool resultValid,
        bool cycleFault, bool protocolViolation, CancellationToken cancellationToken = default) =>
        WriteStateCoreAsync(qualificationReady, busy, resultValid, cycleFault,
            protocolViolation, cancellationToken);

    internal Task WritePayloadAsync(StationQualificationPayload payload,
        CancellationToken cancellationToken = default) =>
        WritePayloadCoreAsync(payload, cancellationToken);

    /// <summary>
    /// A narrow primitive for Runtime-owned boolean state registers only.
    /// Runtime state writes use one FC16 request through <see cref="WriteStateAsync"/>.
    /// </summary>
    internal Task WriteSingleRegisterAsync(ushort address, ushort value,
        CancellationToken cancellationToken = default) =>
        WriteSingleRegisterCoreAsync(address, value, cancellationToken);

    /// <summary>
    /// Validates every contract-owned address before a payload can reach the network.
    /// </summary>
    internal void ValidatePayloadBinding(PlcResultContractBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var ranges = CollectContractRanges(binding);
        if (ranges.Count == 0)
            throw new ArgumentException("ModbusQualificationContractRangesRequired", nameof(binding));

        ValidateNonOverlapping(ranges, "ModbusQualificationContractRangesOverlap");
        ValidateProfileExclusion(ranges);
    }

    public ValueTask DisposeAsync()
    {
        TcpClient? client;
        NetworkStream? stream;
        lock (_stateGate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            client = _client;
            stream = _stream;
            _client = null;
            _stream = null;
        }

        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TcpClient? candidate = null;
        try
        {
            lock (_stateGate)
            {
                EnsureUsableLocked();
                if (_stream is not null) return;
            }

            if (!IPAddress.TryParse(_profile.LoopbackAddress, out var address) || address is null)
                throw new InvalidOperationException("ModbusQualificationLoopbackAddressInvalid");

            candidate = new TcpClient(address.AddressFamily);
            var deadline = new MonotonicDeadline(_profile.TransportTimeout);
            using var ioToken = CreateIoToken(cancellationToken, deadline);
            await candidate.ConnectAsync(address, _profile.Port).WaitAsync(ioToken.Token)
                .ConfigureAwait(false);
            var connectedClient = candidate ?? throw new InvalidOperationException("ModbusQualificationClientUnavailable");
            var stream = connectedClient.GetStream();
            lock (_stateGate)
            {
                EnsureUsableLocked();
                _client = connectedClient;
                _stream = stream;
                candidate = null;
            }
        }
        catch (OperationCanceledException)
        {
            // No request was sent. A caller cancellation does not make a connected channel
            // uncertain and therefore does not latch a transport fault.
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException exception) when
            (exception.Message == "ModbusQualificationChannelFaulted")
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Connection establishment has not written a Modbus request. Keep the channel
            // reusable for an explicit caller retry; all post-connect request failures latch.
            throw new InvalidOperationException("ModbusQualificationConnectFailed", exception);
        }
        finally
        {
            try { candidate?.Dispose(); } catch { }
            _transportGate.Release();
        }
    }

    private async Task<ModbusControllerSignals> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var body = await ExecuteRequestAsync(
            ReadHoldingRegistersFunction,
            BuildReadRequest(_profile.ControllerStartAddress, ControlRegisterCount),
            expectedMbapLength: 15,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (body.Length != 14 || body[0] != ReadHoldingRegistersFunction || body[1] != 12)
                throw ProtocolFailure("ModbusQualificationReadResponseLengthInvalid");

            var trigger = ReadBooleanRegister(body, 2);
            var resultAck = ReadBooleanRegister(body, 4);
            var controllerEpoch = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(6, 4));
            var cycleSequence = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(10, 4));
            return new ModbusControllerSignals(trigger, resultAck, controllerEpoch, cycleSequence);
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task<ModbusRuntimeSignals> ReadRuntimeStateCoreAsync(CancellationToken cancellationToken)
    {
        var body = await ExecuteRequestAsync(
            ReadHoldingRegistersFunction,
            BuildReadRequest(_profile.RuntimeStartAddress, ControlRegisterCount),
            expectedMbapLength: 15,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (body.Length != 14 || body[0] != ReadHoldingRegistersFunction || body[1] != 12)
                throw ProtocolFailure("ModbusQualificationRuntimeReadResponseLengthInvalid");

            return new ModbusRuntimeSignals(
                ReadBooleanRegister(body, 2),
                ReadBooleanRegister(body, 4),
                ReadBooleanRegister(body, 6),
                ReadBooleanRegister(body, 8),
                ReadBooleanRegister(body, 10),
                ReadBooleanRegister(body, 12));
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task WriteStateCoreAsync(bool qualificationReady, bool busy, bool resultValid,
        bool cycleFault, bool protocolViolation, CancellationToken cancellationToken)
    {
        var registers = new ushort[ControlRegisterCount];
        registers[0] = qualificationReady ? (ushort)1 : (ushort)0;
        registers[1] = busy ? (ushort)1 : (ushort)0;
        registers[2] = resultValid ? (ushort)1 : (ushort)0;
        registers[3] = cycleFault ? (ushort)1 : (ushort)0;
        registers[4] = protocolViolation ? (ushort)1 : (ushort)0;
        registers[5] = 0; // ProductionReady is intentionally hard-coded unavailable.

        var body = await ExecuteRequestAsync(
            WriteMultipleRegistersFunction,
            BuildWriteMultipleRequest(_profile.RuntimeStartAddress, registers),
            expectedMbapLength: 6,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWriteMultipleResponse(body, _profile.RuntimeStartAddress, ControlRegisterCount);
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task WriteSingleRegisterCoreAsync(ushort address, ushort value,
        CancellationToken cancellationToken)
    {
        if (address < _profile.RuntimeStartAddress || address >= _profile.RuntimeEndAddressExclusive ||
            value > 1 || address == _profile.RuntimeStartAddress + 5 && value != 0)
            throw new ArgumentException("ModbusQualificationRuntimeRegisterOwnershipViolation");
        var body = await ExecuteRequestAsync(
            WriteSingleRegisterFunction,
            BuildWriteSingleRequest(address, value),
            expectedMbapLength: 6,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (body.Length != 5 || body[0] != WriteSingleRegisterFunction ||
                BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2)) != address ||
                BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3, 2)) != value)
                throw ProtocolFailure("ModbusQualificationSingleWriteEchoMismatch");
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task WritePayloadCoreAsync(StationQualificationPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayloadBinding(payload.Binding);
        var contractRanges = CollectContractRanges(payload.Binding);

        var payloadRegisterCount = 0;
        var payloadByteCount = 0;
        foreach (var segment in payload.Segments)
        {
            ValidatePayloadSegment(segment, contractRanges);
            payloadRegisterCount = checked(payloadRegisterCount + segment.RegisterCount);
            payloadByteCount = checked(payloadByteCount + segment.RegisterBytes.Count);
        }

        if (payloadByteCount > payload.Binding.Contract.MaximumPayloadBytes ||
            payloadRegisterCount > payload.Binding.Contract.MaximumRegisterCount)
            throw new ArgumentException("ModbusQualificationPayloadCapacityExceeded", nameof(payload));

        foreach (var segment in payload.Segments)
        {
            var offset = 0;
            while (offset < segment.RegisterCount)
            {
                var registerCount = Math.Min(MaximumWriteRegisters, segment.RegisterCount - offset);
                var bytes = new byte[registerCount * 2];
                for (var index = 0; index < bytes.Length; index++)
                    bytes[index] = segment.RegisterBytes[offset * 2 + index];
                var address = checked(segment.StartRegister + offset);
                var body = await ExecuteRequestAsync(
                    WriteMultipleRegistersFunction,
                    BuildWriteMultipleRequest(address, bytes, registerCount),
                    expectedMbapLength: 6,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    ValidateWriteMultipleResponse(body, address, registerCount);
                }
                catch (ModbusProtocolException exception)
                {
                    FaultChannel();
                    throw new InvalidOperationException(exception.Message, exception);
                }
                offset += registerCount;
            }
        }
    }

    private async Task<byte[]> ExecuteRequestAsync(byte function, byte[] pdu,
        ushort expectedMbapLength, CancellationToken cancellationToken)
    {
        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requestStarted = false;
        try
        {
            NetworkStream stream;
            lock (_stateGate)
            {
                EnsureUsableLocked();
                stream = _stream ?? throw new InvalidOperationException("ModbusQualificationNotConnected");
            }

            var transactionId = NextTransactionId();
            var frame = BuildFrame(transactionId, _profile.UnitId, pdu);
            var deadline = new MonotonicDeadline(_profile.TransportTimeout);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            requestStarted = true;
            using (var writeToken = CreateIoToken(cancellationToken, deadline))
                await stream.WriteAsync(frame.AsMemory(), writeToken.Token).ConfigureAwait(false);

            var header = new byte[7];
            await ReadExactAsync(stream, header, deadline, cancellationToken).ConfigureAwait(false);
            ValidateMbapHeader(header, transactionId, _profile.UnitId);
            var mbapLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            if (mbapLength is < 2 or > MaximumPduBytes + 1)
                throw ProtocolFailure("ModbusQualificationMbapLengthInvalid");

            var body = new byte[mbapLength - 1];
            await ReadExactAsync(stream, body, deadline, cancellationToken).ConfigureAwait(false);
            if (body.Length >= 2 && body[0] == (byte)(function | 0x80))
                throw ProtocolFailure("ModbusQualificationExceptionResponse");
            if (body.Length != expectedMbapLength - 1 || body.Length == 0 || body[0] != function)
                throw ProtocolFailure("ModbusQualificationFunctionOrLengthMismatch");
            return body;
        }
        catch (OperationCanceledException)
        {
            if (requestStarted) FaultChannel();
            throw;
        }
        catch (ModbusProtocolException exception)
        {
            FaultChannel();
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (InvalidOperationException exception) when
            (exception.Message is "ModbusQualificationChannelFaulted" or "ModbusQualificationNotConnected")
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            FaultChannel();
            throw new InvalidOperationException("ModbusQualificationTransportFailed", exception);
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private void ValidateProfileExclusion(IReadOnlyList<RegisterInterval> ranges)
    {
        var controller = new RegisterInterval(_profile.ControllerStartAddress,
            _profile.ControllerEndAddressExclusive);
        var runtime = new RegisterInterval(_profile.RuntimeStartAddress,
            _profile.RuntimeEndAddressExclusive);
        foreach (var range in ranges)
        {
            if (range.Overlaps(controller) || range.Overlaps(runtime))
                throw new ArgumentException("ModbusQualificationPayloadOverlapsControlBlock");
        }
    }

    private static void ValidatePayloadSegment(PlcRegisterSegment segment,
        IReadOnlyList<RegisterInterval> contractRanges)
    {
        if (segment.RegisterCount < 1 || segment.RegisterCount > ushort.MaxValue + 1 ||
            segment.StartRegister + segment.RegisterCount > 65536)
            throw new ArgumentException("ModbusQualificationPayloadSegmentInvalid");

        var interval = new RegisterInterval(segment.StartRegister,
            checked(segment.StartRegister + segment.RegisterCount));
        var cursor = interval.Start;
        foreach (var range in contractRanges)
        {
            if (range.End <= cursor) continue;
            if (range.Start > cursor) break;
            cursor = Math.Max(cursor, range.End);
            if (cursor >= interval.End) return;
        }
        throw new ArgumentException("ModbusQualificationPayloadSegmentOutsideContract");
    }

    private static List<RegisterInterval> CollectContractRanges(PlcResultContractBinding binding)
    {
        var ranges = new List<RegisterInterval>();
        foreach (var field in binding.Contract.FrameworkFields)
            ranges.Add(ToInterval(field.RegisterRange));

        var schemaMap = binding.Contract.SchemaMaps.SingleOrDefault(map =>
            map.ResultSchema.Id == binding.ResultSchema.Id &&
            map.ResultSchema.Version == binding.ResultSchema.Version &&
            map.ResultSchema.ContentHash == binding.ResultSchema.ContentHash);
        if (schemaMap is null)
            throw new ArgumentException("ModbusQualificationBindingSchemaMapMissing", nameof(binding));

        foreach (var mapping in schemaMap.Measurements)
        {
            if (mapping.Disposition == PlcMeasurementDisposition.Excluded)
                continue;
            if (mapping.RegisterRange is null)
                throw new ArgumentException("ModbusQualificationMappedRangeRequired");
            ranges.Add(ToInterval(mapping.RegisterRange));
            if (mapping.OptionalAbsence?.ValidityField is { } validity)
                ranges.Add(ToInterval(validity.RegisterRange));
        }

        foreach (var constant in schemaMap.ConstantFields)
            ranges.Add(ToInterval(constant.RegisterRange));

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        return ranges;
    }

    private static RegisterInterval ToInterval(PlcRegisterRange range) =>
        new(range.StartRegister, range.EndRegisterExclusive);

    private static void ValidateNonOverlapping(IReadOnlyList<RegisterInterval> ranges, string reason)
    {
        for (var index = 1; index < ranges.Count; index++)
            if (ranges[index - 1].End > ranges[index].Start)
                throw new ArgumentException(reason);
    }

    private static bool ReadBooleanRegister(byte[] body, int offset)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset, 2));
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw ProtocolFailure("ModbusQualificationBooleanRegisterInvalid")
        };
    }

    private static void ValidateWriteMultipleResponse(byte[] body, int startAddress, int registerCount)
    {
        if (body.Length != 5 || body[0] != WriteMultipleRegistersFunction ||
            BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2)) != startAddress ||
            BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3, 2)) != registerCount)
            throw ProtocolFailure("ModbusQualificationMultipleWriteEchoMismatch");
    }

    private static byte[] BuildReadRequest(ushort startAddress, int registerCount)
    {
        var pdu = new byte[5];
        pdu[0] = ReadHoldingRegistersFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), checked((ushort)registerCount));
        return pdu;
    }

    private static byte[] BuildWriteSingleRequest(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = WriteSingleRegisterFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value);
        return pdu;
    }

    private static byte[] BuildWriteMultipleRequest(int startAddress, ushort[] registers)
    {
        var bytes = new byte[registers.Length * 2];
        for (var index = 0; index < registers.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2, 2), registers[index]);
        return BuildWriteMultipleRequest(startAddress, bytes, registers.Length);
    }

    private static byte[] BuildWriteMultipleRequest(int startAddress, byte[] bytes, int registerCount)
    {
        if (startAddress is < 0 or > ushort.MaxValue || registerCount is < 1 or > MaximumWriteRegisters ||
            bytes.Length != registerCount * 2)
            throw new ArgumentOutOfRangeException(nameof(registerCount));
        var pdu = new byte[6 + bytes.Length];
        pdu[0] = WriteMultipleRegistersFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), checked((ushort)startAddress));
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), checked((ushort)registerCount));
        pdu[5] = checked((byte)bytes.Length);
        bytes.CopyTo(pdu, 6);
        return pdu;
    }

    private static byte[] BuildFrame(ushort transactionId, byte unitId, byte[] pdu)
    {
        if (pdu.Length is < 1 or > MaximumPduBytes)
            throw new ArgumentException("ModbusQualificationPduLengthInvalid", nameof(pdu));
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)(pdu.Length + 1)));
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static void ValidateMbapHeader(byte[] header, ushort transactionId, byte unitId)
    {
        if (header.Length != 7 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)) != transactionId ||
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != 0 || header[6] != unitId)
            throw ProtocolFailure("ModbusQualificationMbapHeaderMismatch");
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer,
        MonotonicDeadline deadline, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            using var ioToken = CreateIoToken(cancellationToken, deadline);
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ioToken.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("ModbusQualificationConnectionClosed");
            offset += read;
        }
    }

    private static CancellationTokenSource CreateIoToken(CancellationToken caller,
        MonotonicDeadline deadline)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) linked.Cancel();
        else linked.CancelAfter(remaining);
        return linked;
    }

    private ushort NextTransactionId()
    {
        unchecked
        {
            _transactionId++;
            return _transactionId;
        }
    }

    private void EnsureUsableLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModbusQualificationChannel));
        if (_faulted) throw new InvalidOperationException("ModbusQualificationChannelFaulted");
    }

    private void FaultChannel()
    {
        TcpClient? client;
        NetworkStream? stream;
        lock (_stateGate)
        {
            if (_disposed) return;
            _faulted = true;
            client = _client;
            stream = _stream;
            _client = null;
            _stream = null;
        }

        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
    }

    private static ModbusProtocolException ProtocolFailure(string reason) =>
        new(reason);

    private readonly record struct RegisterInterval(int Start, int End)
    {
        internal bool Overlaps(RegisterInterval other) => Start < other.End && other.Start < End;
    }

    private sealed class MonotonicDeadline
    {
        private readonly long _deadlineTimestamp;

        internal MonotonicDeadline(TimeSpan timeout)
        {
            var ticks = timeout.TotalSeconds * Stopwatch.Frequency;
            _deadlineTimestamp = Stopwatch.GetTimestamp() + Math.Max(1L, checked((long)ticks));
        }

        internal TimeSpan Remaining
        {
            get
            {
                var remaining = _deadlineTimestamp - Stopwatch.GetTimestamp();
                if (remaining <= 0) return TimeSpan.Zero;
                var seconds = remaining / (double)Stopwatch.Frequency;
                return TimeSpan.FromSeconds(seconds);
            }
        }
    }

    private sealed class ModbusProtocolException : Exception
    {
        internal ModbusProtocolException(string reason) : base(reason) { }
    }
}
