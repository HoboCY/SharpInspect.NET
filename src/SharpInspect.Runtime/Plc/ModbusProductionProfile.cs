using System.Globalization;
using System.Net;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Plc;

/// <summary>An explicit production Modbus mapping. Registration grants no production authority.</summary>
public sealed class ModbusProductionProfile : IModbusInspectionProfile
{
    public ModbusProductionProfile(string id, string version, string address, int port, byte unitId,
        ushort controllerStartAddress, ushort runtimeStartAddress,
        ModbusCommunicationBinding communicationBinding, TimeSpan acknowledgementTimeout)
        : this(id, version, address, port, unitId, controllerStartAddress, runtimeStartAddress,
            communicationBinding, acknowledgementTimeout, null) { }

    public ModbusProductionProfile(string id, string version, string address, int port, byte unitId,
        ushort controllerStartAddress, ushort runtimeStartAddress,
        ModbusCommunicationBinding communicationBinding, TimeSpan acknowledgementTimeout,
        ModbusPartIdentityReadPlan? partIdentity)
        : this(id, version, address, port, unitId, controllerStartAddress, runtimeStartAddress,
            communicationBinding, acknowledgementTimeout, partIdentity, null) { }

    /// <summary>
    /// Creates a profile with an explicit dedicated Recipe Change Handshake block. The
    /// two pre-existing overloads retain their signatures and, while
    /// <paramref name="recipeChange"/> is null, their exact v1/v2 content hash algorithm.
    /// </summary>
    public ModbusProductionProfile(string id, string version, string address, int port, byte unitId,
        ushort controllerStartAddress, ushort runtimeStartAddress,
        ModbusCommunicationBinding communicationBinding, TimeSpan acknowledgementTimeout,
        ModbusPartIdentityReadPlan? partIdentity, ModbusRecipeChangeBinding? recipeChange)
        : this(id, version, address, port, unitId, controllerStartAddress, runtimeStartAddress,
            communicationBinding, acknowledgementTimeout, partIdentity, recipeChange, null) { }

