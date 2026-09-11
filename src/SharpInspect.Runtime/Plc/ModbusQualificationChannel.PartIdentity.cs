using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

internal sealed class ModbusPartIdentitySnapshot
{
    internal ModbusPartIdentitySnapshot(ModbusPartIdentityReadPlan plan, ModbusControllerSignals before,
        ModbusControllerSignals after, uint revisionBefore, uint revisionAfter, byte[] block,
        uint revision, uint controllerEpoch, uint cycleSequence, string? value, bool missing, string? failure,
        DateTimeOffset receivedAtUtc, long receivedMonotonicTimestamp)
    {
        Plan = plan;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        Revision = revision;
        Value = value;
        Missing = missing;
        FailureReason = failure;
        ReceivedAtUtc = receivedAtUtc;
        ReceivedMonotonicTimestamp = receivedMonotonicTimestamp;
        RawBlock = Array.AsReadOnly(block.ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-part-identity-observation-v1", plan.ContentHash,
            revisionBefore.ToString(CultureInfo.InvariantCulture), revisionAfter.ToString(CultureInfo.InvariantCulture),
            SignalHash(before), SignalHash(after), Convert.ToBase64String(RawBlock.ToArray()), value,
            missing.ToString(), failure, ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ReceivedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)
        });
    }

    internal ModbusPartIdentityReadPlan Plan { get; }
    internal uint ControllerEpoch { get; }
    internal uint CycleSequence { get; }
    internal uint Revision { get; }
    internal string? Value { get; }
    internal bool Missing { get; }
    internal string? FailureReason { get; }
    internal DateTimeOffset ReceivedAtUtc { get; }
    internal long ReceivedMonotonicTimestamp { get; }
    internal ReadOnlyCollection<byte> RawBlock { get; }
    internal string ContentHash { get; }

    private static string SignalHash(ModbusControllerSignals value) => AlgorithmContractValidation.HashParts(new[]
    {
        value.Trigger.ToString(), value.ResultAck.ToString(),
        value.ControllerEpoch.ToString(CultureInfo.InvariantCulture), value.CycleSequence.ToString(CultureInfo.InvariantCulture)
    });
}

internal sealed partial class ModbusQualificationChannel
{
    private async Task<ModbusPartIdentitySnapshot> ReadPartIdentitySnapshotAsync(ModbusControllerSignals before,
        ModbusPartIdentityReadPlan plan, CancellationToken token)
    {
        // Every request uses the existing channel/owner fence. No independent connection or
        // second provider can read a newer value after the Trigger observation was accepted.
        var revisionBeforeBytes = await ReadIdentityRegistersAsync(plan.StartAddress, 2, token).ConfigureAwait(false);
        var block = await ReadIdentityRegistersAsync(plan.StartAddress, plan.RegisterCount, token).ConfigureAwait(false);
        var receivedAtUtc = DateTimeOffset.UtcNow;
        var receivedTimestamp = Stopwatch.GetTimestamp();
        var revisionAfterBytes = await ReadIdentityRegistersAsync(plan.StartAddress, 2, token).ConfigureAwait(false);
        var after = await ReadRawControllerCoreAsync(token).ConfigureAwait(false);
        var revisionBefore = BinaryPrimitives.ReadUInt32BigEndian(revisionBeforeBytes);
        var revisionAfter = BinaryPrimitives.ReadUInt32BigEndian(revisionAfterBytes);
        var revision = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0, 4));
        var epoch = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4, 4));
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8, 4));
        var state = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(12, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(14, 2));
        string? failure = revision == 0 || (revision & 1) != 0 || revision != revisionBefore || revision != revisionAfter ||
            before != after ? "PartIdentityPlcSnapshotUnstable" :
            epoch != before.ControllerEpoch || sequence != before.CycleSequence ? "PartIdentityPlcCycleMismatch" : null;
        string? value = null;
        var missing = state == 2;
        if (failure is null)
        {
            if (state == 3) failure = "PartIdentityAmbiguous";
            else if (state is not (1 or 2) || length > plan.MaximumValueBytes ||
                missing && length != 0 || !missing && length == 0)
                failure = "PartIdentityPlcValueInvalid";
            else if (block.Skip(16 + length).Any(octet => octet != 0))
                failure = "PartIdentityPlcPaddingInvalid";
            else if (!missing)
            {
                try { value = new UTF8Encoding(false, true).GetString(block, 16, length); }
                catch (DecoderFallbackException) { failure = "PartIdentityPlcUtf8Invalid"; }
            }
        }
        return new(plan, before, after, revisionBefore, revisionAfter, block, revision, epoch, sequence,
            value, missing, failure, receivedAtUtc, receivedTimestamp);
    }

    private async Task<byte[]> ReadIdentityRegistersAsync(ushort start, int count, CancellationToken token)
    {
        var body = await ExecuteRequestAsync(ReadHoldingRegistersFunction, BuildReadRequest(start, count),
            expectedMbapLength: checked((ushort)(3 + count * 2)), token).ConfigureAwait(false);
        if (body.Length != 2 + count * 2 || body[0] != ReadHoldingRegistersFunction || body[1] != count * 2)
        {
            FaultChannel();
            throw new InvalidOperationException("ModbusPartIdentityReadResponseLengthInvalid");
        }
        return body.AsSpan(2).ToArray();
    }
}
