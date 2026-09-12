using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.OutboxProbe;
using SharpInspect.Runtime.Outbox;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionOutboxProtocolTests
{
    [Fact]
    [Trait("VerificationId", "V152_P01")]
    public void V152_P01_DeliveryBytesAndTransportEvidenceArePrivateFrozenCopies()
    {
        using var fixture = new OutboxReceiverFixture();
        var bytes = Encoding.UTF8.GetBytes("{\"decision\":\"Pass\"}");
        var delivery = fixture.Delivery(bytes);
        bytes[0] = 0;
        var exported = delivery.Payload!.CopyBytes();
        exported[0] = 1;
        Assert.Equal((byte)'{', delivery.Payload.CopyBytes()[0]);
        var raw = fixture.Accept(delivery);
        var observation = new OutboxTransportObservation(raw);
        raw[0] = 0;
        var first = observation.CopyAcceptance()!;
        first[0] = 1;
        Assert.Equal((byte)'{', observation.CopyAcceptance()![0]);
    }

    [Fact]
    [Trait("VerificationId", "V152_P02")]
    public void V152_P02_ReceiverCommitsOnceThenReopensAndReturnsOriginalReceiptForDuplicate()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(Encoding.UTF8.GetBytes("original wire bytes"));
        var original = fixture.Accept(delivery); // Connection closes; later acceptance opens the durable file again.
        var duplicate = fixture.Accept(delivery);
        Assert.Equal(original, duplicate);
        Assert.Equal(1L, fixture.EffectCount());
        var verified = OutboxAcceptanceVerifier.VerifyReceipt(delivery, duplicate);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(duplicate)), verified.ReceiptHash);
    }

    [Fact]
    [Trait("VerificationId", "V152_P03")]
    public void V152_P03_SameDeliveryIdWithDifferentBytesIsPermanentConflictWithoutAnotherEffect()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(new byte[] { 1, 2, 3 });
        _ = fixture.Accept(delivery);
        var conflicting = fixture.Delivery(new byte[] { 3, 2, 1 }, delivery.DeliveryId, delivery.InspectionId);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Accept(conflicting));
        Assert.Equal("ReceiverIdempotencyConflict", error.Message);
        Assert.Equal(1L, fixture.EffectCount());
    }

    [Fact]
    [Trait("VerificationId", "V152_P04")]
    public void V152_P04_ReceiptBindsExactPayloadDeliveryRouteAndPinnedReceivingIdentity()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(new byte[] { 1, 2, 3 });
        var receipt = fixture.Accept(delivery);
        var wrongPayload = fixture.Delivery(new byte[] { 7, 8, 9 }, delivery.DeliveryId, delivery.InspectionId);
        Assert.Throws<InvalidOperationException>(() => OutboxAcceptanceVerifier.VerifyReceipt(wrongPayload, receipt));
        var wrongId = fixture.Delivery(new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidOperationException>(() => OutboxAcceptanceVerifier.VerifyReceipt(wrongId, receipt));
        using var otherReceiver = new OutboxReceiverFixture();
        var wrongReceiver = otherReceiver.Delivery(new byte[] { 1, 2, 3 }, delivery.DeliveryId, delivery.InspectionId);
        Assert.Throws<InvalidOperationException>(() => OutboxAcceptanceVerifier.VerifyReceipt(wrongReceiver, receipt));
        var capability = fixture.Capability();
        Assert.Throws<InvalidOperationException>(() => OutboxAcceptanceVerifier.VerifyReceipt(delivery, capability));
    }

    [Fact]
    [Trait("VerificationId", "V152_P05")]
    public void V152_P05_RequiredBindingRejectsMissingOrWrongSignedCapability()
    {
        using var fixture = new OutboxReceiverFixture();
        var transport = new ReceiverTransport(fixture);
        Assert.Throws<ArgumentException>(() => new OutboxTransportBinding(fixture.Route, transport,
            "isolated", "1", OutboxReceiverFixture.Hash("connection")));
        using var other = new OutboxReceiverFixture();
        Assert.Throws<InvalidOperationException>(() => new OutboxTransportBinding(fixture.Route, transport,
            "isolated", "1", OutboxReceiverFixture.Hash("connection"), other.Capability()));
        var valid = fixture.Binding(transport);
        Assert.NotNull(valid.CapabilityHash);
        Assert.Empty(new ProductionOutboxOptions(Array.Empty<OutboxTransportBinding>()).Transports);
    }

    [Fact]
    [Trait("VerificationId", "V152_P06")]
    public async Task V152_P06_LostAcknowledgementRetriesSameIdentityAndBytesWithOneReceiverEffect()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(new byte[] { 1, 2, 3 });
        var transport = new ReceiverTransport(fixture) { LoseFirstAcknowledgement = true };
        var binding = fixture.Binding(transport);
        var epoch = Guid.NewGuid();
        using (var first = new OutboxSendOwner(delivery, binding, Guid.NewGuid(), epoch, TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            var outcome = await first.ObserveAsync(CancellationToken.None);
            Assert.Equal(OutboxFailureCategory.UnknownOutcome, outcome.Failure);
            Assert.Null(outcome.Acceptance);
            await first.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var attempt = Guid.NewGuid();
        using var retry = new OutboxSendOwner(delivery, binding, attempt, epoch, TimeSpan.FromSeconds(5), CancellationToken.None);
        var recovered = await retry.ObserveAsync(CancellationToken.None);
        var claim = Assert.IsType<OutboxAcceptanceVerifier.VerifiedAcceptanceClaim>(recovered.Acceptance);
        Assert.True(claim.Matches(delivery, epoch, attempt));
        Assert.False(claim.Matches(delivery, Guid.NewGuid(), attempt));
        Assert.True(claim.TryConsume());
        Assert.False(claim.TryConsume());
        Assert.Equal(1L, fixture.EffectCount());
        Assert.Equal(2, transport.Deliveries.Count);
        Assert.All(transport.Deliveries, item =>
        {
            Assert.Equal(delivery.DeliveryId, item.Id);
            Assert.Equal(delivery.Payload!.CopyBytes(), item.Bytes);
        });
    }

    [Fact]
    [Trait("VerificationId", "V152_P07")]
    public async Task V152_P07_TimeoutRevokesLateSuccessAndRetainsPhysicalOperationUntilExit()
    {
        using var fixture = new OutboxReceiverFixture();
        var transport = new HeldTransport(fixture);
        using var owner = new OutboxSendOwner(fixture.Delivery(new byte[] { 8 }), fixture.Binding(transport),
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        try
        {
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await owner.ObserveAsync(CancellationToken.None);
            Assert.Equal(OutboxFailureCategory.UnknownOutcome, result.Failure);
            Assert.Null(result.Acceptance);
            Assert.False(owner.PhysicalCompletion.IsCompleted);
            transport.Release.TrySetResult(true);
            await owner.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            var late = await owner.ObserveAsync(CancellationToken.None);
            Assert.Null(late.Acceptance);
            Assert.Equal(OutboxFailureCategory.UnknownOutcome, late.Failure);
        }
        finally { transport.Release.TrySetResult(true); }
    }

    [Fact]
    [Trait("VerificationId", "V152_P08")]
    public void V152_P08_ExpiredOrRetiredOwnerCannotIssueOrConsumeAuthenticatedAcceptance()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(new byte[] { 1 });
        var raw = fixture.Accept(delivery);
        using var expired = new OutboxAttemptAuthority(TimeSpan.FromTicks(1), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => OutboxAcceptanceVerifier.CreateClaim(delivery,
            Guid.NewGuid(), Guid.NewGuid(), raw, expired));
        using var cancellation = new CancellationTokenSource();
        using var authority = new OutboxAttemptAuthority(TimeSpan.FromSeconds(5), cancellation.Token);
        var claim = OutboxAcceptanceVerifier.CreateClaim(delivery, Guid.NewGuid(), Guid.NewGuid(), raw, authority);
        cancellation.Cancel();
        Assert.False(claim.TryConsume());
        Assert.False(claim.TryConsume());
    }

    [Fact]
    [Trait("VerificationId", "V152_P09")]
    public void V152_P09_DifferentRoutesCannotShareOnePhysicalTransportInstance()
    {
        using var first = new OutboxReceiverFixture(routeId: "required");
        using var second = new OutboxReceiverFixture(OutboxRouteCriticality.BestEffort, "best-effort");
        var transport = new ReceiverTransport(first);
        var failure = Assert.Throws<ArgumentException>(() => new ProductionOutboxOptions(new[]
            { first.Binding(transport), second.Binding(transport) }));
        Assert.Contains("OutboxTransportInstanceSharedAcrossRoutes", failure.Message);
    }

    internal class ReceiverTransport : IOutboxRouteTransport
    {
        protected readonly OutboxReceiverFixture Receiver;
        internal ReceiverTransport(OutboxReceiverFixture receiver) => Receiver = receiver;
        public OutboxContractReference Contract => OutboxReceiverFixture.AdapterContract;
        internal bool LoseFirstAcknowledgement { get; init; }
        internal List<(Guid Id, byte[] Bytes)> Deliveries { get; } = new();
        public virtual ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
            CancellationToken cancellationToken = default)
        {
            Deliveries.Add((delivery.DeliveryId, delivery.Payload!.CopyBytes()));
            var accepted = Receiver.Accept(delivery);
            if (LoseFirstAcknowledgement && Deliveries.Count == 1)
                throw new IOException("Isolated receiver committed; acknowledgement was lost");
            return ValueTask.FromResult(new OutboxTransportObservation(accepted));
        }
    }

    private sealed class HeldTransport : ReceiverTransport
    {
        internal HeldTransport(OutboxReceiverFixture receiver) : base(receiver) { }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<OutboxTransportObservation> SendAsync(OutboxDelivery delivery, Guid attemptId,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation.
            return new(Receiver.Accept(delivery));
        }
    }
}

