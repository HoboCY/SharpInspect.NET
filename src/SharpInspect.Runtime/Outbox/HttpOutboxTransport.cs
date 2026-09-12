using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

/// <summary>
/// Explicit HTTP destination for the signed durable-acceptance contract. Redirects are rejected;
/// the caller owns this transport's lifetime. Constructing it performs no I/O and discovers no
/// destination. Receiver identity is independently pinned by the signed acceptance contract.
/// </summary>
public sealed class HttpOutboxTransport : IOutboxRouteTransport, IDisposable
{
    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    public static OutboxContractReference AdapterContract { get; } = new("SharpInspect.Outbox.Http", "1",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "HTTP-POST-frozen-bytes-v1;no-redirect;delivery-route-payload-headers;signed-acceptance-body-max16384"))));

    public HttpOutboxTransport(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("OutboxEndpointInvalid", nameof(endpoint));
        _endpoint = endpoint;
        _client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 1,
            UseCookies = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        ConnectionConfigurationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
    }
    public OutboxContractReference Contract => AdapterContract;
    /// <summary>The endpoint binding hash; the raw address is never put in Core or Outbox payloads.</summary>
    public string ConnectionConfigurationHash { get; }

    public async ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Payload is not { } payload) throw new ArgumentException("OutboxPayloadMissing", nameof(delivery));
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(payload.CopyBytes());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType);
        request.Headers.Add("X-SharpInspect-Delivery-Id", delivery.DeliveryId.ToString("D"));
        request.Headers.Add("X-SharpInspect-Attempt-Id", attemptId.ToString("D"));
        request.Headers.Add("X-SharpInspect-Payload-Hash", payload.ContentHash);
        request.Headers.Add("X-SharpInspect-Route-Hash", delivery.Route.ContentHash);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            return status is 408 ? new(OutboxFailureCategory.UnknownOutcome, "OutboxHttpRequestTimeout") :
                status is 429 or >= 500 ? new(OutboxFailureCategory.Transient, "OutboxHttpTemporaryRejection") :
                new(OutboxFailureCategory.Permanent, status == 409 ? "OutboxReceiverIdempotencyConflict" :
                    "OutboxHttpPermanentRejection");
        }
        if (response.Content.Headers.ContentLength is > OutboxReceiverProtocol.MaximumEvidenceBytes)
            return new(OutboxFailureCategory.Permanent, "OutboxAcceptanceTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[OutboxReceiverProtocol.MaximumEvidenceBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count is < 1 or > OutboxReceiverProtocol.MaximumEvidenceBytes
            ? new(OutboxFailureCategory.Permanent, "OutboxAcceptanceSizeInvalid")
            : new(buffer.AsSpan(0, count));
    }
    public void Dispose() => _client.Dispose();
}
