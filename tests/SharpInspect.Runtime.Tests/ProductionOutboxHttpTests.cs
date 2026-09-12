using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionOutboxHttpTests
{
    [Fact]
    [Trait("VerificationId", "V152_H01")]
    public async Task V152_H01_HttpRetriesSendExactFrozenBytesAndIdentityWithDistinctAttemptIds()
    {
        using var receiver = new OutboxReceiverFixture();
        using var server = new IsolatedHttpPeer();
        using var transport = new HttpOutboxTransport(server.Endpoint);
        var source = receiver.Delivery(Encoding.UTF8.GetBytes("{\"value\":\"冻结报文\"}"));
        var route = new OutboxRouteDefinition(source.Route.RouteId, source.Route.Version, source.Route.Criticality,
            source.Route.DestinationIdentity, source.Route.PayloadContract, source.Route.ContentType,
            source.Route.ReceiverContract, source.Route.ReceiverPublicKeyBase64, HttpOutboxTransport.AdapterContract,
            source.Route.MaximumPayloadBytes);
        var delivery = new OutboxDelivery(source.DeliveryId, source.InspectionId, source.CoreHash, route,
            new(route.PayloadContract, route.ContentType, source.Payload!.CopyBytes()), null, source.CreatedAtUtc, 5);
        var receipt = receiver.Accept(delivery);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var attempt in new[] { first, second })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var served = server.ServeOnceAsync(200, receipt, false, timeout.Token);
            var observation = await transport.SendAsync(delivery, attempt, timeout.Token);
            var request = await served;
            Assert.Equal("POST /result HTTP/1.1", request.StartLine);
            Assert.Equal(delivery.Payload!.CopyBytes(), request.Body);
            Assert.Equal(delivery.DeliveryId.ToString("D"), request.Headers["X-SharpInspect-Delivery-Id"]);
            Assert.Equal(attempt.ToString("D"), request.Headers["X-SharpInspect-Attempt-Id"]);
            Assert.Equal(delivery.Payload.ContentHash, request.Headers["X-SharpInspect-Payload-Hash"]);
            Assert.Equal(route.ContentHash, request.Headers["X-SharpInspect-Route-Hash"]);
            Assert.Null(observation.Failure);
            Assert.Equal(receipt, observation.CopyAcceptance());
            _ = OutboxAcceptanceVerifier.VerifyReceipt(delivery, observation.CopyAcceptance()!);
        }
        Assert.Equal(1L, receiver.EffectCount());
    }

    [Theory]
    [InlineData(302, OutboxFailureCategory.Permanent)]
    [InlineData(409, OutboxFailureCategory.Permanent)]
    [InlineData(408, OutboxFailureCategory.UnknownOutcome)]
    [InlineData(429, OutboxFailureCategory.Transient)]
    [InlineData(500, OutboxFailureCategory.Transient)]
    [Trait("VerificationId", "V152_H02")]
    public async Task V152_H02_NonSuccessIsTypedAndRedirectDoesNotResend(int status, OutboxFailureCategory category)
    {
        using var receiver = new OutboxReceiverFixture();
        using var server = new IsolatedHttpPeer();
        using var transport = new HttpOutboxTransport(server.Endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var served = server.ServeOnceAsync(status, Array.Empty<byte>(), false, timeout.Token);
        var observation = await transport.SendAsync(receiver.Delivery(new byte[] { 1, 2, 3 }), Guid.NewGuid(), timeout.Token);
        await served;
        Assert.Equal(category, observation.Failure);
        Assert.Null(observation.CopyAcceptance());
        Assert.False(server.HasPendingConnection);
    }

    [Theory]
    [InlineData(0, false, "OutboxAcceptanceSizeInvalid")]
    [InlineData(16385, false, "OutboxAcceptanceTooLarge")]
    [InlineData(16385, true, "OutboxAcceptanceSizeInvalid")]
    [Trait("VerificationId", "V152_H03")]
    public async Task V152_H03_AcknowledgementSizeIsBoundedEvenWithoutContentLength(int bytes, bool chunked, string reason)
    {
        using var receiver = new OutboxReceiverFixture();
        using var server = new IsolatedHttpPeer();
        using var transport = new HttpOutboxTransport(server.Endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var served = server.ServeOnceAsync(200, new byte[bytes], chunked, timeout.Token);
        var observation = await transport.SendAsync(receiver.Delivery(new byte[] { 1 }), Guid.NewGuid(), timeout.Token);
        await served;
        Assert.Equal(OutboxFailureCategory.Permanent, observation.Failure);
        Assert.Equal(reason, observation.ReasonCode);
        Assert.Null(observation.CopyAcceptance());
    }

    private sealed class IsolatedHttpPeer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        internal IsolatedHttpPeer()
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/result");
        }
        internal Uri Endpoint { get; }
        internal bool HasPendingConnection => _listener.Pending();
        internal async Task<RequestEvidence> ServeOnceAsync(int status, byte[] body, bool chunked, CancellationToken token)
        {
            using var client = await _listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            var header = new List<byte>();
            var next = new byte[1];
            while (header.Count < 16384)
            {
                if (await stream.ReadAsync(next, token) != 1) throw new IOException("RequestHeaderTruncated");
                header.Add(next[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var count = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(count, 1, 8 * 1024 * 1024);
            var requestBody = new byte[count];
            var read = 0;
            while (read < count)
            {
                var current = await stream.ReadAsync(requestBody.AsMemory(read), token);
                if (current == 0) throw new IOException("RequestBodyTruncated");
                read += current;
            }
            var framing = chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nConnection: close\r\n" +
                $"Location: {Endpoint}redirect-target\r\n" + framing + "\r\n");
            await stream.WriteAsync(response, token);
            if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"), token);
            await stream.WriteAsync(body, token);
            if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"), token);
            await stream.FlushAsync(token);
            return new(lines[0], headers, requestBody);
        }
        public void Dispose() => _listener.Stop();
    }
    private sealed record RequestEvidence(string StartLine, Dictionary<string, string> Headers, byte[] Body);
}
