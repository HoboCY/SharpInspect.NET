using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

/// <summary>
/// Explicit register binding for the mutual PLC communication block.  The
/// block is independent from the existing six-register handshake blocks and
/// from every result-payload range.
/// </summary>
public sealed class ModbusCommunicationBinding
{
    public const int ControllerRegisterCount = 12;
    public const int RuntimeRegisterCount = 10;

    /// <summary>
    /// Creates a binding using the policy-first argument order used by the
    /// qualification fixture and service composition helpers.
    /// </summary>
    public ModbusCommunicationBinding(PlcCommunicationPolicy policy,
        ushort controllerStartAddress, ushort runtimeStartAddress)
        : this(controllerStartAddress, runtimeStartAddress, policy)
    {
    }

    public ModbusCommunicationBinding(ushort controllerStartAddress,
        ushort runtimeStartAddress, PlcCommunicationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateRange(controllerStartAddress, ControllerRegisterCount,
            nameof(controllerStartAddress));
        ValidateRange(runtimeStartAddress, RuntimeRegisterCount,
            nameof(runtimeStartAddress));
        if (Overlaps(controllerStartAddress, ControllerRegisterCount,
                runtimeStartAddress, RuntimeRegisterCount))
            throw new ArgumentException("ModbusCommunicationRegisterBlocksOverlap");

        ControllerStartAddress = controllerStartAddress;
        RuntimeStartAddress = runtimeStartAddress;
        Policy = policy;
        BindingHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-communication-binding-v1",
            ControllerStartAddress.ToString(CultureInfo.InvariantCulture),
            ControllerRegisterCount.ToString(CultureInfo.InvariantCulture),
            RuntimeStartAddress.ToString(CultureInfo.InvariantCulture),
            RuntimeRegisterCount.ToString(CultureInfo.InvariantCulture),
            "wire:uint32-big-endian;guid:dotnet-byte-array-v1",
            "controller-layout:heartbeat-u32@0|runtime-epoch-guid@4:16|runtime-heartbeat-u32@20",
            "runtime-layout:runtime-epoch-guid@0:16|runtime-heartbeat-u32@16",
            Policy.ContentHash
        });
    }

    public ushort ControllerStartAddress { get; }
    public ushort RuntimeStartAddress { get; }
    public PlcCommunicationPolicy Policy { get; }
    public string BindingHash { get; }
    public string ContentHash => BindingHash;
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
