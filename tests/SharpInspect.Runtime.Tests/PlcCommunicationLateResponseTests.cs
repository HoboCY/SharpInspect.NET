using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;
using Xunit;
namespace SharpInspect.Runtime.Tests;
public sealed class PlcCommunicationLateResponseTests
{
    [Fact]
    public async Task V141_L01_LateControllerResponseCannotReviveRetiredGeneration()
    {
        await using var peer = LateResponsePeer.Start();
        var policy = Policy();
        var profile = Profile(peer.Port, policy);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new ModbusQualificationChannel(profile);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            reason => revoked.TrySetResult(reason), _ => Task.CompletedTask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await channel.ConnectAsync(timeout.Token);
        await owner.SynchronizeAsync(channel, timeout.Token);
        await Task.Delay(350, timeout.Token);
        peer.HoldNextControllerResponse();
        var oldRead = owner.ReadAsync(channel, timeout.Token);
        await peer.ControllerResponseHeld.WaitAsync(timeout.Token);
        var revokedReason = await revoked.Task.WaitAsync(timeout.Token);
        Assert.Contains(revokedReason, new[] { "PlcControllerHeartbeatStale", "PlcRuntimeHeartbeatUnobserved" });
        await owner.RecoverAsync(channel, pending: false, timeout.Token);
        Assert.Equal(2, peer.ConnectionCount);
        Assert.Contains(health, value => value.ConnectionGeneration >= 2 &&
            value.ControllerEpoch == 42 && value.RecoveryRequired);
        peer.ReleaseHeldControllerResponse();
        await peer.LateResponseAttempted.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => oldRead.WaitAsync(timeout.Token));
        var postRecovery = health.Where(value => value.ConnectionGeneration >= 2).ToArray();
        Assert.NotEmpty(postRecovery);
        Assert.DoesNotContain(postRecovery, value => value.ControllerEpoch == 41);
        Assert.Equal(revokedReason, Assert.Throws<InvalidOperationException>(
            owner.RequireHealthy).Message);
    }
    [Fact]
    public async Task V141_L02_ReplayedRuntimeHeartbeatCannotSynchronize()
    {
        await using var peer = LateResponsePeer.Start(replayRuntimeHeartbeat: true);
        var policy = Policy();
        await using var channel = new ModbusQualificationChannel(Profile(peer.Port, policy));
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(Profile(peer.Port, policy), Guid.NewGuid(), health.Enqueue,
            _ => { }, _ => Task.CompletedTask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.ConnectAsync(timeout.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => owner.SynchronizeAsync(channel, timeout.Token));
        Assert.DoesNotContain(health, value => value.Healthy || value.RuntimeHeartbeatObserved);
    }
    private static PlcCommunicationPolicy Policy() => new("V141.LateResponse", "1", TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(10), 1, TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(2));
    private static ModbusQualificationProfile Profile(int port, PlcCommunicationPolicy policy) =>
        new("V141.Late.Modbus", "1", "Scenario.Late", "127.0.0.1", port, 1, 100, 200, 1,
            new string('A', 64), policy.PollInterval, policy.OperationTimeout, TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None, new ModbusCommunicationBinding(policy, 300, 400));
    private sealed class LateResponsePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<Task> _sessions = new();
        private readonly TaskCompletionSource<bool> _held = NewSignal();
        private readonly TaskCompletionSource<bool> _release = NewSignal();
        private readonly TaskCompletionSource<bool> _lateAttempted = NewSignal();
        private readonly bool _replayRuntimeHeartbeat;
        private bool _holdNext;
        private int _connectionCount;
        private uint _controllerHeartbeat = 100;
        private Guid _runtimeEpoch;
        private uint _runtimeHeartbeat;
        private LateResponsePeer(TcpListener listener, bool replayRuntimeHeartbeat)
        {
            _listener = listener;
            _replayRuntimeHeartbeat = replayRuntimeHeartbeat;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }
        internal int Port { get; }
        internal int ConnectionCount => Volatile.Read(ref _connectionCount);
        internal Task ControllerResponseHeld => _held.Task;
        internal Task LateResponseAttempted => _lateAttempted.Task;
        internal static LateResponsePeer Start(bool replayRuntimeHeartbeat = false)
        { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return new LateResponsePeer(listener, replayRuntimeHeartbeat); }
        internal void HoldNextControllerResponse() { lock (_gate) _holdNext = true; }
        internal void ReleaseHeldControllerResponse() => _release.TrySetResult(true);
        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().WaitAsync(_stop.Token)
                        .ConfigureAwait(false);
                    client.NoDelay = true;
                    var connection = Interlocked.Increment(ref _connectionCount);
                    var session = ServeAsync(client, connection);
                    lock (_sessions) _sessions.Add(session);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient client, int connection)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var header = await ReadExactAsync(stream, 7, _stop.Token).ConfigureAwait(false);
                        var length = U16(header, 4);
                        var pdu = await ReadExactAsync(stream, length - 1, _stop.Token).ConfigureAwait(false);
                        var response = await HandleAsync(new(header[6], U16(header, 0), pdu),
                            connection).ConfigureAwait(false);
                        await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
        private async Task<byte[]> HandleAsync(Request request, int connection)
        {
            var pdu = request.Pdu;
            if (pdu[0] == 0x10)
            {
                var address = U16(pdu, 1);
                var count = U16(pdu, 3);
                if (address == 400 && count == 10)
                {
                    lock (_gate)
                    {
                        _runtimeEpoch = new Guid(pdu.AsSpan(6, 16));
                        if (!_replayRuntimeHeartbeat || _runtimeHeartbeat == 0)
                            _runtimeHeartbeat = U32(pdu, 22);
                    }
                }
                return Response(request, 0x10, pdu.AsSpan(1, 4).ToArray());
            }
            if (pdu[0] != 0x03) return Response(request, (byte)(pdu[0] | 0x80), new byte[] { 1 });
            var start = U16(pdu, 1);
            var countRead = U16(pdu, 3);
            if (start == 102 && countRead == 2)
            {
                var data = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(data, connection == 1 ? 41u : 42u);
                return Registers(request, data);
            }
            if (start == 300 && countRead == 12) return Communication(request);
            if (start == 200 && countRead == 6)
                return Registers(request, new byte[12]);
            if (start == 100 && countRead == 6)
            {
                var hold = false;
                lock (_gate)
                {
                    hold = connection == 1 && _holdNext;
                    _holdNext = false;
                }
                if (hold)
                {
                    _held.TrySetResult(true);
                    await _release.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                    _lateAttempted.TrySetResult(true);
                }
                var epoch = connection == 1 ? 41u : 42u;
                return Controller(request, epoch);
            }
            return Response(request, 0x83, new byte[] { 2 });
        }
        private byte[] Communication(Request request)
        {
            uint heartbeat;
            Guid epoch;
            uint runtimeHeartbeat;
            lock (_gate)
            {
                heartbeat = ++_controllerHeartbeat;
                epoch = _runtimeEpoch;
                runtimeHeartbeat = _runtimeHeartbeat;
            }
            var data = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), heartbeat);
            epoch.ToByteArray().CopyTo(data, 4);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), runtimeHeartbeat);
            return Registers(request, data);
        }
        private static byte[] Controller(Request request, uint epoch)
        {
            var data = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), epoch);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 1);
            return Registers(request, data);
        }
        private static byte[] Registers(Request request, byte[] data) =>
            Response(request, 0x03, new[] { checked((byte)data.Length) }.Concat(data).ToArray());
        private static byte[] Response(Request request, byte function, byte[] body)
        {
            var pdu = new byte[body.Length + 1];
            pdu[0] = function;
            body.CopyTo(pdu, 1);
            var frame = new byte[pdu.Length + 7];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), request.TransactionId);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)(pdu.Length + 1)));
            frame[6] = request.UnitId;
            pdu.CopyTo(frame, 7);
            return frame;
        }
        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length,
            CancellationToken token)
        {
            var bytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset, length - offset), token)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return bytes;
        }
        private static ushort U16(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult(true); _stop.Cancel(); _listener.Stop();
            Task[] sessions;
            lock (_sessions) sessions = _sessions.ToArray();
            try { await Task.WhenAll(sessions).ConfigureAwait(false); }
            catch (Exception) { }
            _stop.Dispose();
        }
        private sealed record Request(byte UnitId, ushort TransactionId, byte[] Pdu);
    }
}
