using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;

using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ModbusQualificationTransportTests
{
    [Fact]
    public void V140_M01_ProfileCanonicalizesLoopbackAndBindsEndpointSeparately()
    {
        var first = Profile(1502, controller: 100, runtime: 200, address: "127.0.0.1");
        var equivalent = Profile(1502, controller: 100, runtime: 200, address: "127.0.0.1");
        var differentUnit = new ModbusQualificationProfile("T40.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 2, 100, 200, 1, Hash('A'),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None);

        Assert.Equal(first.EndpointBindingHash, equivalent.EndpointBindingHash);
        Assert.Equal(first.ContentHash, equivalent.ContentHash);
        Assert.NotEqual(first.EndpointBindingHash, differentUnit.EndpointBindingHash);
        Assert.NotEqual(first.ContentHash, differentUnit.ContentHash);
        Assert.Equal("127.0.0.1", first.LoopbackAddress);
        Assert.Equal(QualificationEvidenceCaptureMode.None, first.EvidenceMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusQualificationProfile("T40.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 1, 100, 200, 1, Hash('A'),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            (QualificationEvidenceCaptureMode)1));
        Assert.Throws<ArgumentException>(() => new ModbusQualificationProfile("T40.Modbus", "1", "Scenario.A",
            "localhost", 1502, 1, 100, 200, 1, Hash('A'),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusQualificationProfile("T40.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 0, 100, 200, 1, Hash('A'),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None));
    }

    [Fact]
    public async Task V140_M02_ReadUsesExactMbapAndAcceptsFragmentedFc03Response()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            Assert.Equal(1, request.UnitId);
            Assert.Equal(0x03, request.Pdu[0]);
            Assert.Equal((ushort)100, U16(request.Pdu, 1));
            Assert.Equal((ushort)6, U16(request.Pdu, 3));
            var data = new byte[12];
            PutU16(data, 0, 1);
            PutU16(data, 2, 0);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 0x11223344);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 0x55667788);
            await WriteFragmentedAsync(stream, Response(request, 0x03, new byte[] { 0x0C }.Concat(data).ToArray()),
                serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        var signals = await channel.ReadAsync(serverCancellation.Token);
        await server;

        Assert.True(signals.Trigger);
        Assert.False(signals.ResultAck);
        Assert.Equal(0x11223344u, signals.ControllerEpoch);
        Assert.Equal(0x55667788u, signals.CycleSequence);
    }

    [Fact]
    public async Task V140_M03_WriteStateUsesOneFc16AndNeverRaisesProductionReady()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            Assert.Equal(0x10, request.Pdu[0]);
            Assert.Equal((ushort)200, U16(request.Pdu, 1));
            Assert.Equal((ushort)6, U16(request.Pdu, 3));
            Assert.Equal(12, request.Pdu[5]);
            Assert.Equal(new ushort[] { 1, 1, 0, 0, 1, 0 }, Registers(request.Pdu, 6, 6));
            await WriteFragmentedAsync(stream, Response(request, 0x10,
                request.Pdu.AsSpan(1, 4).ToArray()), serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        await channel.WriteStateAsync(true, true, false, false, true, serverCancellation.Token);
        await server;
    }

    [Fact]
    public async Task V140_M04_ReadRuntimeStateUsesExactRuntimeBlockAndProductionReadyRemainsFalse()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            Assert.Equal(0x03, request.Pdu[0]);
            Assert.Equal((ushort)200, U16(request.Pdu, 1));
            Assert.Equal((ushort)6, U16(request.Pdu, 3));
            var data = new byte[12];
            PutU16(data, 0, 0);
            PutU16(data, 2, 1);
            PutU16(data, 4, 1);
            PutU16(data, 6, 0);
            PutU16(data, 8, 1);
            PutU16(data, 10, 0);
            await WriteFragmentedAsync(stream, Response(request, 0x03,
                new byte[] { 0x0C }.Concat(data).ToArray()), serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        var state = await channel.ReadRuntimeStateAsync(serverCancellation.Token);
        await server;

        Assert.False(state.QualificationReady);
        Assert.True(state.Busy);
        Assert.True(state.ResultValid);
        Assert.False(state.CycleFault);
        Assert.True(state.ProtocolViolation);
        Assert.False(state.ProductionReady);
    }

    [Fact]
    public async Task V140_M05_WriteSingleRegisterUsesExactFc06Echo()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            Assert.Equal(0x06, request.Pdu[0]);
            Assert.Equal((ushort)201, U16(request.Pdu, 1));
            Assert.Equal((ushort)1, U16(request.Pdu, 3));
            await WriteFragmentedAsync(stream, Response(request, 0x06, request.Pdu.AsSpan(1, 4).ToArray()),
                serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            channel.WriteSingleRegisterAsync(100, 1, serverCancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            channel.WriteSingleRegisterAsync(205, 1, serverCancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            channel.WriteSingleRegisterAsync(321, 0xCAFE, serverCancellation.Token));
        await channel.WriteSingleRegisterAsync(201, 1, serverCancellation.Token);
        await server;
    }

    [Fact]
    public async Task V140_M06_PayloadPreservesBytesAndSplitsFc16At123Registers()
    {
        var binding = BindingForRange(100, 130);
        var payload = new StationQualificationPayload(Guid.NewGuid(), new QualificationRunId(Guid.NewGuid()),
            Hash('C'), binding, 1, 2, ExecutionStatus.Success, InspectionDecision.Pass, null,
            new[] { new PlcRegisterSegment(100, Enumerable.Range(0, 260).Select(value => (byte)value).ToArray()) });
        var expectedWrites = PayloadChunks(payload).ToArray();
        Assert.True(expectedWrites.Length > 1);

        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            foreach (var expected in expectedWrites)
            {
                var request = await ReadRequestAsync(stream, serverCancellation.Token);
                Assert.Equal(0x10, request.Pdu[0]);
                Assert.Equal((ushort)expected.StartAddress, U16(request.Pdu, 1));
                Assert.Equal((ushort)expected.RegisterCount, U16(request.Pdu, 3));
                Assert.Equal(expected.RegisterCount * 2, request.Pdu[5]);
                Assert.Equal(expected.RegisterBytes, request.Pdu.Skip(6).ToArray());
                await WriteFragmentedAsync(stream, Response(request, 0x10,
                    request.Pdu.AsSpan(1, 4).ToArray()), serverCancellation.Token);
            }
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 1000, 2000));
        await channel.ConnectAsync(serverCancellation.Token);
        await channel.WritePayloadAsync(payload, serverCancellation.Token);
        await server;
    }

    [Fact]
    public async Task V140_M07_PayloadOverlappingControlBlockIsRejectedBeforeConnectOrWrite()
    {
        var binding = BindingForRange(100, 2);
        var payload = new StationQualificationPayload(Guid.NewGuid(), new QualificationRunId(Guid.NewGuid()),
            Hash('C'), binding, 1, 2, ExecutionStatus.Success, InspectionDecision.Pass, null,
            new[] { new PlcRegisterSegment(100, new byte[] { 0x00, 0x01, 0x00, 0x02 }) });
        await using var channel = new ModbusQualificationChannel(Profile(1502, 100, 200));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => channel.WritePayloadAsync(payload));
        Assert.Equal("ModbusQualificationPayloadOverlapsControlBlock", exception.Message);
    }

    [Fact]
    public async Task V140_M08_TidMismatchLatchesFaultAndNeverReplaysRequest()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            Interlocked.Increment(ref requests);
            var wrong = Response(request with { TransactionId = unchecked((ushort)(request.TransactionId + 1)) },
                0x03, new byte[] { 0x0C }.Concat(new byte[12]).ToArray());
            await WriteFragmentedAsync(stream, wrong, serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadAsync(serverCancellation.Token));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadAsync());
        Assert.Equal("ModbusQualificationChannelFaulted", second.Message);
        await server;
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task V140_M09_TransportCancellationLatchesUnknownRequestAndDisallowsRetry()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            _ = await ReadRequestAsync(stream, serverCancellation.Token);
            requestReceived.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200,
            transportTimeout: TimeSpan.FromMilliseconds(100)));
        await channel.ConnectAsync(serverCancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(serverCancellation.Token));
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadAsync());
        Assert.Equal("ModbusQualificationChannelFaulted", second.Message);
        serverCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server);
    }

    [Fact]
    public async Task V140_M10_RuntimeReadRejectsNonBooleanStateAndLatchesFault()
    {
        using var listener = StartListener(out var port);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(serverCancellation.Token);
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, serverCancellation.Token);
            var data = new byte[12];
            PutU16(data, 0, 2); // QualificationReady is a strict boolean register.
            await WriteFragmentedAsync(stream, Response(request, 0x03,
                new byte[] { 0x0C }.Concat(data).ToArray()), serverCancellation.Token);
        }, serverCancellation.Token);

        await using var channel = new ModbusQualificationChannel(Profile(port, 100, 200));
        await channel.ConnectAsync(serverCancellation.Token);
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.ReadRuntimeStateAsync(serverCancellation.Token));
        Assert.Equal("ModbusQualificationBooleanRegisterInvalid", invalid.Message);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadRuntimeStateAsync());
        Assert.Equal("ModbusQualificationChannelFaulted", second.Message);
        await server;
    }

    private static ModbusQualificationProfile Profile(int port, ushort controller, ushort runtime,
        string address = "127.0.0.1", byte unitId = 1, TimeSpan? transportTimeout = null) =>
        new("T40.Modbus", "1", "Scenario.A", address, port, unitId, controller, runtime, 1,
            Hash('A'), TimeSpan.FromMilliseconds(20), transportTimeout ?? TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), QualificationEvidenceCaptureMode.None);

    private static string Hash(char value) => new(value, 64);

    private static TestListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new TestListener(listener);
    }

    private static async Task<ModbusRequest> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var header = await ReadExactAsync(stream, 7, token);
        var length = U16(header, 4);
        Assert.InRange(length, (ushort)2, (ushort)254);
        var body = await ReadExactAsync(stream, length - 1, token);
        return new(U16(header, 0), header[6], body);
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

    private static async Task WriteFragmentedAsync(NetworkStream stream, byte[] frame, CancellationToken token)
    {
        await stream.WriteAsync(frame.AsMemory(0, 2), token);
        await stream.WriteAsync(frame.AsMemory(2), token);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static void PutU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);

    private static ushort[] Registers(byte[] bytes, int offset, int count)
    {
        var result = new ushort[count];
        for (var index = 0; index < count; index++) result[index] = U16(bytes, offset + index * 2);
        return result;
    }

    private static PlcResultContractBinding BindingForRange(int start, int count)
    {
        var schema = new AlgorithmResultSchema("T40.Modbus.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            Array.Empty<string>(), new OverlayContract("T40.Modbus.Overlay", "1", 1, 1, 1));
        var wire = new PlcWireEncoding(PlcWireRepresentation.UInt16, PlcByteOrder.BigEndian,
            PlcWordOrder.NotApplicable, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var contract = new PlcResultContract("T40.Modbus.Contract", "1", 4096, 512,
            new[] { new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch,
                new(start, count), wire) }, new[]
            { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash)) });
        var validation = new PlcResultSchemaValidation(contract, schema,
            new[] { new PlcResultValidationCheck("T40.Modbus.Binding", "transport", true, "Passed") });
        return new PlcResultContractBinding(new RecipeReference("T40.Modbus.Recipe", "1", Hash('B')),
            new AlgorithmIdentity("T40.Modbus.Algorithm", "1"), validation);
    }

    private static IEnumerable<ExpectedPayloadWrite> PayloadChunks(StationQualificationPayload payload)
    {
        foreach (var segment in payload.Segments)
        {
            var offset = 0;
            while (offset < segment.RegisterCount)
            {
                var count = Math.Min(123, segment.RegisterCount - offset);
                yield return new ExpectedPayloadWrite(segment.StartRegister + offset, count,
                    segment.RegisterBytes.Skip(offset * 2).Take(count * 2).ToArray());
                offset += count;
            }
        }
    }

    private sealed record ExpectedPayloadWrite(int StartAddress, int RegisterCount,
        byte[] RegisterBytes);

    private sealed record ModbusRequest(ushort TransactionId, byte UnitId, byte[] Pdu);

    private sealed class TestListener : IDisposable
    {
        private readonly TcpListener _listener;

        internal TestListener(TcpListener listener) => _listener = listener;

        internal Task<TcpClient> AcceptTcpClientAsync() => _listener.AcceptTcpClientAsync();

        public void Dispose() => _listener.Stop();
    }
}
