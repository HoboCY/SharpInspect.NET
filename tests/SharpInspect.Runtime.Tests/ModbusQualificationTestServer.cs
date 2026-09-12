using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// A bounded controller-owned Modbus peer for the schema-26 runtime tests.  It
/// only answers protocol requests and exposes the controller input/output state;
/// it does not call Runtime transitions or manufacture qualification events.
/// </summary>
internal sealed partial class ModbusQualificationTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _readyWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _payloadWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _payloadRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _controllerLowObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _resultValidHigh =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ackHigh =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ackLow =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _controllerSample =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _initialRuntimeReadEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _initialRuntimeReadReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateSync = new();
    private readonly ConcurrentQueue<ModbusQualificationWrite> _writes = new();
    private readonly ConcurrentQueue<ModbusQualificationStateWrite> _stateWrites = new();
    private readonly Task _acceptLoop;
    private NetworkStream? _stream;
    private ModbusQualificationProfile? _profile;
    private bool _trigger;
    private bool _resultAck;
    private uint _controllerEpoch;
    private uint _cycleSequence;
    private uint _lastControllerSampleEpoch;
    private uint _lastControllerSampleSequence;
    private bool _lastControllerSampleTrigger;
    private uint _expectedControllerSampleEpoch;
    private uint _expectedControllerSampleSequence;
    private bool _expectedControllerSampleTrigger = true;
    private bool _controllerSampleExpected;
    private bool _qualificationReady;
    private bool _productionReady;
    private bool _productionPeer;
    private bool _busy;
    private bool _resultValid;
    private bool _cycleFault;
    private bool _protocolViolation;
    private bool _disconnect;
    private bool _dropNextPayloadResponse;
    private bool _holdInitialRuntimeRead;
    private bool _initialRuntimeReadHeld;
    private int _disposed;
    private int _requestCount;
    private int _connectionCount;

    private ModbusQualificationTestServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    internal int Port { get; }
    internal int ReadyWriteCount { get; private set; }
    internal int ResultValidHighCount { get; private set; }
    internal int ResultValidLowCount { get; private set; }
    internal int AckHighCount { get; private set; }
    internal int AckLowCount { get; private set; }
    internal int ProductionReadyWriteCount { get; private set; }
    internal int RequestCount => Volatile.Read(ref _requestCount);
    internal int ConnectionCount => Volatile.Read(ref _connectionCount);
    internal uint RaiseTriggerEpoch { get; private set; }
    internal uint RaiseTriggerSequence { get; private set; }
    internal bool AutoAcknowledge { get; set; } = true;
    internal bool AutoClearAcknowledge { get; set; } = true;
    internal bool HoldFirstPayloadWrite { get; set; } = true;
    internal bool HoldInitialRuntimeRead
    {
        get { lock (_stateSync) return _holdInitialRuntimeRead; }
        set { lock (_stateSync) _holdInitialRuntimeRead = value; }
    }
    internal bool DropNextPayloadResponse
    {
        get { lock (_stateSync) return _dropNextPayloadResponse; }
        set { lock (_stateSync) _dropNextPayloadResponse = value; }
    }
    internal IReadOnlyList<ModbusQualificationWrite> Writes => _writes.ToArray();
    internal IReadOnlyList<ModbusQualificationStateWrite> StateWrites => _stateWrites.ToArray();

    internal static ModbusQualificationTestServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ModbusQualificationTestServer(listener);
    }

    internal ModbusQualificationProfile CreateProfile(TraceStoragePolicySnapshot snapshot,
        ushort controllerStartAddress = 100, ushort runtimeStartAddress = 200,
        TimeSpan? transportTimeout = null, TimeSpan? acknowledgementTimeout = null)
    {
        var profile = new ModbusQualificationProfile("V140.Modbus.Qualification", "1",
            "V137.Scenario.A", "127.0.0.1", Port, 1, controllerStartAddress,
            runtimeStartAddress, snapshot.Version, snapshot.ContentHash,
            TimeSpan.FromMilliseconds(20), transportTimeout ?? TimeSpan.FromSeconds(2),
            acknowledgementTimeout ?? TimeSpan.FromSeconds(2),
            QualificationEvidenceCaptureMode.None);
        lock (_stateSync) _profile = profile;
        return profile;
    }

    internal Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
        _readyWrite.Task.WaitAsync(cancellationToken);

    internal Task WaitForPayloadWriteAsync(CancellationToken cancellationToken = default) =>
        _payloadWrite.Task.WaitAsync(cancellationToken);

    internal Task WaitForResultValidAsync(CancellationToken cancellationToken = default) =>
        _resultValidHigh.Task.WaitAsync(cancellationToken);

    internal Task WaitForAckHighAsync(CancellationToken cancellationToken = default) =>
        _ackHigh.Task.WaitAsync(cancellationToken);

    internal Task WaitForAckLowAsync(CancellationToken cancellationToken = default) =>
        _ackLow.Task.WaitAsync(cancellationToken);

    internal Task WaitForControllerLowObservedAsync(CancellationToken cancellationToken = default) =>
        _controllerLowObserved.Task.WaitAsync(cancellationToken);

    internal Task WaitForControllerSampleAsync(uint controllerEpoch, uint cycleSequence,
        CancellationToken cancellationToken = default, bool trigger = true)
    {
        lock (_stateSync)
        {
            _expectedControllerSampleEpoch = controllerEpoch;
            _expectedControllerSampleSequence = cycleSequence;
            _expectedControllerSampleTrigger = trigger;
            _controllerSampleExpected = true;
            _controllerSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_lastControllerSampleTrigger == trigger &&
                _lastControllerSampleEpoch == controllerEpoch &&
                _lastControllerSampleSequence == cycleSequence)
                _controllerSample.TrySetResult(true);
            return _controllerSample.Task.WaitAsync(cancellationToken);
        }
    }

    internal Task WaitForInitialRuntimeReadAsync(CancellationToken cancellationToken = default) =>
        _initialRuntimeReadEntered.Task.WaitAsync(cancellationToken);

    internal void ReleaseInitialRuntimeRead() => _initialRuntimeReadReleased.TrySetResult(true);

    internal void SetInitialControllerAck(bool value)
    {
        lock (_stateSync) _resultAck = value;
    }

    internal void ArmControllerLowObservation()
    {
        lock (_stateSync)
        {
            _controllerLowObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_trigger) _controllerLowObserved.TrySetResult(true);
        }
    }

    internal void ReleasePayloadWrite() => _payloadRelease.TrySetResult(true);

    internal void AcknowledgeResult() => SetControllerAck(true);
    internal void ResetResultAcknowledgement() => SetControllerAck(false);

    internal void RaiseTrigger(uint controllerEpoch, uint cycleSequence)
    {
        if (controllerEpoch == 0 || cycleSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(controllerEpoch));
        lock (_stateSync)
        {
            _controllerEpoch = controllerEpoch;
            _cycleSequence = cycleSequence;
            RaiseTriggerEpoch = controllerEpoch;
            RaiseTriggerSequence = cycleSequence;
            _trigger = true;
        }
    }

    internal void SetTrigger(bool value)
    {
        lock (_stateSync) _trigger = value;
    }

    internal void SetControllerCycle(uint controllerEpoch, uint cycleSequence)
    {
        if (controllerEpoch == 0 || cycleSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(controllerEpoch));
        lock (_stateSync)
        {
            _controllerEpoch = controllerEpoch;
            _cycleSequence = cycleSequence;
        }
    }

    internal void Disconnect()
    {
        NetworkStream? stream;
        lock (_stateSync)
        {
            _disconnect = true;
            stream = _stream;
        }
        try { stream?.Dispose(); } catch (ObjectDisposedException) { }
    }

    internal bool IsRuntimeClear
    {
        get
        {
            lock (_stateSync)
                return !_qualificationReady && !_busy && !_resultValid && !_cycleFault &&
                    !_protocolViolation && !_productionReady;
        }
    }

    internal bool RuntimeBusy
    {
        get { lock (_stateSync) return _busy; }
    }

    internal bool RuntimeResultValid
    {
        get { lock (_stateSync) return _resultValid; }
    }

    internal bool ControllerResultAck
    {
        get { lock (_stateSync) return _resultAck; }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync().WaitAsync(_shutdown.Token)
                    .ConfigureAwait(false);
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    Interlocked.Increment(ref _connectionCount);
                    lock (_stateSync) _stream = stream;
                    try { await ServeClientAsync(stream, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                    catch (EndOfStreamException) { }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    finally { lock (_stateSync) _stream = null; }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ServeClientAsync(NetworkStream stream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var header = await ReadExactAsync(stream, 7, token).ConfigureAwait(false);
            var length = ReadUInt16(header, 4);
            if (ReadUInt16(header, 2) != 0 || length is < 2 or > 254 || header[6] != 1)
                throw new InvalidOperationException("ModbusTestServerMbapInvalid");
            var pdu = await ReadExactAsync(stream, length - 1, token).ConfigureAwait(false);
            var request = new ModbusRequest(ReadUInt16(header, 0), header[6], pdu);
            Interlocked.Increment(ref _requestCount);
            var response = await HandleAsync(request, token).ConfigureAwait(false);
            if (ConsumeDropPayloadResponse(request))
            {
                Disconnect();
                return;
            }
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            if (IsDisconnected()) return;
        }
    }

    private async Task<byte[]> HandleAsync(ModbusRequest request, CancellationToken token)
    {
        var profile = _profile ?? throw new InvalidOperationException("ModbusTestServerProfileMissing");
        if (request.Pdu.Length == 0) throw new InvalidOperationException("ModbusTestServerPduMissing");
        if (request.Pdu[0] == 0x03 && request.Pdu.Length >= 5 &&
            ReadUInt16(request.Pdu, 1) == profile.RuntimeStartAddress)
        {
            var hold = false;
            lock (_stateSync)
            {
                if (_holdInitialRuntimeRead && !_initialRuntimeReadHeld)
                {
                    _initialRuntimeReadHeld = true;
                    hold = true;
                }
            }
            if (hold)
            {
                _initialRuntimeReadEntered.TrySetResult(true);
                await _initialRuntimeReadReleased.Task.WaitAsync(token).ConfigureAwait(false);
            }
        }
        return request.Pdu[0] switch
        {
            0x03 => ReadRegisters(request, profile),
            0x06 => WriteSingle(request, profile),
            0x10 => await WriteMultipleAsync(request, profile, token).ConfigureAwait(false),
            _ => ExceptionResponse(request, request.Pdu[0], 0x01)
        };
    }

    private byte[] ReadRegisters(ModbusRequest request, ModbusQualificationProfile profile)
    {
        if (TryReadRecipeChange(request, out var recipeChange)) return recipeChange;
        if (TryReadPartIdentity(request, out var identity)) return identity;
        if (TryReadCommunication(request, profile, out var communication)) return communication;
        if (request.Pdu.Length != 5 || ReadUInt16(request.Pdu, 3) != 6)
            return ExceptionResponse(request, 0x03, 0x03);
        var start = ReadUInt16(request.Pdu, 1);
        ushort[] values;
        lock (_stateSync)
        {
            if (start == profile.ControllerStartAddress)
            {
                if (!_trigger) _controllerLowObserved.TrySetResult(true);
                _lastControllerSampleTrigger = _trigger;
                _lastControllerSampleEpoch = _controllerEpoch;
                _lastControllerSampleSequence = _cycleSequence;
                if (_controllerSampleExpected && _trigger == _expectedControllerSampleTrigger &&
                    _controllerEpoch == _expectedControllerSampleEpoch &&
                    _cycleSequence == _expectedControllerSampleSequence)
                    _controllerSample.TrySetResult(true);
                values = new[] { _trigger ? (ushort)1 : (ushort)0, _resultAck ? (ushort)1 : (ushort)0,
                    (ushort)(_controllerEpoch >> 16), (ushort)_controllerEpoch,
                    (ushort)(_cycleSequence >> 16), (ushort)_cycleSequence };
            }
            else if (start == profile.RuntimeStartAddress)
                values = new[] { _qualificationReady ? (ushort)1 : (ushort)0, _busy ? (ushort)1 : (ushort)0,
                    _resultValid ? (ushort)1 : (ushort)0, _cycleFault ? (ushort)1 : (ushort)0,
                    _protocolViolation ? (ushort)1 : (ushort)0, _productionReady ? (ushort)1 : (ushort)0 };
            else return ExceptionResponse(request, 0x03, 0x02);
        }
        var body = new byte[1 + values.Length * 2];
        body[0] = checked((byte)(values.Length * 2));
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + i * 2, 2), values[i]);
        return Response(request, 0x03, body);
    }

    private byte[] WriteSingle(ModbusRequest request, ModbusQualificationProfile profile)
    {
        if (request.Pdu.Length != 5) return ExceptionResponse(request, 0x06, 0x03);
        var address = ReadUInt16(request.Pdu, 1);
        var value = ReadUInt16(request.Pdu, 3);
        _writes.Enqueue(new(address, new[] { checked((byte)(value >> 8)), checked((byte)value) }, 0x06));
        lock (_stateSync)
        {
            if (address == profile.RuntimeStartAddress && value == 0) _qualificationReady = false;
            if (address == profile.RuntimeStartAddress + 5 && value == 0) _productionReady = false;
        }
        return Response(request, 0x06, request.Pdu.AsSpan(1, 4).ToArray());
    }

    private async Task<byte[]> WriteMultipleAsync(ModbusRequest request,
        ModbusQualificationProfile profile, CancellationToken token)
    {
        if (request.Pdu.Length < 6) return ExceptionResponse(request, 0x10, 0x03);
        var address = ReadUInt16(request.Pdu, 1);
        var count = ReadUInt16(request.Pdu, 3);
        var bytes = request.Pdu[5];
        if (count is < 1 or > 123 || bytes != count * 2 || request.Pdu.Length != 6 + bytes)
            return ExceptionResponse(request, 0x10, 0x03);
        var data = request.Pdu.AsSpan(6, bytes).ToArray();
        _writes.Enqueue(new(address, data, 0x10));
        if (TryWriteProductionArmStatus(address, count, data)) { }
        else if (TryWriteRecipeChange(address, count, data)) { }
        else if (TryWriteHeartbeat(profile, address, count, data))
        {
            if (BeforeHeartbeatResponse is { } beforeResponse)
                await beforeResponse().ConfigureAwait(false);
        }
        else if (address == profile.RuntimeStartAddress && count == 6)
        {
            bool resultValid;
            bool productionReady;
            bool ackToSet = false;
            bool ackToClear = false;
            lock (_stateSync)
            {
                _qualificationReady = BooleanRegister(data, 0);
                _busy = BooleanRegister(data, 1);
                resultValid = BooleanRegister(data, 2);
                _cycleFault = BooleanRegister(data, 3);
                _protocolViolation = BooleanRegister(data, 4);
                productionReady = _productionReady = BooleanRegister(data, 5);
                if (productionReady) ProductionReadyWriteCount++;
                if (productionReady && _productionPeer) _readyWrite.TrySetResult(true);
                _stateWrites.Enqueue(new(_qualificationReady, _busy, resultValid,
                    _cycleFault, _protocolViolation, productionReady));
                if (_qualificationReady) { ReadyWriteCount++; _readyWrite.TrySetResult(true); }
                if (resultValid && !_resultValid)
                {
                    ResultValidHighCount++;
                    _resultValidHigh.TrySetResult(true);
                    ackToSet = AutoAcknowledge;
                }
                else if (!resultValid && _resultValid)
                {
                    ResultValidLowCount++;
                    ackToClear = _resultAck && AutoClearAcknowledge;
                }
                _resultValid = resultValid;
            }
            await HoldProductionReadyWriteAsync(productionReady, token).ConfigureAwait(false);
            if (ackToSet) SetControllerAck(true);
            if (ackToClear) SetControllerAck(false);
        }
        else
        {
            _payloadWrite.TrySetResult(true);
            if (HoldFirstPayloadWrite && _payloadRelease.Task.Status != TaskStatus.RanToCompletion)
                await _payloadRelease.Task.WaitAsync(token).ConfigureAwait(false);
        }
        return Response(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray());
    }

    private void SetControllerAck(bool value)
    {
        lock (_stateSync)
        {
            if (_resultAck == value) return;
            _resultAck = value;
            if (value) { AckHighCount++; _ackHigh.TrySetResult(true); }
            else { AckLowCount++; _ackLow.TrySetResult(true); }
        }
    }

    private bool IsDisconnected()
    {
        lock (_stateSync) return _disconnect;
    }

    private bool ConsumeDropPayloadResponse(ModbusRequest request)
    {
        if (request.Pdu.Length < 6 || request.Pdu[0] != 0x10) return false;
        var profile = _profile;
        if (profile is null || ReadUInt16(request.Pdu, 1) == profile.RuntimeStartAddress ||
            ReadUInt16(request.Pdu, 1) == profile.CommunicationBinding?.RuntimeStartAddress) return false;
        lock (_stateSync)
        {
            if (!_dropNextPayloadResponse) return false;
            _dropNextPayloadResponse = false;
            return true;
        }
    }

    private static bool BooleanRegister(byte[] data, int index)
    {
        var value = ReadUInt16(data, index * 2);
        if (value > 1) throw new InvalidOperationException("ModbusTestServerBooleanInvalid");
        return value == 1;
    }

    private static byte[] Response(ModbusRequest request, byte function, byte[] body)
    {
        var pdu = new byte[1 + body.Length];
        pdu[0] = function;
        body.CopyTo(pdu, 1);
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), request.TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)(pdu.Length + 1)));
        frame[6] = request.UnitId;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static byte[] ExceptionResponse(ModbusRequest request, byte function, byte code) =>
        Response(request, checked((byte)(function | 0x80)), new[] { code });

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length,
        CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return buffer;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _payloadRelease.TrySetResult(true);
        _shutdown.Cancel();
        _listener.Stop();
        Disconnect();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    internal sealed record ModbusQualificationWrite(ushort StartAddress, byte[] RegisterBytes,
        byte Function);

    internal sealed record ModbusQualificationStateWrite(bool QualificationReady, bool Busy,
        bool ResultValid, bool CycleFault, bool ProtocolViolation, bool ProductionReady);

    private sealed record ModbusRequest(ushort TransactionId, byte UnitId, byte[] Pdu);
}
