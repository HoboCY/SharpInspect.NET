using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.OutboxProbe;

/// <summary>
/// Test-only contract shared by the parent test and the probe child process: the flushed durability
/// boundary marker, the accepted phase names, the structured evidence event names and the
/// conflict reason. Both sides compile this file so the killed boundary and the reported evidence
/// cannot drift apart.
/// </summary>
internal static class OutboxReceiverProbeContract
{
    internal const string BoundaryPrefix = "V152-PROCESS-BOUNDARY:";
    internal const string NoBoundary = "none";
    internal const string BeforeSend = "before-send";
    internal const string BeforeCommit = "before-commit";
    internal const string AfterCommit = "after-commit";
    internal const string BoundaryEvent = "receiver-boundary";
    internal const string AcceptEvent = "receiver-accept";
    internal const string ConflictEvent = "receiver-conflict";
    internal const string ErrorEvent = "receiver-error";
    internal const string ConflictReason = "ReceiverIdempotencyConflict";
    internal const int MaximumReceiptBytes = OutboxReceiverProtocol.MaximumEvidenceBytes;
    internal const int MaximumPayloadBytes = OutboxPayloadSnapshot.MaximumAllowedBytes;
    internal const int MaximumInputBytes = 12 * 1024 * 1024; // 8 MiB payload plus base64 and JSON overhead.
    internal const int MaximumOwnerKeyBytes = 4 * 1024;

    internal static bool IsPhase(string value) =>
        value is NoBoundary or BeforeSend or BeforeCommit or AfterCommit;
}

/// <summary>
/// Paths of one isolated-receiver probe export. All of them stay inside the fixture's temporary
/// receiver root: the frozen delivery input, the fixture's ephemeral receiver key and the SQLite
/// receiver database. No production key, MES or station store is ever exported.
/// </summary>
internal sealed record OutboxReceiverProbeExport(string InputPath, string OwnerKeyPath, string DatabasePath);

/// <summary>The persisted receiver row read back by an independent connection.</summary>
internal sealed record OutboxReceiverStoredReceipt(string PayloadHash, string ReceiptId,
    DateTimeOffset AcceptedAtUtc, byte[] Receipt);

/// <summary>
/// The isolated receiver shared by the protocol fixture and by the killed child process: one
/// SQLite file holding a receipt row and a business effect row per delivery, WAL and FULL
/// durability, and delivery-id deduplication. The signed acceptance is exposed only after
/// COMMIT, an identical duplicate returns the original stored receipt, and different bytes for the
/// same delivery id are a permanent conflict. One implementation for both the fixture and the
/// child means the process that dies and the process that replays execute the same acceptance path.
/// This proves the isolated receiver's own behavior only. It is not a production MES, receiver,
/// qualification or sender/Core-transaction claim.
/// </summary>
internal static class IsolatedOutboxReceiver
{
    internal const string DatabaseFileName = "receiver.db";
    internal const string OwnerKeyFileName = "receiver-probe-owner.pkcs8";

