using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Outbox;

/// <summary>Prepared once outside the transaction; reused unchanged for final-fence rollback retries.</summary>
internal sealed class FrozenOutboxBatch
{
    internal FrozenOutboxBatch(string coreHash, string routeSetHash, IEnumerable<OutboxDelivery> deliveries)
    {
        CoreHash = OutboxValidation.Hash(coreHash);
        RouteSetHash = OutboxValidation.Hash(routeSetHash);
        ArgumentNullException.ThrowIfNull(deliveries);
        var copy = deliveries.Take(65).ToArray();
        if (copy.Length > 64 || copy.Any(value => value is null || value.CoreHash != CoreHash) ||
            copy.Select(value => value.DeliveryId).Distinct().Count() != copy.Length ||
            copy.Select(value => value.Route.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            throw new ArgumentException("OutboxFrozenBatchInvalid", nameof(deliveries));
        Deliveries = Array.AsReadOnly(copy.OrderBy(value => value.Route.RouteId, StringComparer.Ordinal).ToArray());
        ContentHash = OutboxValidation.HashParts(new[] { "sharpinspect-outbox-batch-v1", CoreHash, RouteSetHash }
            .Concat(Deliveries.Select(value => value.ContentHash)).ToArray());
    }
    internal string CoreHash { get; }
    internal string RouteSetHash { get; }
    internal ReadOnlyCollection<OutboxDelivery> Deliveries { get; }
    internal string ContentHash { get; }

    internal static FrozenOutboxBatch Prepare(ProductionInspectionCore core, ProductionOutboxStoreOptions options,
        CancellationToken cancellationToken, StoreDeadline deadline)
    {
        options.Validate();
        var items = new List<OutboxDelivery>(options.Routes.Count);
        foreach (var route in options.Routes)
        {
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var id = Guid.NewGuid();
            OutboxPayloadSnapshot? payload = null;
            string? failure = null;
            try
            {
                var bytes = EncodeCore(core, route, id, Math.Min(route.MaximumPayloadBytes, options.MaximumPayloadBytes),
                    deadline, cancellationToken);
                payload = new(route.PayloadContract, route.ContentType, bytes);
            }
            catch (OutboxPayloadCapacityException)
            {
                if (route.Criticality == OutboxRouteCriticality.Required)
                    throw new InvalidOperationException("OutboxRequiredPayloadCapacityExceeded");
                failure = "OutboxPayloadCapacityExceeded";
            }
            items.Add(new(id, core.Admission.InspectionId, core.ContentHash, route, payload, failure,
                core.CommittedAtUtc, options.MaximumAttempts));
        }
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        return new(core.ContentHash, options.RouteSetHash, items);
    }

    private static byte[] EncodeCore(ProductionInspectionCore core, OutboxRouteDefinition route,
        Guid deliveryId, int maximumBytes, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        if (!OutboxValidation.SameContract(route.PayloadContract, OutboxReceiverProtocol.CorePayloadContract) ||
            route.ContentType != OutboxReceiverProtocol.ContentType)
            throw new InvalidOperationException("OutboxPayloadContractUnsupported");
        using var stream = new BoundedPayloadStream(maximumBytes, deadline, cancellationToken);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", route.PayloadContract.Id);
            writer.WriteString("version", route.PayloadContract.Version);
            writer.WriteString("deliveryId", deliveryId.ToString("D"));
            writer.WriteString("inspectionId", core.Admission.InspectionId.ToString("D"));
            writer.WriteString("stationId", core.Admission.StationId);
            writer.WriteString("coreHash", core.ContentHash);
            writer.WriteString("routeId", route.RouteId);
            writer.WriteString("routeVersion", route.Version);
            writer.WriteString("routeHash", route.ContentHash);
            writer.WriteString("destinationIdentity", route.DestinationIdentity);
            writer.WriteString("acceptedAtUtc", core.Admission.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("committedAtUtc", core.CommittedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("controllerEpoch", core.Admission.ControllerCycle.ControllerEpoch);
            writer.WriteNumber("cycleSequence", core.Admission.ControllerCycle.CycleSequence);
            writer.WriteString("executionStatus", core.ExecutionStatus.ToString());
            writer.WriteString("decision", core.Decision.ToString());
            writer.WriteString("reasonCode", core.ReasonCode);
            writer.WriteString("partIdentity", core.PartIdentity);
            writer.WriteString("recipeActivationHash", core.Admission.ActivationSnapshot.ContentHash);
            writer.WriteString("tracePolicyHash", core.Admission.TracePolicySnapshot.ContentHash);
            writer.WriteString("structuredResultHash", core.StructuredResultHash);
            writer.WriteString("structuredResultJson", core.StructuredResultJson);
            writer.WriteString("plcPayloadHash", core.PlcPayload?.ContentHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private sealed class OutboxPayloadCapacityException : IOException { }
    private sealed class BoundedPayloadStream : MemoryStream
    {
        private readonly int _limit;
        private readonly StoreDeadline _deadline;
        private readonly CancellationToken _token;
        internal BoundedPayloadStream(int limit, StoreDeadline deadline, CancellationToken token)
        { _limit = limit; _deadline = deadline; _token = token; }
        public override void Write(byte[] buffer, int offset, int count)
        { RequireCapacity(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { RequireCapacity(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { RequireCapacity(1); base.WriteByte(value); }
        public override void SetLength(long value)
        {
            if (value < 0 || value > _limit) throw new OutboxPayloadCapacityException();
            base.SetLength(value);
        }
        private void RequireCapacity(int additional)
        {
            SqliteNative.EnsureDeadline(_deadline, _token);
            if (Position > _limit - additional) throw new OutboxPayloadCapacityException();
        }
    }
}
