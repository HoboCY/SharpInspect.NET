using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ModbusCommunicationTransportTests
{
    [Fact]
    public void V141_M04_BindingUsesDedicated12And10RegisterBlocks()
    {
        var policy = PlcCommunicationPolicyTests.Policy();
        var binding = new ModbusCommunicationBinding(300, 320, policy);

        Assert.Equal(12, binding.ControllerEndAddressExclusive - binding.ControllerStartAddress);
        Assert.Equal(10, binding.RuntimeEndAddressExclusive - binding.RuntimeStartAddress);
        Assert.NotEqual(string.Empty, binding.BindingHash);
    }

    [Fact]
    public void V141_M05_ProfileBindsPolicyTimesAndAddsBindingToNewHash()
    {
        var policy = PlcCommunicationPolicyTests.Policy();
        var binding = new ModbusCommunicationBinding(300, 320, policy);
        var legacy = new ModbusQualificationProfile("T141.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 1, 100, 200, 1, Hash('A'), policy.PollInterval,
            policy.OperationTimeout, TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None);
        var bound = new ModbusQualificationProfile("T141.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 1, 100, 200, 1, Hash('A'), policy.PollInterval,
            policy.OperationTimeout, TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None, binding);

        Assert.Null(legacy.CommunicationBinding);
        Assert.Same(binding, bound.CommunicationBinding);
        Assert.NotEqual(legacy.ContentHash, bound.ContentHash);
        Assert.Equal(policy.ContentHash, bound.CommunicationPolicy!.ContentHash);
    }

    [Fact]
    public void V141_M06_ProfileRejectsCommunicationControlOverlapOrPolicyMismatch()
    {
        var policy = PlcCommunicationPolicyTests.Policy();
        var overlap = new ModbusCommunicationBinding(200, 320, policy);
        Assert.Throws<ArgumentException>(() => Profile(policy, overlap));

        var mismatchedPolicy = new PlcCommunicationPolicy("T141.PlcCommunication", "2",
            TimeSpan.FromMilliseconds(30), policy.OperationTimeout,
            policy.RuntimeHeartbeatInterval, policy.ControllerHeartbeatInterval,
            policy.ControllerHeartbeatStaleAfter, policy.RuntimeHeartbeatStaleAfter,
            policy.ReconnectInterval, policy.MaximumReconnectAttempts,
            policy.SynchronizationStabilityWindow, policy.SynchronizationTimeout);
        var binding = new ModbusCommunicationBinding(300, 320, mismatchedPolicy);
        Assert.Throws<ArgumentException>(() => Profile(policy, binding));
    }

    private static ModbusQualificationProfile Profile(PlcCommunicationPolicy policy,
        ModbusCommunicationBinding binding) => new("T141.Modbus", "1", "Scenario.A",
            "127.0.0.1", 1502, 1, 100, 200, 1, Hash('A'), policy.PollInterval,
            policy.OperationTimeout, TimeSpan.FromSeconds(1),
            QualificationEvidenceCaptureMode.None, binding);

    private static string Hash(char value) => new(value, 64);
}