    internal static SqliteConnection Open(string databasePath)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return database;
    }

    internal static void CreateSchema(string databasePath)
    {
        using var database = Open(databasePath);
        using var create = database.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS receipts(DeliveryId TEXT PRIMARY KEY,PayloadHash TEXT NOT NULL," +
            "ReceiptId TEXT NOT NULL,AcceptedAtUtc TEXT NOT NULL,Receipt BLOB NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS effects(DeliveryId TEXT PRIMARY KEY REFERENCES receipts(DeliveryId));";
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// Accepts one delivery, optionally reporting the durability boundaries the parent test kills
    /// this process at. The prepared receipt stays private to the transaction until COMMIT.
    /// </summary>
    internal static byte[] Accept(string databasePath, ECDsa owner, OutboxDelivery delivery,
        Action<string>? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(delivery);
        var payload = delivery.Payload ??
            throw new ArgumentException("OutboxProbeReceiverPayloadRequired", nameof(delivery));
        using var database = Open(databasePath);
        using var transaction = database.BeginTransaction();
        using var existing = database.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT PayloadHash,Receipt FROM receipts WHERE DeliveryId=$id";
        existing.Parameters.AddWithValue("$id", delivery.DeliveryId.ToString("D"));
        using (var read = existing.ExecuteReader())
        {
            if (read.Read())
            {
                if (read.GetString(0) != payload.ContentHash)
                    throw new InvalidOperationException(OutboxReceiverProbeContract.ConflictReason);
                return (byte[])read.GetValue(1);
            }
        }
        var receiptId = Guid.NewGuid().ToString("D");
        var acceptedAt = DateTimeOffset.UtcNow;
        // The prepared receipt remains private to this transaction until COMMIT. No caller can
        // observe it or a business effect if COMMIT fails or the process dies before COMMIT.
        var receipt = Sign(owner, OutboxReceiverProtocol.CreateAcceptanceStatement(delivery, receiptId, acceptedAt));
        using var insert = database.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO receipts VALUES($id,$hash,$receiptId,$at,$receipt);" +
            "INSERT INTO effects VALUES($id);";
        insert.Parameters.AddWithValue("$id", delivery.DeliveryId.ToString("D"));
        insert.Parameters.AddWithValue("$hash", payload.ContentHash);
        insert.Parameters.AddWithValue("$receiptId", receiptId);
        insert.Parameters.AddWithValue("$at", acceptedAt.ToString("O"));
        insert.Parameters.AddWithValue("$receipt", receipt);
        insert.ExecuteNonQuery();
        boundary?.Invoke(OutboxReceiverProbeContract.BeforeCommit);
        transaction.Commit();
        boundary?.Invoke(OutboxReceiverProbeContract.AfterCommit);
        return receipt;
    }

    internal static long EffectCount(string databasePath)
    {
        using var database = Open(databasePath);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM effects";
        return (long)command.ExecuteScalar()!;
    }

    internal static long DeliveryEffectCount(string databasePath, Guid deliveryId)
    {
        using var database = Open(databasePath);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM effects WHERE DeliveryId=$id";
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        return (long)command.ExecuteScalar()!;
    }

    internal static OutboxReceiverStoredReceipt? ReadStoredReceipt(string databasePath, Guid deliveryId)
    {
        using var database = Open(databasePath);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT PayloadHash,ReceiptId,AcceptedAtUtc,Receipt FROM receipts WHERE DeliveryId=$id";
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        using var read = command.ExecuteReader();
        if (!read.Read()) return null;
        return new(read.GetString(0), read.GetString(1),
            DateTimeOffset.ParseExact(read.GetString(2), "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            (byte[])read.GetValue(3));
    }

    internal static ECDsa ImportOwnerKey(ReadOnlySpan<byte> pkcs8)
    {
        if (pkcs8.Length is < 1 or > OutboxReceiverProbeContract.MaximumOwnerKeyBytes)
            throw new InvalidOperationException("OutboxProbeOwnerKeySizeInvalid");
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8.ToArray(), out var consumed);
            if (consumed != pkcs8.Length || key.KeySize != 256)
                throw new InvalidOperationException("OutboxProbeOwnerKeyInvalid");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Freezes the exact reconstructed route, delivery identity and payload bytes a child process
    /// needs to rebuild the same delivery through the production row reconstruction path.
    /// </summary>
    internal static byte[] EncodeProbeInput(OutboxDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var payload = delivery.Payload ??
            throw new ArgumentException("OutboxProbePayloadMissing", nameof(delivery));
        var route = delivery.Route;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("routeId", route.RouteId);
            writer.WriteString("routeVersion", route.Version);
            writer.WriteNumber("criticality", (int)route.Criticality);
            writer.WriteString("destinationIdentity", route.DestinationIdentity);
            writer.WriteString("payloadContractId", route.PayloadContract.Id);
            writer.WriteString("payloadContractVersion", route.PayloadContract.Version);
            writer.WriteString("payloadContractHash", route.PayloadContract.ContentHash);
            writer.WriteString("contentType", route.ContentType);
            writer.WriteString("receiverContractId", route.ReceiverContract.Id);
            writer.WriteString("receiverContractVersion", route.ReceiverContract.Version);
            writer.WriteString("receiverContractHash", route.ReceiverContract.ContentHash);
            writer.WriteString("receiverPublicKeyBase64", route.ReceiverPublicKeyBase64);
            writer.WriteString("adapterContractId", route.AdapterContract.Id);
            writer.WriteString("adapterContractVersion", route.AdapterContract.Version);
            writer.WriteString("adapterContractHash", route.AdapterContract.ContentHash);
            writer.WriteNumber("maximumPayloadBytes", route.MaximumPayloadBytes);
            writer.WriteString("routeContentHash", route.ContentHash);
            writer.WriteString("deliveryId", delivery.DeliveryId.ToString("D"));
            writer.WriteString("inspectionId", delivery.InspectionId.ToString("D"));
            writer.WriteString("coreHash", delivery.CoreHash);
            writer.WriteNumber("position", 1);
            writer.WriteString("payloadBytesBase64", Convert.ToBase64String(payload.CopyBytes()));
            writer.WriteString("payloadContentHash", payload.ContentHash);
            writer.WriteNumber("payloadByteLength", payload.ByteLength);
            writer.WriteString("preparationFailure", delivery.PreparationFailure);
            writer.WriteString("createdAtUtc", delivery.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("maximumAttempts", delivery.MaximumAttempts);
            writer.WriteString("deliveryContentHash", delivery.ContentHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Rebuilds the frozen delivery with the production row reconstruction path, which re-derives
    /// the route hash, the payload binding and the delivery content hash, so an edited or truncated
    /// probe input fails closed instead of becoming a second delivery identity.
    /// </summary>
    internal static OutboxDelivery DecodeProbeInput(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > OutboxReceiverProbeContract.MaximumInputBytes)
            throw new InvalidOperationException("OutboxProbeInputSizeInvalid");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        var payloadBytes = Convert.FromBase64String(Text(root, "payloadBytesBase64"));
        if (payloadBytes.Length is < 1 or > OutboxReceiverProbeContract.MaximumPayloadBytes)
            throw new InvalidOperationException("OutboxProbePayloadSizeInvalid");
        return ProductionOutboxStorageCodec.ReconstructDelivery(
            Guid.Parse(Text(root, "deliveryId")),
            Guid.Parse(Text(root, "inspectionId")),
            Text(root, "coreHash"),
            root.GetProperty("position").GetInt64(),
            Text(root, "routeId"),
            Text(root, "routeVersion"),
            Text(root, "routeContentHash"),
            root.GetProperty("criticality").GetInt32(),
            Text(root, "destinationIdentity"),
            Text(root, "payloadContractId"),
            Text(root, "payloadContractVersion"),
            Text(root, "payloadContractHash"),
            Text(root, "contentType"),
            Text(root, "receiverContractId"),
            Text(root, "receiverContractVersion"),
            Text(root, "receiverContractHash"),
            Text(root, "receiverPublicKeyBase64"),
            Text(root, "adapterContractId"),
            Text(root, "adapterContractVersion"),
            Text(root, "adapterContractHash"),
            root.GetProperty("maximumPayloadBytes").GetInt32(),
            payloadBytes,
            Text(root, "payloadContentHash"),
            root.GetProperty("payloadByteLength").GetInt64(),
            NullableText(root, "preparationFailure"),
            DateTimeOffset.ParseExact(Text(root, "createdAtUtc"), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None),
            root.GetProperty("maximumAttempts").GetInt32(),
            Text(root, "deliveryContentHash"));
    }

    private static string Text(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new InvalidOperationException("OutboxProbeInputInvalid");

    private static string? NullableText(JsonElement root, string name) =>
        root.GetProperty(name).GetString();

    private static byte[] Sign(ECDsa owner, byte[] statement) => OutboxReceiverProtocol.CreateSignedEnvelope(statement,
        owner.SignData(statement, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
