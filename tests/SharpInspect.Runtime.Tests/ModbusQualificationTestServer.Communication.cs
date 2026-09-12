using System.Buffers.Binary;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Tests;

internal sealed partial class ModbusQualificationTestServer
{
    private uint _heartbeat = 1;
    private long _lastScan = Stopwatch.GetTimestamp();
    private Guid _receivedRuntimeEpoch;
    private uint _receivedRuntimeHeartbeat;
    private Guid _echoRuntimeEpoch;
    private uint _echoRuntimeHeartbeat;
    internal bool FreezeControllerHeartbeat { get; set; }
    internal bool FreezeRuntimeEcho { get; set; }
    internal Guid? RuntimeEchoEpochOverride { get; set; }
    internal bool ChangeEpochAfterNextHeartbeatRead { get; set; }
    internal Func<Task>? BeforeHeartbeatResponse { get; set; }
    internal Guid ReceivedRuntimeEpoch { get { lock (_stateSync) return _receivedRuntimeEpoch; } }

    internal void SeedRetainedRuntimeState(bool busy, bool valid, bool fault, bool violation)
    {
        lock (_stateSync)
        { _busy = busy; _resultValid = valid; _cycleFault = fault; _protocolViolation = violation; }
    }

    internal ModbusQualificationProfile CreateCommunicationProfile(TraceStoragePolicySnapshot snapshot,
        PlcCommunicationPolicy? policy = null) => CreateCommunicationProfile(snapshot.Version, snapshot.ContentHash, policy);

    internal ModbusQualificationProfile CreateCommunicationProfile(long traceVersion = 1, string? traceHash = null,
        PlcCommunicationPolicy? policy = null)
    {
        policy ??= new PlcCommunicationPolicy("V141.Communication", "1",
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var profile = new ModbusQualificationProfile("V141.Modbus.Qualification", "1",
            "V137.Scenario.A", "127.0.0.1", Port, 1, 100, 200, traceVersion,
            traceHash ?? new string('A', 64), policy.PollInterval, policy.OperationTimeout,
            TimeSpan.FromSeconds(10), QualificationEvidenceCaptureMode.None,
            new ModbusCommunicationBinding(policy, 300, 400));
        lock (_stateSync)
        {
            _profile = profile; _controllerEpoch = 61; _cycleSequence = 1;
        }
        return profile;
    }

    private bool TryReadCommunication(ModbusRequest request, ModbusQualificationProfile profile, out byte[] response)
    {
        response = Array.Empty<byte>();
        if (request.Pdu.Length == 5 && ReadUInt16(request.Pdu, 1) == profile.ControllerStartAddress + 2 &&
            ReadUInt16(request.Pdu, 3) == 2)
        {
            var epochBody = new byte[5]; epochBody[0] = 4;
            lock (_stateSync) BinaryPrimitives.WriteUInt32BigEndian(epochBody.AsSpan(1, 4), _controllerEpoch);
            response = Response(request, 0x03, epochBody);
            return true;
        }
        if (profile.CommunicationBinding is not { } binding || request.Pdu.Length != 5 ||
            ReadUInt16(request.Pdu, 1) != binding.ControllerStartAddress) return false;
        if (ReadUInt16(request.Pdu, 3) != 12) { response = ExceptionResponse(request, 0x03, 0x03); return true; }
        var body = new byte[25]; body[0] = 24;
        lock (_stateSync)
        {
            var now = Stopwatch.GetTimestamp();
            if (!FreezeControllerHeartbeat && (now - _lastScan) / (double)Stopwatch.Frequency >= .020)
            { _heartbeat++; _lastScan = now; }
            if (!FreezeRuntimeEcho)
            {
                _echoRuntimeEpoch = _receivedRuntimeEpoch;
                _echoRuntimeHeartbeat = _receivedRuntimeHeartbeat;
            }
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), _heartbeat);
            (RuntimeEchoEpochOverride ?? _echoRuntimeEpoch).ToByteArray().CopyTo(body, 5);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(21, 4), _echoRuntimeHeartbeat);
            if (ChangeEpochAfterNextHeartbeatRead)
            {
                ChangeEpochAfterNextHeartbeatRead = false;
                _controllerEpoch++; FreezeControllerHeartbeat = true;
            }
        }
        response = Response(request, 0x03, body);
        return true;
    }

    private bool TryWriteHeartbeat(ModbusQualificationProfile profile, ushort address, ushort count, byte[] data)
    {
        if (profile.CommunicationBinding is not { } binding || address != binding.RuntimeStartAddress) return false;
        if (count != 10) throw new InvalidOperationException("HeartbeatTupleMustBeAtomic");
        lock (_stateSync)
        {
            _receivedRuntimeEpoch = new Guid(data.AsSpan(0, 16));
            _receivedRuntimeHeartbeat = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        }
        return true;
    }
}
