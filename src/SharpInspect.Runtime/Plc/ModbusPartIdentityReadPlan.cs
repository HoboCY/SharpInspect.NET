using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Controller-owned UTF-8 identity block. The controller sets an odd revision while updating,
/// writes the complete block, then publishes a new nonzero even revision. A revision is never
/// reused in one controller epoch, including when successive parts have the same business code.
/// </summary>
public sealed class ModbusPartIdentityReadPlan
{
    public ModbusPartIdentityReadPlan(string id, string version, ushort startAddress, int maximumValueBytes)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        // FC03 permits 125 registers: 8 header registers and at most 117 data registers.
        if (maximumValueBytes is < 1 or > 234) throw new ArgumentOutOfRangeException(nameof(maximumValueBytes));
        RegisterCount = 8 + (maximumValueBytes + 1) / 2;
        if (startAddress + RegisterCount > 65536) throw new ArgumentOutOfRangeException(nameof(startAddress));
        StartAddress = startAddress;
        MaximumValueBytes = maximumValueBytes;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-part-identity-seqlock-utf8-v1", Id, Version,
            startAddress.ToString(CultureInfo.InvariantCulture), maximumValueBytes.ToString(CultureInfo.InvariantCulture),
            "RevisionU32BE,ControllerEpochU32BE,CycleSequenceU32BE,StateU16BE,LengthU16BE,Utf8ZeroPadded",
            "Provided=1,Missing=2,Ambiguous=3;UniqueNonzeroEvenRevisionPerControllerEpoch"
        });
        Reference = new(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public ushort StartAddress { get; }
    public int MaximumValueBytes { get; }
    public int RegisterCount { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference { get; }
}
