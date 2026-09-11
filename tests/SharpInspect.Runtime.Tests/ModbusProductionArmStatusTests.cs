using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;

using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded transport tests for the Runtime-owned Production Arm Status observation block.
/// They exercise the real loopback wire bytes and the existing channel guards; they assert no
/// arm, admission, activation, or audit behaviour, which stays with the caller.
/// </summary>
public sealed class ModbusProductionArmStatusTests
{
    [Fact]
    public void V147_P01_LegacyProfileHashesSurviveTheNewArmStatusOverload()
    {
        // The frozen v1/v2 values are copied from the T46 transport tests: the observation
        // block is additive and, while it is absent, every old overload keeps its exact hash.
        var policy = new PlcCommunicationPolicy("V143.Legacy.Policy", "1",
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var communication = new ModbusCommunicationBinding(policy, 100, 200);
        var plan = new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 600, 32);
        var recipeChange = new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1));

        var legacy = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500));
        var legacyThroughOverload = new ModbusProductionProfile("V143.Legacy.Profile", "1",
            "127.0.0.1", 502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500),
            null, null, null);
        var identity = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500), plan);
        var identityThroughOverload = new ModbusProductionProfile("V143.Legacy.Profile", "1",
            "127.0.0.1", 502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500), plan,
            null, null);
        var recipe = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500), plan, recipeChange);
        var recipeThroughOverload = new ModbusProductionProfile("V143.Legacy.Profile", "1",
            "127.0.0.1", 502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500), plan,
            recipeChange, null);

        Assert.Equal("CA5C222F277000670327BDB357CAA9EDFEE140FEB57192F65FC9ED529D0A259F",
            legacy.ContentHash);
        Assert.Equal(legacy.ContentHash, legacyThroughOverload.ContentHash);
        Assert.Equal("3607148DF4F3C0C46B5750D150B6D088EF465C99DF9A83595154ABE2FBABB6DF",
            identity.ContentHash);
        Assert.Equal(identity.ContentHash, identityThroughOverload.ContentHash);
        Assert.Equal(recipe.ContentHash, recipeThroughOverload.ContentHash);
        Assert.Null(legacy.ProductionArmStatus);
        Assert.Null(legacyThroughOverload.ProductionArmStatus);
        Assert.Null(identityThroughOverload.ProductionArmStatus);
        Assert.Null(recipeThroughOverload.ProductionArmStatus);

        var binding = new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 700);
        var bound = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500), plan, recipeChange, binding);
        var boundAgain = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1",
            502, 1, 0, 10, communication, TimeSpan.FromMilliseconds(500), plan, recipeChange,
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 700));
        var moved = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, communication, TimeSpan.FromMilliseconds(500), plan, recipeChange,
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 701));

        Assert.Same(binding, bound.ProductionArmStatus);
        Assert.NotEqual(recipe.ContentHash, bound.ContentHash);
        Assert.Equal(bound.ContentHash, boundAgain.ContentHash);
        Assert.NotEqual(bound.ContentHash, moved.ContentHash);
    }

    [Fact]
    public void V147_P02_ArmStatusBindingValidatesRegisterCountRangeIdentityAndHash()
    {
        var binding = new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 700);

        Assert.Equal(27, ModbusProductionArmStatusBinding.RuntimeRegisterCount);
        Assert.Equal(27, binding.RuntimeEndAddressExclusive - binding.RuntimeStartAddress);
        Assert.Equal("V147.ArmStatus", binding.Id);
        Assert.Equal("1", binding.Version);
        Assert.Equal((ushort)700, binding.RuntimeStartAddress);
        Assert.Equal(binding.ContentHash,
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 700).ContentHash);
        Assert.NotEqual(binding.ContentHash,
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "2", 700).ContentHash);
        Assert.NotEqual(binding.ContentHash,
            new ModbusProductionArmStatusBinding("V147.ArmStatus.B", "1", 700).ContentHash);
        Assert.NotEqual(binding.ContentHash,
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 701).ContentHash);

        // The final register of the block is address 65535; one more register overflows.
        _ = new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 65509);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1", 65510));
        Assert.Throws<ArgumentException>(() =>
            new ModbusProductionArmStatusBinding(string.Empty, "1", 700));
        Assert.Throws<ArgumentException>(() =>
            new ModbusProductionArmStatusBinding("V147.ArmStatus", "1 ", 700));
    }

    [Fact]
    public void V147_P03_ProfileRejectsArmStatusBlockOverlappingAnyExistingBlock()
    {
        Assert.Throws<ArgumentException>(() => Profile(1502, armStatus: ArmStatus(104)));
        Assert.Throws<ArgumentException>(() => Profile(1502, armStatus: ArmStatus(204)));
        Assert.Throws<ArgumentException>(() => Profile(1502, armStatus: ArmStatus(300)));
        Assert.Throws<ArgumentException>(() => Profile(1502, armStatus: ArmStatus(400)));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            recipeChange: new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)),
            armStatus: ArmStatus(503)));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            recipeChange: new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)),
            armStatus: ArmStatus(521)));
        Assert.Throws<ArgumentException>(() => Profile(1502,
            partIdentity: new ModbusPartIdentityReadPlan("V147.Part", "1", 600, 32),
            armStatus: ArmStatus(610)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Profile(1502, armStatus: ArmStatus(65510)));

        var bare = Profile(1502);
        var bound = Profile(1502, armStatus: ArmStatus(700));
        Assert.Null(bare.ProductionArmStatus);
        Assert.NotNull(bound.ProductionArmStatus);
        Assert.Equal((ushort)700, bound.ProductionArmStatus!.RuntimeStartAddress);
        Assert.NotEqual(bare.ContentHash, bound.ContentHash);
        Assert.Equal(bound.ContentHash, Profile(1502, armStatus: ArmStatus(700)).ContentHash);
        Assert.NotEqual(bound.ContentHash, Profile(1502, armStatus: ArmStatus(701)).ContentHash);
        _ = Profile(1502, armStatus: ArmStatus(65509));
    }

    [Fact]
    public void V147_P04_PayloadValidationExcludesTheArmStatusObservationBlock()
    {
        var profile = Profile(1502,
            new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)),
            new ModbusPartIdentityReadPlan("V147.Part", "1", 600, 32),
            ArmStatus(700));
        var channel = new ModbusQualificationChannel(profile);

        Assert.Equal("ModbusProductionPayloadOverlapsArmStatusBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(700, 1))).Message);
        Assert.Equal("ModbusProductionPayloadOverlapsArmStatusBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(724, 3))).Message);
        Assert.Equal("ModbusProductionPayloadOverlapsRecipeChangeBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(520, 1))).Message);
        Assert.Equal("ModbusQualificationPayloadOverlapsControlBlock",
            Assert.Throws<ArgumentException>(() =>
                channel.ValidatePayloadBinding(BindingForRange(100, 1))).Message);
        channel.ValidatePayloadBinding(BindingForRange(728, 2));
    }

    [Fact]
    public async Task V147_P05_ArmStatusWriteUsesFc16OnTheBoundBlockThroughTheOwnedRequestFence()
    {
        await using var peer = new ArmStatusPeer(request =>
            Frame(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray()));
        var fenced = 0;
        await using var channel = new ModbusQualificationChannel(
            Profile(peer.Port, armStatus: ArmStatus(700)),
            startOwnedRequest: start =>
            {
                Interlocked.Increment(ref fenced);
                return start();
            });
        await channel.ConnectAsync();

        await channel.WriteProductionArmStatusAsync(new ModbusProductionArmStatusSignals(
            true, 0x0007, 0x0003, 0x0009, 0x000B,
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 0xA1B2C3D4, 0x01020304,
            0x05060708, Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")),
            CancellationToken.None);

        Assert.Equal(1, fenced);
        Assert.Single(peer.Requests);
        var request = peer.Requests[0];
        Assert.Equal((byte)0x10, request.Pdu[0]);
        Assert.Equal((ushort)700, U16(request.Pdu, 1));
        Assert.Equal((ushort)27, U16(request.Pdu, 3));
        Assert.Equal(54, request.Pdu[5]);
        Assert.Equal(new ushort[]
        {
            1, 0x0007, 0x0003, 0x0009, 0x000B,
            0x0011, 0x2233, 0x4455, 0x6677, 0x8899, 0xAABB, 0xCCDD, 0xEEFF,
            0xA1B2, 0xC3D4, 0x0102, 0x0304, 0x0506, 0x0708,
            0xFFEE, 0xDDCC, 0xBBAA, 0x9988, 0x7766, 0x5544, 0x3322, 0x1100
        }, Registers(request.Pdu, 6, 27));
    }

    [Fact]
    public async Task V147_P06_InvalidBlocksAreRejectedOrEncodedAsExactZero()
    {
        await using var peer = new ArmStatusPeer(request =>
            Frame(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray()));
        var fenced = 0;
        await using var channel = new ModbusQualificationChannel(
            Profile(peer.Port, armStatus: ArmStatus(700)),
            startOwnedRequest: start =>
            {
                Interlocked.Increment(ref fenced);
                return start();
            });
        await channel.ConnectAsync();

        await channel.WriteProductionArmStatusAsync(new ModbusProductionArmStatusSignals(
            false, 0, 0, 0, 0, Guid.Empty, 0, 0, 0, Guid.Empty), CancellationToken.None);

        Assert.Equal(1, fenced);
        Assert.Single(peer.Requests);
        Assert.Equal(new ushort[27], Registers(peer.Requests[0].Pdu, 6, 27));

        Assert.Equal("ModbusProductionArmStatusIdentityRequired",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                channel.WriteProductionArmStatusAsync(new ModbusProductionArmStatusSignals(
                    true, 0, 0, 0, 0, Guid.Empty, 1, 1, 1, Guid.NewGuid()),
                    CancellationToken.None))).Message);
        Assert.Equal("ModbusProductionArmStatusIdentityRequired",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                channel.WriteProductionArmStatusAsync(new ModbusProductionArmStatusSignals(
                    true, 0, 0, 0, 0, Guid.NewGuid(), 1, 1, 1, Guid.Empty),
                    CancellationToken.None))).Message);
        Assert.Equal("ModbusProductionArmStatusInvalidBlockMustBeZero",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                channel.WriteProductionArmStatusAsync(new ModbusProductionArmStatusSignals(
                    false, 0, 0, 0, 0, Guid.Empty, 0, 0, 0, Guid.NewGuid()),
                    CancellationToken.None))).Message);

        Assert.Equal(1, fenced);
        Assert.Single(peer.Requests);
    }

    [Fact]
    public async Task V147_P07_MissingConfigurationOrSendAuthorityRejectsBeforeAnyIo()
    {
        await using var peer = new ArmStatusPeer(request =>
            Frame(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray()));
        var signals = ValidSignals();

        var qualification = new ModbusQualificationProfile("V147.Modbus.Qualification", "1",
            "V147.Scenario", "127.0.0.1", peer.Port, 1, 100, 200, 1, new string('A', 64),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None);
        await using (var channel = new ModbusQualificationChannel(qualification))
        {
            Assert.Equal("ModbusProductionArmStatusBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteProductionArmStatusAsync(signals, CancellationToken.None))).Message);
        }

        var unbound = Profile(peer.Port);
        await using (var channel = new ModbusQualificationChannel(unbound))
        {
            Assert.Equal("ModbusProductionArmStatusBindingRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteProductionArmStatusAsync(signals, CancellationToken.None))).Message);
        }

        var bound = Profile(peer.Port, armStatus: ArmStatus(700));
        await using (var channel = new ModbusQualificationChannel(bound))
        {
            Assert.Equal("ModbusProductionArmStatusRequestAuthorityRequired",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    channel.WriteProductionArmStatusAsync(signals, CancellationToken.None))).Message);
        }

        Assert.Equal(0, peer.ConnectionCount);
        Assert.Empty(peer.Requests);

        var revoked = 0;
        await using (var channel = new ModbusQualificationChannel(bound, startOwnedRequest: _ =>
        {
            ++revoked;
            throw new PlcRequestRevokedException();
        }))
        {
            await channel.ConnectAsync();
            await Assert.ThrowsAsync<PlcRequestRevokedException>(() =>
                channel.WriteProductionArmStatusAsync(signals, CancellationToken.None));
            Assert.Equal(1, revoked);
            Assert.Empty(peer.Requests);
        }
    }

    [Fact]
    public async Task V147_P08_ArmStatusWriteLeavesControllerAndRecipeChangeResponseUntouched()
    {
        // The previous Recipe Change response was acknowledged and reset to exactly zero; the
        // arm observation must reach its own block only and leave that frozen response alone.
        var responseReads = 0;
        await using var peer = new ArmStatusPeer(request =>
        {
            if (request.Pdu.Length == 5 && request.Pdu[0] == 0x03)
            {
                Interlocked.Increment(ref responseReads);
                return ReadResponse(request, 0, 0, 0, 0, 0, 0, 0);
            }
            return Frame(request, 0x10, request.Pdu.AsSpan(1, 4).ToArray());
        });
        await using var channel = new ModbusQualificationChannel(
            Profile(peer.Port, new ModbusRecipeChangeBinding(500, 520, TimeSpan.FromSeconds(1)),
                null, ArmStatus(700)),
            startOwnedRequest: start => start());
        await channel.ConnectAsync();

        await channel.WriteProductionArmStatusAsync(ValidSignals(), CancellationToken.None);
        var frozen = await channel.ReadRecipeChangeRuntimeAsync();

        Assert.False(frozen.ResponseValid);
        Assert.Equal((ushort)0, frozen.Outcome);
        Assert.Equal((ushort)0, frozen.Reason);
        Assert.Equal(0u, frozen.RequestSequence);
        Assert.Equal(0u, frozen.SelectionCode);
        Assert.Equal(1, responseReads);

        Assert.Equal(2, peer.RequestCount);
        Assert.All(peer.Requests, request =>
        {
            Assert.NotEqual((byte)0x06, request.Pdu[0]);
            if (request.Pdu[0] == 0x10)
                Assert.Equal((ushort)700, U16(request.Pdu, 1));
        });
        Assert.Equal((byte)0x10, peer.Requests[0].Pdu[0]);
        Assert.Equal((ushort)700, U16(peer.Requests[0].Pdu, 1));
        Assert.Equal((ushort)27, U16(peer.Requests[0].Pdu, 3));
        Assert.Equal((byte)0x03, peer.Requests[1].Pdu[0]);
        Assert.Equal((ushort)520, U16(peer.Requests[1].Pdu, 1));
        Assert.Equal((ushort)7, U16(peer.Requests[1].Pdu, 3));
    }

    private static ModbusProductionProfile Profile(int port,
        ModbusRecipeChangeBinding? recipeChange = null,
        ModbusPartIdentityReadPlan? partIdentity = null,
        ModbusProductionArmStatusBinding? armStatus = null) =>
        new("V147.Modbus.Production", "1", "127.0.0.1", port, 1, 100, 200,
            new ModbusCommunicationBinding(Policy(), 300, 400), TimeSpan.FromSeconds(2),
            partIdentity, recipeChange, armStatus);

    private static ModbusProductionArmStatusBinding ArmStatus(int address) =>
        new("V147.ArmStatus", "1", checked((ushort)address));

    private static ModbusProductionArmStatusSignals ValidSignals() => new(true, 0x0011, 0x0022,
        0x0033, 0x0044, Guid.Parse("11111111-2222-3333-4444-555555555555"), 0x0000A001,
        0x0000B002, 0x0000C003, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static PlcCommunicationPolicy Policy() =>
        new("V147.Modbus.PlcCommunication", "1", TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40), 2,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

    private static PlcResultContractBinding BindingForRange(int start, int count)
    {
        var schema = new AlgorithmResultSchema("V147.Modbus.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(),
            new OverlayContract("V147.Modbus.Overlay", "1", 1, 1, 1));
        var wire = new PlcWireEncoding(PlcWireRepresentation.UInt16, PlcByteOrder.BigEndian,
            PlcWordOrder.NotApplicable, PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);
        var contract = new PlcResultContract("V147.Modbus.Contract", "1", 4096, 512,
            new[] { new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch,
                new(start, count), wire) }, new[]
            { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash)) });
        var validation = new PlcResultSchemaValidation(contract, schema,
            new[] { new PlcResultValidationCheck("V147.Modbus.Binding", "transport", true, "Passed") });
        return new PlcResultContractBinding(new RecipeReference("V147.Modbus.Recipe", "1",
            new string('B', 64)), new AlgorithmIdentity("V147.Modbus.Algorithm", "1"), validation);
    }

    private static byte[] ReadResponse(PeerRequest request, params ushort[] registers)
    {
        var body = new byte[1 + registers.Length * 2];
        body[0] = checked((byte)(registers.Length * 2));
        for (var index = 0; index < registers.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + index * 2, 2), registers[index]);
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
    /// Minimal controller-owned loopback peer for the observation block. It answers the exact
    /// scripted bytes, records every request, and performs no runtime transition of its own.
    /// </summary>
    private sealed class ArmStatusPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<PeerRequest> _requests = new();
        private readonly Func<PeerRequest, byte[]?> _respond;
        private int _connections;
        private int _disposed;

        internal ArmStatusPeer(Func<PeerRequest, byte[]?> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        internal int Port { get; }
        internal int ConnectionCount => Volatile.Read(ref _connections);
        internal int RequestCount => _requests.Count;
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
