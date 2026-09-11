using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Plc;

// The transport and communication owner share one immutable binding contract.
// Qualification and production profiles retain distinct public identities.
internal interface IModbusInspectionProfile
{
    string Address { get; }
    int Port { get; }
    byte UnitId { get; }
    ushort ControllerStartAddress { get; }
    ushort RuntimeStartAddress { get; }
    int ControllerEndAddressExclusive { get; }
    int RuntimeEndAddressExclusive { get; }
    TimeSpan TransportTimeout { get; }
    ModbusCommunicationBinding? CommunicationBinding { get; }
}