/// <summary>Isolated receiver evidence, never a production MES or receiver qualification claim.</summary>
internal sealed class OutboxReceiverFixture : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpInspect.OutboxReceiver", Guid.NewGuid().ToString("N"));
    internal static OutboxContractReference AdapterContract { get; } = new("IsolatedReceiver", "1", Hash("isolated-adapter-v1"));
    internal OutboxReceiverFixture(OutboxRouteCriticality criticality = OutboxRouteCriticality.Required,
        string routeId = "result", int maximumPayloadBytes = 1024 * 1024)
    {
        Directory.CreateDirectory(_directory);
        Route = new(routeId, "1", criticality, "isolated-receiver",
            OutboxReceiverProtocol.CorePayloadContract, OutboxReceiverProtocol.ContentType,
            OutboxReceiverProtocol.ReceiverContract, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
            AdapterContract, maximumPayloadBytes);
        IsolatedOutboxReceiver.CreateSchema(DatabasePath);
    }
    internal string DatabasePath => Path.Combine(_directory, IsolatedOutboxReceiver.DatabaseFileName);
    internal OutboxRouteDefinition Route { get; }
    internal OutboxDelivery Delivery(byte[] bytes, Guid? id = null, Guid? inspectionId = null) =>
        new(id ?? Guid.NewGuid(), inspectionId ?? Guid.NewGuid(), Hash("core"), Route,
            new(Route.PayloadContract, Route.ContentType, bytes), null, DateTimeOffset.UtcNow, 5);
    internal OutboxTransportBinding Binding(IOutboxRouteTransport transport) =>
        new(Route, transport, "isolated-connection", "1", Hash("isolated-connection-v1"), Capability());
    internal byte[] Capability() => Sign(OutboxReceiverProtocol.CreateCapabilityStatement(Route));
    internal byte[] Accept(OutboxDelivery delivery) =>
        IsolatedOutboxReceiver.Accept(DatabasePath, _key, delivery);
    internal long EffectCount() => IsolatedOutboxReceiver.EffectCount(DatabasePath);
    /// <summary>
    /// Test-only export of one frozen delivery input and this fixture's ephemeral receiver key so a
    /// separate probe process can execute the identical acceptance path. Everything stays inside
    /// this fixture's temporary receiver root; no production key, MES or station store is exported.
    /// </summary>
    internal OutboxReceiverProbeExport ExportProbeInput(OutboxDelivery delivery)
    {
        var inputPath = Path.Combine(_directory, "receiver-probe-input-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(inputPath, IsolatedOutboxReceiver.EncodeProbeInput(delivery));
        var ownerKeyPath = Path.Combine(_directory, IsolatedOutboxReceiver.OwnerKeyFileName);
        File.WriteAllBytes(ownerKeyPath, _key.ExportPkcs8PrivateKey());
        return new(inputPath, ownerKeyPath, DatabasePath);
    }
    private byte[] Sign(byte[] statement) => OutboxReceiverProtocol.CreateSignedEnvelope(statement,
        _key.SignData(statement, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose()
    {
        _key.Dispose();
        // Fixture evidence remains available for inspection and is outside the source checkout.
    }
}
