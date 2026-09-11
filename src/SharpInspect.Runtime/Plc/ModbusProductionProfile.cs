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
        var ranges = new (int Start, int Count)[]
        {
            (controllerStartAddress, 6), (runtimeStartAddress, 6),
            (communicationBinding.ControllerStartAddress, ModbusCommunicationBinding.ControllerRegisterCount),
            (communicationBinding.RuntimeStartAddress, ModbusCommunicationBinding.RuntimeRegisterCount)
        };
        for (var left = 0; left < ranges.Length; left++)
            for (var right = left + 1; right < ranges.Length; right++)
                if (ModbusCommunicationBinding.Overlaps(ranges[left].Start, ranges[left].Count,
                        ranges[right].Start, ranges[right].Count))
                    throw new ArgumentException("ModbusProductionRegisterBlocksOverlap");
        Port = port;
        UnitId = unitId;
        ControllerStartAddress = controllerStartAddress;
        RuntimeStartAddress = runtimeStartAddress;
        AcknowledgementTimeout = acknowledgementTimeout;
        EndpointBindingHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-production-endpoint-v1", Address,
            port.ToString(CultureInfo.InvariantCulture), unitId.ToString(CultureInfo.InvariantCulture)
        });
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-production-profile-v1", Id, Version, EndpointBindingHash,
            controllerStartAddress.ToString(CultureInfo.InvariantCulture),
            runtimeStartAddress.ToString(CultureInfo.InvariantCulture), communicationBinding.BindingHash,
            acknowledgementTimeout.ToString("c", CultureInfo.InvariantCulture)
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
    int IModbusInspectionProfile.ControllerEndAddressExclusive => ControllerStartAddress + 6;
    int IModbusInspectionProfile.RuntimeEndAddressExclusive => RuntimeStartAddress + 6;
}
