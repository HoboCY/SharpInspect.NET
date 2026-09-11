using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Tests;

internal sealed partial class ModbusQualificationTestServer
{
    internal ModbusProductionProfile CreateProductionProfile(PlcCommunicationPolicy? policy = null)
    {
        var wire = CreateCommunicationProfile(policy: policy);
        lock (_stateSync) _productionPeer = true;
        return new("V142.Modbus.Production", "1", wire.LoopbackAddress, wire.Port, wire.UnitId,
            wire.ControllerStartAddress, wire.RuntimeStartAddress, wire.CommunicationBinding!,
            wire.AcknowledgementTimeout);
    }
}