    /// <summary>
    /// Creates a profile with the optional Runtime-owned Production Arm Status observation
    /// block. The block is additive and grants no acknowledgement, request, activation, or
    /// execution authority. While <paramref name="productionArmStatus"/> is null every
    /// pre-existing overload keeps its exact v1/v2/v3 content hash and algorithm; otherwise
    /// the v4 hash binds that value to the observation block.
    /// </summary>
    public ModbusProductionProfile(string id, string version, string address, int port, byte unitId,
        ushort controllerStartAddress, ushort runtimeStartAddress,
        ModbusCommunicationBinding communicationBinding, TimeSpan acknowledgementTimeout,
        ModbusPartIdentityReadPlan? partIdentity, ModbusRecipeChangeBinding? recipeChange,
        ModbusProductionArmStatusBinding? productionArmStatus)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        if (!IPAddress.TryParse(address, out var parsed) || parsed.Equals(IPAddress.Any) ||
            parsed.Equals(IPAddress.IPv6Any) || parsed.Equals(IPAddress.Broadcast) || parsed.IsIPv6Multicast)
            throw new ArgumentException("ModbusProductionNumericUnicastAddressRequired", nameof(address));
        var bytes = parsed.GetAddressBytes();
        if (bytes.Length == 4 && bytes[0] is >= 224 and <= 239)
            throw new ArgumentException("ModbusProductionNumericUnicastAddressRequired", nameof(address));
        Address = parsed.ToString();
        if (port is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
        if (unitId is 0 or > 247) throw new ArgumentOutOfRangeException(nameof(unitId));
        if (controllerStartAddress > ushort.MaxValue - 5)
            throw new ArgumentOutOfRangeException(nameof(controllerStartAddress));
        if (runtimeStartAddress > ushort.MaxValue - 5)
            throw new ArgumentOutOfRangeException(nameof(runtimeStartAddress));
        CommunicationBinding = communicationBinding ?? throw new ArgumentNullException(nameof(communicationBinding));
        if (acknowledgementTimeout < TimeSpan.FromMilliseconds(1) || acknowledgementTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(acknowledgementTimeout));
        var ranges = new List<(int Start, int Count)>
        {
            (controllerStartAddress, 6), (runtimeStartAddress, 6),
            (communicationBinding.ControllerStartAddress, ModbusCommunicationBinding.ControllerRegisterCount),
            (communicationBinding.RuntimeStartAddress, ModbusCommunicationBinding.RuntimeRegisterCount)
        };
        if (partIdentity is not null) ranges.Add((partIdentity.StartAddress, partIdentity.RegisterCount));
        if (recipeChange is not null)
        {
            ranges.Add((recipeChange.ControllerStartAddress, ModbusRecipeChangeBinding.ControllerRegisterCount));
            ranges.Add((recipeChange.RuntimeStartAddress, ModbusRecipeChangeBinding.RuntimeRegisterCount));
        }
        if (productionArmStatus is not null)
            ranges.Add((productionArmStatus.RuntimeStartAddress,
                ModbusProductionArmStatusBinding.RuntimeRegisterCount));
        for (var left = 0; left < ranges.Count; left++)
            for (var right = left + 1; right < ranges.Count; right++)
                if (ModbusCommunicationBinding.Overlaps(ranges[left].Start, ranges[left].Count,
                        ranges[right].Start, ranges[right].Count))
                    throw new ArgumentException("ModbusProductionRegisterBlocksOverlap");
        Port = port;
        UnitId = unitId;
        ControllerStartAddress = controllerStartAddress;
        RuntimeStartAddress = runtimeStartAddress;
        AcknowledgementTimeout = acknowledgementTimeout;
        PartIdentity = partIdentity;
        RecipeChange = recipeChange;
        ProductionArmStatus = productionArmStatus;
        EndpointBindingHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-production-endpoint-v1", Address,
            port.ToString(CultureInfo.InvariantCulture), unitId.ToString(CultureInfo.InvariantCulture)
        });
        List<string?> profileParts;
        if (recipeChange is null)
        {
            profileParts = new List<string?>
            {
                partIdentity is null ? "sharpinspect-modbus-production-profile-v1" :
                    "sharpinspect-modbus-production-profile-v2", Id, Version, EndpointBindingHash,
                controllerStartAddress.ToString(CultureInfo.InvariantCulture),
                runtimeStartAddress.ToString(CultureInfo.InvariantCulture), communicationBinding.BindingHash,
                acknowledgementTimeout.ToString("c", CultureInfo.InvariantCulture)
            };
            if (partIdentity is not null) profileParts.Add(partIdentity.ContentHash);
        }
        else
        {
            profileParts = new List<string?>
            {
                "sharpinspect-modbus-production-profile-v3", Id, Version, EndpointBindingHash,
                controllerStartAddress.ToString(CultureInfo.InvariantCulture),
                runtimeStartAddress.ToString(CultureInfo.InvariantCulture), communicationBinding.BindingHash,
                acknowledgementTimeout.ToString("c", CultureInfo.InvariantCulture),
                partIdentity is null ? "sharpinspect-modbus-part-identity-absent" : partIdentity.ContentHash,
                recipeChange.ContentHash
            };
        }
        var profileHash = AlgorithmContractValidation.HashParts(profileParts);
        // The observation block is additive: with no binding the historic v1/v2/v3 value and
        // algorithm above are exact, and v4 binds that value to the block so a changed
        // observation binding can never reuse an older profile hash.
        ContentHash = productionArmStatus is null ? profileHash :
            AlgorithmContractValidation.HashParts(new[]
            {
                "sharpinspect-modbus-production-profile-v4", profileHash,
                productionArmStatus.ContentHash
            });
    }

    public string Id { get; }
    public string Version { get; }
    public string Address { get; }
    public int Port { get; }
    public byte UnitId { get; }
    public ushort ControllerStartAddress { get; }
    public ushort RuntimeStartAddress { get; }
    public ModbusCommunicationBinding CommunicationBinding { get; }
    public TimeSpan PollInterval => CommunicationBinding.Policy.PollInterval;
    public TimeSpan TransportTimeout => CommunicationBinding.Policy.OperationTimeout;
    public TimeSpan AcknowledgementTimeout { get; }
    public string EndpointBindingHash { get; }
    public string ContentHash { get; }
    public ModbusPartIdentityReadPlan? PartIdentity { get; }
    public ModbusRecipeChangeBinding? RecipeChange { get; }
    public ModbusProductionArmStatusBinding? ProductionArmStatus { get; }
    int IModbusInspectionProfile.ControllerEndAddressExclusive => ControllerStartAddress + 6;
    int IModbusInspectionProfile.RuntimeEndAddressExclusive => RuntimeStartAddress + 6;
}
