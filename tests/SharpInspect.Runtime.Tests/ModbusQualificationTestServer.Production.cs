using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Tests;

internal sealed partial class ModbusQualificationTestServer
{
    internal ModbusProductionProfile CreateProductionProfile(PlcCommunicationPolicy? policy = null,
        ModbusPartIdentityReadPlan? partIdentity = null, ModbusRecipeChangeBinding? recipeChange = null,
        ModbusProductionArmStatusBinding? productionArmStatus = null)
    {
        var wire = CreateCommunicationProfile(policy: policy);
        lock (_stateSync)
        {
            _productionPeer = true;
            _partIdentityPlan = partIdentity;
            _recipeChangeBinding = recipeChange;
            _productionArmStatusBinding = productionArmStatus;
        }
        return new("V142.Modbus.Production", "1", wire.LoopbackAddress, wire.Port, wire.UnitId,
            wire.ControllerStartAddress, wire.RuntimeStartAddress, wire.CommunicationBinding!,
            wire.AcknowledgementTimeout, partIdentity, recipeChange, productionArmStatus);
    }
}
