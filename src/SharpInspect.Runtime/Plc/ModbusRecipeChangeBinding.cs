using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Explicit register binding for the dedicated Recipe Change Handshake. The controller-owned
/// request block and the runtime-owned response block are fully separate from Trigger, Busy,
/// Result Valid, Result Acknowledgement, the mutual-heartbeat block, Part Identity, and every
/// result-payload range. The binding carries no recipe-selection policy, activation authority,
/// or outcome semantics; those stay with the caller.
/// </summary>
public sealed class ModbusRecipeChangeBinding
{
    /// <summary>
    /// Controller-owned request block: Request(bool), Acknowledgement(bool),
    /// RequestSequence(uint32 big endian two registers), SelectionCode(uint32 big endian
    /// two registers).
    /// </summary>
    public const int ControllerRegisterCount = 6;

    /// <summary>
    /// Runtime-owned response block: ResponseValid(bool), Outcome(ushort), Reason(ushort),
    /// RequestSequence(uint32 big endian two registers), SelectionCode(uint32 big endian
    /// two registers).
    /// </summary>
    public const int RuntimeRegisterCount = 7;

    public ModbusRecipeChangeBinding(ushort controllerStartAddress, ushort runtimeStartAddress,
        TimeSpan handshakeTimeout)
    {
        ValidateRange(controllerStartAddress, ControllerRegisterCount, nameof(controllerStartAddress));
        ValidateRange(runtimeStartAddress, RuntimeRegisterCount, nameof(runtimeStartAddress));
        if (Overlaps(controllerStartAddress, ControllerRegisterCount, runtimeStartAddress,
                RuntimeRegisterCount))
            throw new ArgumentException("ModbusRecipeChangeRegisterBlocksOverlap");
        if (handshakeTimeout < TimeSpan.FromMilliseconds(1) || handshakeTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

        ControllerStartAddress = controllerStartAddress;
        RuntimeStartAddress = runtimeStartAddress;
        HandshakeTimeout = handshakeTimeout;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-recipe-change-binding-v1",
            ControllerStartAddress.ToString(CultureInfo.InvariantCulture),
            ControllerRegisterCount.ToString(CultureInfo.InvariantCulture),
            RuntimeStartAddress.ToString(CultureInfo.InvariantCulture),
            RuntimeRegisterCount.ToString(CultureInfo.InvariantCulture),
            "wire:bool-0-or-1;uint32-big-endian;uint16-big-endian",
            "controller-layout:request@0|acknowledgement@1|request-sequence-u32@2:2|selection-code-u32@4:2",
            "runtime-layout:response-valid@0|outcome@1|reason@2|request-sequence-u32@3:2|selection-code-u32@5:2",
            "request-identity:controller-epoch+dedicated-request-sequence;never-cycle-sequence",
            HandshakeTimeout.ToString("c", CultureInfo.InvariantCulture)
        });
    }

    public ushort ControllerStartAddress { get; }
    public ushort RuntimeStartAddress { get; }
    public TimeSpan HandshakeTimeout { get; }
    public string ContentHash { get; }
    public int ControllerEndAddressExclusive => ControllerStartAddress + ControllerRegisterCount;
    public int RuntimeEndAddressExclusive => RuntimeStartAddress + RuntimeRegisterCount;

    internal static bool Overlaps(int leftStart, int leftCount, int rightStart, int rightCount) =>
        leftStart < rightStart + rightCount && rightStart < leftStart + leftCount;

    private static void ValidateRange(ushort start, int count, string parameterName)
    {
        if (start > ushort.MaxValue - count + 1)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
