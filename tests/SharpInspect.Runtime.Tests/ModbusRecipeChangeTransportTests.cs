using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Qualification;

using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded transport tests for the dedicated Recipe Change Handshake block. They exercise the
/// real loopback wire bytes and the existing channel guards; they do not assert any activation,
/// recipe-selection, arm, or audit behaviour, which stays with the caller.
/// </summary>
public sealed class ModbusRecipeChangeTransportTests
{
    [Fact]
    public void V146_R01_LegacyProfileHashesSurviveTheNewRecipeChangeOverload()
    {
        // Frozen by executing the published T45 assemblies and independently reproduced with
        // the documented hash-parts layout: a null recipe change keeps the v1/v2 algorithm.
        var policy = new PlcCommunicationPolicy("V143.Legacy.Policy", "1",
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var communication = new ModbusCommunicationBinding(policy, 100, 200);
        var plan = new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 600, 32);

        var legacy = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500));
        var legacyThroughOverload = new ModbusProductionProfile("V143.Legacy.Profile", "1",
            "127.0.0.1", 502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500),
            null, null);
        var identity = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500), plan);
        var identityThroughOverload = new ModbusProductionProfile("V143.Legacy.Profile", "1",
            "127.0.0.1", 502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500), plan, null);

        Assert.Equal("CA5C222F277000670327BDB357CAA9EDFEE140FEB57192F65FC9ED529D0A259F",
            legacy.ContentHash);
        Assert.Equal(legacy.ContentHash, legacyThroughOverload.ContentHash);
        Assert.Equal("3607148DF4F3C0C46B5750D150B6D088EF465C99DF9A83595154ABE2FBABB6DF",
            identity.ContentHash);
        Assert.Equal(identity.ContentHash, identityThroughOverload.ContentHash);
        Assert.Equal("88FF78F974BF1400FBBEB9F41D9133E5024DA2CEF3A47393AA9C882606F63932",
            legacy.EndpointBindingHash);
        Assert.Null(legacy.RecipeChange);
        Assert.Null(legacyThroughOverload.RecipeChange);
        Assert.Null(identityThroughOverload.RecipeChange);
    }

    [Fact]
    public void V146_R02_RecipeChangeBindingValidatesRangesOverlapAndExplicitTimeout()
    {
        var binding = new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromMilliseconds(250));

        Assert.Equal(6, binding.ControllerEndAddressExclusive - binding.ControllerStartAddress);
        Assert.Equal(7, binding.RuntimeEndAddressExclusive - binding.RuntimeStartAddress);
        Assert.Equal(binding.ContentHash, new ModbusRecipeChangeBinding(500, 520,
            TimeSpan.FromMilliseconds(250)).ContentHash);
        Assert.NotEqual(binding.ContentHash, new ModbusRecipeChangeBinding(501, 520,
            TimeSpan.FromMilliseconds(250)).ContentHash);
        Assert.NotEqual(binding.ContentHash, new ModbusRecipeChangeBinding(500, 521,
            TimeSpan.FromMilliseconds(250)).ContentHash);
        Assert.NotEqual(binding.ContentHash, new ModbusRecipeChangeBinding(500, 520,
            TimeSpan.FromMilliseconds(251)).ContentHash);

        Assert.Throws<ArgumentException>(() =>
            new ModbusRecipeChangeBinding(500, 503, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModbusRecipeChangeBinding(65531, 100, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModbusRecipeChangeBinding(100, 65530, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModbusRecipeChangeBinding(100, 200, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModbusRecipeChangeBinding(100, 200, TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1)));
        _ = new ModbusRecipeChangeBinding(100, 200, TimeSpan.FromMilliseconds(1));
        _ = new ModbusRecipeChangeBinding(65530, 100, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void V146_R03_ProfileRejectsRecipeChangeBlocksOverlappingAnyExistingBlock()
    {
        Assert.Throws<ArgumentException>(() => Profile(1502,
            new ModbusRecipeChangeBinding(100, 600, TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            new ModbusRecipeChangeBinding(600, 200, TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            new ModbusRecipeChangeBinding(305, 600, TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            new ModbusRecipeChangeBinding(600, 405, TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            new ModbusRecipeChangeBinding(600, 520, TimeSpan.FromSeconds(1)),
            new ModbusPartIdentityReadPlan("V146.Part", "1", 520, 32)));

        var bare = Profile(1502);
        var bound = Profile(1502, new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)));
        Assert.Null(bare.RecipeChange);
        Assert.NotNull(bound.RecipeChange);
        Assert.NotEqual(bare.ContentHash, bound.ContentHash);
        Assert.Equal(bound.ContentHash, Profile(1502,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1))).ContentHash);
        Assert.NotEqual(bound.ContentHash, Profile(1502,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(2))).ContentHash);
        Assert.NotEqual(bound.ContentHash, Profile(1502, bound.RecipeChange,
            new ModbusPartIdentityReadPlan("V146.Part", "1", 600, 32)).ContentHash);
    }

    [Fact]
    public void V146_R04_PayloadValidationExcludesBothRecipeChangeBlocks()
    {
        var profile = Profile(1502,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)));
        var channel = new ModbusQualificationChannel(profile);

        Assert.Equal("ModbusProductionPayloadOverlapsRecipeChangeBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(504, 2))).Message);
        Assert.Throws<ArgumentException>(() =>
            channel.ValidatePayloadBinding(BindingForRange(520, 1)));
        Assert.Throws<ArgumentException>(() =>
            channel.ValidatePayloadBinding(BindingForRange(525, 2)));
        Assert.Equal("ModbusQualificationPayloadOverlapsControlBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(100, 1))).Message);
        channel.ValidatePayloadBinding(BindingForRange(600, 1));
    }

    [Fact]
    public async Task V146_R05_RecipeChangeReadsUseDedicatedFc03FieldsWithoutWritingAnything()
    {
        await using var peer = new RecipeChangePeer(request => U16(request.Pdu, 1) == 500
            ? ReadResponse(request, 1, 0, 0x1122, 0x3344, 0x5566, 0x7788)
            : ReadResponse(request, 0, 7, 200, 0x0102, 0x0304, 0x0506, 0x0708));
        await using var channel = new ModbusQualificationChannel(Profile(peer.Port,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1))));
        await channel.ConnectAsync();

        var controller = await channel.ReadRecipeChangeControllerAsync();
        var runtime = await channel.ReadRecipeChangeRuntimeAsync();

        Assert.True(controller.Request);
        Assert.False(controller.Acknowledgement);
        Assert.Equal(0x11223344u, controller.RequestSequence);
        Assert.Equal(0x55667788u, controller.SelectionCode);
        Assert.False(runtime.ResponseValid);
        Assert.Equal((ushort)7, runtime.Outcome);
        Assert.Equal((ushort)200, runtime.Reason);
        Assert.Equal(0x01020304u, runtime.RequestSequence);
        Assert.Equal(0x05060708u, runtime.SelectionCode);

        Assert.Equal(2, peer.Requests.Count);
        Assert.All(peer.Requests, request => Assert.Equal((byte)0x03, request.Pdu[0]));
        Assert.Equal((ushort)500, U16(peer.Requests[0].Pdu, 1));
        Assert.Equal((ushort)6, U16(peer.Requests[0].Pdu, 3));
        Assert.Equal((ushort)520, U16(peer.Requests[1].Pdu, 1));
        Assert.Equal((ushort)7, U16(peer.Requests[1].Pdu, 3));
    }

    [Fact]
    public async Task V146_R06_RecipeChangeResponseWriteUsesFc16OnTheRuntimeBlockThroughTheFence()
    {
        await using var peer = new RecipeChangePeer(request =>
            Frame(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray()));
        var fenced = 0;
        await using var channel = new ModbusQualificationChannel(Profile(peer.Port,
                new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1))),
            startOwnedRequest: start =>
            {
                Interlocked.Increment(ref fenced);
                return start();
            });
        await channel.ConnectAsync();

        await channel.WriteRecipeChangeResponseAsync(true, 0x0102, 0x0304, 0xAABBCCDD, 0x00112233,
            CancellationToken.None);
        await channel.WriteRecipeChangeResponseAsync(false, 0, 0, 0, 0, CancellationToken.None);

        Assert.Equal(2, fenced);
        Assert.Equal(2, peer.Requests.Count);
        Assert.All(peer.Requests, request =>
        {
            Assert.Equal((byte)0x10, request.Pdu[0]);
            Assert.Equal((ushort)520, U16(request.Pdu, 1));
            Assert.Equal((ushort)7, U16(request.Pdu, 3));
            Assert.Equal(14, request.Pdu[5]);
        });
        Assert.Equal(new ushort[] { 1, 0x0102, 0x0304, 0xAABB, 0xCCDD, 0x0011, 0x2233 },
            Registers(peer.Requests[0].Pdu, 6, 7));
        Assert.Equal(new ushort[] { 0, 0, 0, 0, 0, 0, 0 }, Registers(peer.Requests[1].Pdu, 6, 7));
    }

    [Fact]
    public async Task V146_R07_MissingConfigurationOrSendAuthorityRejectsBeforeAnyIo()
    {
        await using var peer = new RecipeChangePeer(request => ReadResponse(request, 0, 0, 0, 0, 0, 0, 0));

        var policy = Policy();
        var qualification = new ModbusQualificationProfile("V146.Modbus.Qualification", "1",
            "V146.Scenario", "127.0.0.1", peer.Port, 1, 100, 200, 1, new string('A', 64),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None);
        await using (var channel = new ModbusQualificationChannel(qualification))
        {
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteRecipeChangeResponseAsync(true, 0, 0, 1, 1, CancellationToken.None)))
                .Message);
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.ReadRecipeChangeControllerAsync())).Message);
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.ReadRecipeChangeRuntimeAsync())).Message);
        }

        var unconfigured = Profile(peer.Port);
        await using (var channel = new ModbusQualificationChannel(unconfigured))
        {
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteRecipeChangeResponseAsync(true, 0, 0, 1, 1, CancellationToken.None)))
                .Message);
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.ReadRecipeChangeControllerAsync())).Message);
            Assert.Equal("ModbusRecipeChangeBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.ReadRecipeChangeRuntimeAsync())).Message);
        }

        var configured = Profile(peer.Port,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)));
        await using (var channel = new ModbusQualificationChannel(configured))
        {
            Assert.Equal("ModbusRecipeChangeRequestAuthorityRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteRecipeChangeResponseAsync(true, 0, 0, 1, 1, CancellationToken.None)))
                .Message);
        }

        Assert.Equal(0, peer.ConnectionCount);
        Assert.Empty(peer.Requests);

        var revoked = 0;
        await using (var channel = new ModbusQualificationChannel(configured, startOwnedRequest: _ =>
        {
            ++revoked;
            throw new PlcRequestRevokedException();
        }))
        {
            await channel.ConnectAsync();
            await Assert.ThrowsAsync<PlcRequestRevokedException>(() =>
                channel.WriteRecipeChangeResponseAsync(true, 0, 0, 1, 1, CancellationToken.None));
            Assert.Equal(1, revoked);
            Assert.Empty(peer.Requests);
            await Assert.ThrowsAsync<PlcRequestRevokedException>(() => channel.ReadRecipeChangeRuntimeAsync());
            Assert.Equal(2, revoked);
        }
        Assert.Empty(peer.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V146_R08_NonBooleanRecipeChangeRegistersLatchTheTransportFault(
        bool controllerBlock)
    {
        await using var peer = new RecipeChangePeer(request => controllerBlock
            ? ReadResponse(request, 2, 0, 0, 0, 0, 0)
            : ReadResponse(request, 2, 0, 0, 0, 0, 0, 0));
        await using var channel = new ModbusQualificationChannel(Profile(peer.Port,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1))));
        await channel.ConnectAsync();
        Func<Task> read = controllerBlock
            ? () => channel.ReadRecipeChangeControllerAsync()
            : () => channel.ReadRecipeChangeRuntimeAsync();

        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(read);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(read);

        Assert.Equal("ModbusQualificationBooleanRegisterInvalid", invalid.Message);
        Assert.Equal("ModbusQualificationChannelFaulted", second.Message);
        Assert.Single(peer.Requests);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("short-length")]
    [InlineData("byte-count")]
    public async Task V146_R09_MalformedRecipeChangeResponsesLatchTheFaultWithoutReplay(string mutation)
    {
        await using var peer = new RecipeChangePeer(request => mutation switch
        {
            "exception" => Frame(request, 0x83, new byte[] { 0x02 }),
            "short-length" => ReadResponse(request, 0, 0),
            _ => ByteCountMismatch(request)
        });
        await using var channel = new ModbusQualificationChannel(Profile(peer.Port,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1))));
        await channel.ConnectAsync();

        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.ReadRecipeChangeControllerAsync());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.ReadRecipeChangeControllerAsync());

        Assert.Equal(mutation switch
        {
            "exception" => "ModbusQualificationExceptionResponse",
            "short-length" => "ModbusQualificationFunctionOrLengthMismatch",
            _ => "ModbusRecipeChangeControllerReadResponseInvalid"
        }, invalid.Message);
        Assert.Equal("ModbusQualificationChannelFaulted", second.Message);
        Assert.Single(peer.Requests);
    }

    private static ModbusProductionProfile Profile(int port,
        ModbusRecipeChangeBinding? recipeChange = null,
        ModbusPartIdentityReadPlan? partIdentity = null) =>
        new("V146.Modbus.Production", "1", "127.0.0.1", port, 1, 100, 200,
            new ModbusCommunicationBinding(Policy(), 300, 400), TimeSpan.FromSeconds(2),
            partIdentity, recipeChange);

    private static PlcCommunicationPolicy Policy() =>
        new("V146.Modbus.PlcCommunication", "1", TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40), 2,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

    private static PlcResultContractBinding BindingForRange(int start, int count)
    {
        var schema = new AlgorithmResultSchema("V146.Modbus.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(),
            new OverlayContract("V146.Modbus.Overlay", "1", 1, 1, 1));
        var wire = new PlcWireEncoding(PlcWireRepresentation.UInt16, PlcByteOrder.BigEndian,
            PlcWordOrder.NotApplicable, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var contract = new PlcResultContract("V146.Modbus.Contract", "1", 4096, 512,
            new[] { new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch,
                new(start, count), wire) }, new[]
            { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash)) });
        var validation = new PlcResultSchemaValidation(contract, schema,
            new[] { new PlcResultValidationCheck("V146.Modbus.Binding", "transport", true, "Passed") });
        return new PlcResultContractBinding(new RecipeReference("V146.Modbus.Recipe", "1",
            new string('B', 64)), new AlgorithmIdentity("V146.Modbus.Algorithm", "1"), validation);
    }

    private static byte[] ReadResponse(PeerRequest request, params ushort[] registers)
    {
        var body = new byte[1 + registers.Length * 2];
        body[0] = checked((byte)(registers.Length * 2));
        for (var index = 0; index < registers.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + index * 2, 2), registers[index]);
        return Frame(request, 0x03, body);
    }

    private static byte[] ByteCountMismatch(PeerRequest request)
    {
        // The MBAP length carries a complete six-register reply while the byte-count field
        // claims a shorter payload.
        var body = new byte[13];
        body[0] = 10;
        return Frame(request, 0x03, body);
    }

    private static byte[] Frame(PeerRequest request, byte function, byte[] body)
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

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static ushort[] Registers(byte[] bytes, int offset, int count)
    {
        var result = new ushort[count];
        for (var index = 0; index < count; index++) result[index] = U16(bytes, offset + index * 2);
        return result;
    }

    private sealed record PeerRequest(ushort TransactionId, byte UnitId, byte[] Pdu);

    /// <summary>
    /// Minimal controller-owned loopback peer for the dedicated block. It answers the exact
    /// scripted bytes, records every request, and performs no runtime transition of its own.
    /// </summary>
    private sealed class RecipeChangePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<PeerRequest> _requests = new();
        private readonly Func<PeerRequest, byte[]?> _respond;
        private int _connections;
        private int _disposed;

        internal RecipeChangePeer(Func<PeerRequest, byte[]?> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        internal int Port { get; }
        internal int ConnectionCount => Volatile.Read(ref _connections);
        internal IReadOnlyList<PeerRequest> Requests => _requests.ToArray();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync()
                        .WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    client.NoDelay = true;
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var token = _shutdown.Token;
                    while (!token.IsCancellationRequested)
                    {
                        var header = await ReadExactAsync(stream, 7, token).ConfigureAwait(false);
                        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                        if (length is < 2 or > 254) return;
                        var pdu = await ReadExactAsync(stream, length - 1, token).ConfigureAwait(false);
                        var request = new PeerRequest(
                            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)), header[6], pdu);
                        _requests.Enqueue(request);
                        var response = _respond(request);
                        if (response is null) return;
                        await stream.WriteAsync(response, token).ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            _shutdown.Dispose();
        }
    }
}
