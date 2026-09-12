using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.OutboxProbe;
using SharpInspect.Runtime.Outbox;

namespace SharpInspect.AlgorithmHangProbe;

/// <summary>
/// Test-only process host for the isolated receiver. The parent kills this process at one reported
/// durability boundary, and a later child process replays the same delivery identity and exact
/// bytes. This exercises the receiver side only: it makes no sender/Core-transaction claim.
/// </summary>
internal static class OutboxReceiverProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not place exception messages, paths, or stack traces in the probe contract.
            WriteEvent(OutboxReceiverProbeContract.ErrorEvent, "probe", "ProbeFailure",
                Environment.ProcessId);
            return 3;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        // dotnet SharpInspect.AlgorithmHangProbe.dll outbox-receiver <database> <input> <ownerKey> <phase>
        if (args.Length != 4) return 2;
        var databasePath = args[0];
        var inputPath = args[1];
        var ownerKeyPath = args[2];
        var phase = args[3];
        if (!OutboxReceiverProbeContract.IsPhase(phase) || !File.Exists(databasePath)) return 2;
        var executedPid = Environment.ProcessId;
        OutboxDelivery delivery;
        ECDsa owner;
        try
        {
            delivery = IsolatedOutboxReceiver.DecodeProbeInput(
                await ReadBoundedAsync(inputPath, OutboxReceiverProbeContract.MaximumInputBytes).ConfigureAwait(false));
            owner = IsolatedOutboxReceiver.ImportOwnerKey(
                await ReadBoundedAsync(ownerKeyPath, OutboxReceiverProbeContract.MaximumOwnerKeyBytes)
                    .ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteEvent(OutboxReceiverProbeContract.ErrorEvent, phase, "ProbeInputRejected", executedPid);
            return 2;
        }

        using var ownerKey = owner;
        var payload = delivery.Payload!; // The probe input always carries exact payload bytes.
        if (phase == OutboxReceiverProbeContract.BeforeSend)
            WaitAtBoundary(phase, delivery, executedPid);
        byte[] receipt;
        try
        {
            // Only the requested boundary blocks; every other boundary stays a no-op so a replay
            // child with no boundary phase runs the acceptance path to completion.
            receipt = IsolatedOutboxReceiver.Accept(databasePath, ownerKey, delivery,
                current =>
                {
                    if (current == phase) WaitAtBoundary(current, delivery, executedPid);
                });
        }
        catch (InvalidOperationException error) when (error.Message == OutboxReceiverProbeContract.ConflictReason)
        {
            WriteEvent(OutboxReceiverProbeContract.ConflictEvent, phase, "Conflict", executedPid,
                new Dictionary<string, object?>
                {
                    ["deliveryId"] = delivery.DeliveryId.ToString("D"),
                    ["payloadHash"] = payload.ContentHash,
                    ["conflict"] = OutboxReceiverProbeContract.ConflictReason,
                    ["effectCount"] = IsolatedOutboxReceiver.EffectCount(databasePath),
                    ["deliveryEffectCount"] =
                        IsolatedOutboxReceiver.DeliveryEffectCount(databasePath, delivery.DeliveryId)
                });
            return 0;
        }

        if (receipt.Length is < 1 or > OutboxReceiverProbeContract.MaximumReceiptBytes)
        {
            WriteEvent(OutboxReceiverProbeContract.ErrorEvent, phase, "ProbeReceiptInvalid", executedPid);
            return 3;
        }

        WriteEvent(OutboxReceiverProbeContract.AcceptEvent, phase, "Accepted", executedPid,
            new Dictionary<string, object?>
            {
                ["deliveryId"] = delivery.DeliveryId.ToString("D"),
                ["deliveryContentHash"] = delivery.ContentHash,
                ["payloadHash"] = payload.ContentHash,
                ["payloadBytes"] = payload.ByteLength,
                ["receiptHash"] = Convert.ToHexString(SHA256.HashData(receipt)),
                ["receiptBase64"] = Convert.ToBase64String(receipt),
                ["effectCount"] = IsolatedOutboxReceiver.EffectCount(databasePath),
                ["deliveryEffectCount"] =
                    IsolatedOutboxReceiver.DeliveryEffectCount(databasePath, delivery.DeliveryId)
            });
        return 0;
    }

    /// <summary>
    /// Reports a flushed boundary line and blocks. Only the parent test may terminate this process;
    /// the killed path deliberately leaves no disposal, rollback or ack output behind.
    /// </summary>
    private static void WaitAtBoundary(string phase, OutboxDelivery delivery, int executedPid)
    {
        WriteEvent(OutboxReceiverProbeContract.BoundaryEvent, phase, "Blocked", executedPid,
            new Dictionary<string, object?>
            {
                ["deliveryId"] = delivery.DeliveryId.ToString("D"),
                ["payloadHash"] = delivery.Payload!.ContentHash
            });
        Console.WriteLine(OutboxReceiverProbeContract.BoundaryPrefix + phase);
        Console.Out.Flush();
        using var block = new ManualResetEvent(false);
        block.WaitOne(TimeSpan.FromMinutes(1));
        throw new TimeoutException("ProbeParentDidNotTerminate");
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1) throw new InvalidOperationException("ProbeInputMissing");
        if (info.Length > maximumBytes) throw new InvalidOperationException("ProbeInputTooLarge");
        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }

    private static void WriteEvent(string eventName, string phase, string status, int executedPid,
        Dictionary<string, object?>? fields = null)
    {
        var payload = fields ?? new Dictionary<string, object?>();
        payload["event"] = eventName;
        payload["phase"] = phase;
        payload["eventStatus"] = status;
        payload["executedPid"] = executedPid;
        Console.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }
}
