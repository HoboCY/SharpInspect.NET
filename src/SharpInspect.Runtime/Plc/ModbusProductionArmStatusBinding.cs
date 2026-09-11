using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Explicit register binding for the Runtime-owned Production Arm Status observation block.
/// Runtime publishes this block to report why an arm attempt succeeded or was rejected,
/// including the Activated But Not Ready report required after a successful Recipe Change
/// Handshake. The block is a one-way observation: it carries no acknowledgement, request,
/// recipe-selection, activation, or execution authority, never asserts Ready, and is fully
/// separate from Trigger, Busy, Result Valid, Result Acknowledgement, the mutual-heartbeat
/// block, Part Identity, the dedicated Recipe Change Handshake, and every result-payload
/// range. Cause, Outcome, Reason, and Blocked Gate values stay opaque to the transport; the
/// caller owns their meaning.
/// </summary>
public sealed class ModbusProductionArmStatusBinding
{
    /// <summary>
    /// Runtime-owned observation block: Valid(bool), Cause(ushort), Outcome(ushort),
    /// Reason(ushort), BlockedGate(ushort), RuntimeEpoch(UUID), ControllerEpoch(uint32 big
    /// endian two registers), RequestSequence(uint32 big endian two registers),
    /// SelectionCode(uint32 big endian two registers), AttemptId(UUID).
    /// </summary>
    public const int RuntimeRegisterCount = 27;

    public ModbusProductionArmStatusBinding(string id, string version, ushort runtimeStartAddress)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        if (runtimeStartAddress > ushort.MaxValue - RuntimeRegisterCount + 1)
            throw new ArgumentOutOfRangeException(nameof(runtimeStartAddress));

        RuntimeStartAddress = runtimeStartAddress;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-production-arm-status-binding-v1", Id, Version,
            RuntimeStartAddress.ToString(CultureInfo.InvariantCulture),
            RuntimeRegisterCount.ToString(CultureInfo.InvariantCulture),
            "wire:bool-0-or-1;uint16-big-endian;uint32-big-endian;uuid-canonical-rfc4122-n-hex",
            "runtime-layout:valid@0|cause@1|outcome@2|reason@3|blocked-gate@4|" +
            "runtime-epoch@5:8|controller-epoch@13:2|request-sequence@15:2|" +
            "selection-code@17:2|attempt-id@19:8",
            "observation-only:no-acknowledgement;no-controller-write;no-request-authority;" +
            "no-execution-authority",
            "invalid-block:all-fields-zero"
        });
    }

    public string Id { get; }
    public string Version { get; }
    public ushort RuntimeStartAddress { get; }
    public string ContentHash { get; }
    public int RuntimeEndAddressExclusive => RuntimeStartAddress + RuntimeRegisterCount;
}
