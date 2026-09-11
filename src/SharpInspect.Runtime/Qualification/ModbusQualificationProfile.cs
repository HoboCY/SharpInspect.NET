using System.Globalization;
using System.Net;

using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

/// <summary>
/// Explicit, development-only Modbus TCP binding for a qualification controller.
/// The profile contains no connection state and grants no production authority.
/// </summary>
public sealed class ModbusQualificationProfile : SharpInspect.Runtime.Plc.IModbusInspectionProfile
{
    internal const int ControllerRegisterCount = 6;
    internal const int RuntimeRegisterCount = 6;
    internal const int MaximumRegisterAddress = ushort.MaxValue;
    internal const int MinimumTimeoutMilliseconds = 1;
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(5);

    public ModbusQualificationProfile(
        string id,
        string version,
        string scenarioId,
        string loopbackAddress,
        int port,
        byte unitId,
        ushort controllerStartAddress,
        ushort runtimeStartAddress,
        long tracePolicyVersion,
        string tracePolicySnapshotHash,
        TimeSpan pollInterval,
        TimeSpan transportTimeout,
        TimeSpan acknowledgementTimeout,
        QualificationEvidenceCaptureMode evidenceMode)
        : this(id, version, scenarioId, loopbackAddress, port, unitId,
            controllerStartAddress, runtimeStartAddress, tracePolicyVersion,
            tracePolicySnapshotHash, pollInterval, transportTimeout,
            acknowledgementTimeout, evidenceMode, communicationBinding: null)
    {
    }

    /// <summary>
    /// Creates a profile with an explicit mutual-heartbeat communication
    /// binding.  The original constructor remains available and retains its
    /// v1 profile hash when no binding is supplied.
    /// </summary>
    public ModbusQualificationProfile(
        string id,
        string version,
        string scenarioId,
        string loopbackAddress,
        int port,
        byte unitId,
        ushort controllerStartAddress,
        ushort runtimeStartAddress,
        long tracePolicyVersion,
        string tracePolicySnapshotHash,
        TimeSpan pollInterval,
        TimeSpan transportTimeout,
        TimeSpan acknowledgementTimeout,
        QualificationEvidenceCaptureMode evidenceMode,
        ModbusCommunicationBinding? communicationBinding)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        ScenarioId = AlgorithmContractValidation.Identifier(scenarioId, nameof(scenarioId));

        if (!IPAddress.TryParse(loopbackAddress, out var parsedAddress) || parsedAddress is null ||
            !IPAddress.IsLoopback(parsedAddress))
            throw new ArgumentException("ModbusQualificationLoopbackAddressRequired", nameof(loopbackAddress));

        // Hash and expose the canonical numeric address. Host names and alternate DNS
        // spellings must never be able to change a binding after registration.
        LoopbackAddress = parsedAddress.ToString();
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (unitId is 0 or > 247)
            throw new ArgumentOutOfRangeException(nameof(unitId));
        if (controllerStartAddress > ushort.MaxValue - ControllerRegisterCount + 1)
            throw new ArgumentOutOfRangeException(nameof(controllerStartAddress));
        if (runtimeStartAddress > ushort.MaxValue - RuntimeRegisterCount + 1)
            throw new ArgumentOutOfRangeException(nameof(runtimeStartAddress));

        var controllerEnd = checked(controllerStartAddress + ControllerRegisterCount);
        var runtimeEnd = checked(runtimeStartAddress + RuntimeRegisterCount);
        if (controllerStartAddress < runtimeEnd && runtimeStartAddress < controllerEnd)
            throw new ArgumentException("ModbusQualificationRegisterBlocksOverlap");

        if (tracePolicyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(tracePolicyVersion));

        TracePolicySnapshotHash = AlgorithmConfigurationValidation.Hash(
            tracePolicySnapshotHash, nameof(tracePolicySnapshotHash)).ToUpperInvariant();
        PollInterval = ValidateTimeout(pollInterval, nameof(pollInterval));
        TransportTimeout = ValidateTimeout(transportTimeout, nameof(transportTimeout));
        AcknowledgementTimeout = ValidateTimeout(acknowledgementTimeout, nameof(acknowledgementTimeout));
        if (evidenceMode != QualificationEvidenceCaptureMode.None)
            throw new ArgumentOutOfRangeException(nameof(evidenceMode), "QualificationEvidenceNoneRequired");
        EvidenceMode = evidenceMode;

        if (communicationBinding is not null)
        {
            if (pollInterval != communicationBinding.Policy.PollInterval)
                throw new ArgumentException("ModbusCommunicationPolicyPollIntervalMismatch",
                    nameof(pollInterval));
            if (transportTimeout != communicationBinding.Policy.OperationTimeout)
                throw new ArgumentException("ModbusCommunicationPolicyOperationTimeoutMismatch",
                    nameof(transportTimeout));

            if (ModbusCommunicationBinding.Overlaps(controllerStartAddress,
                    ControllerRegisterCount, communicationBinding.ControllerStartAddress,
                    ModbusCommunicationBinding.ControllerRegisterCount) ||
                ModbusCommunicationBinding.Overlaps(controllerStartAddress,
                    ControllerRegisterCount, communicationBinding.RuntimeStartAddress,
                    ModbusCommunicationBinding.RuntimeRegisterCount) ||
                ModbusCommunicationBinding.Overlaps(runtimeStartAddress,
                    RuntimeRegisterCount, communicationBinding.ControllerStartAddress,
                    ModbusCommunicationBinding.ControllerRegisterCount) ||
                ModbusCommunicationBinding.Overlaps(runtimeStartAddress,
                    RuntimeRegisterCount, communicationBinding.RuntimeStartAddress,
                    ModbusCommunicationBinding.RuntimeRegisterCount))
                throw new ArgumentException("ModbusCommunicationRegisterBlocksOverlapControl");
        }

        Port = port;
        UnitId = unitId;
        ControllerStartAddress = controllerStartAddress;
        RuntimeStartAddress = runtimeStartAddress;
        TracePolicyVersion = tracePolicyVersion;
        CommunicationBinding = communicationBinding;

        EndpointBindingHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-modbus-qualification-endpoint-v1",
            LoopbackAddress,
            Port.ToString(CultureInfo.InvariantCulture),
            UnitId.ToString(CultureInfo.InvariantCulture)
        });
        var profileParts = new List<string?>
        {
            "sharpinspect-modbus-qualification-profile-v1",
            Id,
            Version,
            ScenarioId,
            LoopbackAddress,
            Port.ToString(CultureInfo.InvariantCulture),
            UnitId.ToString(CultureInfo.InvariantCulture),
            ControllerStartAddress.ToString(CultureInfo.InvariantCulture),
            RuntimeStartAddress.ToString(CultureInfo.InvariantCulture),
            TracePolicyVersion.ToString(CultureInfo.InvariantCulture),
            TracePolicySnapshotHash,
            EndpointBindingHash,
            PollInterval.ToString("c", CultureInfo.InvariantCulture),
            TransportTimeout.ToString("c", CultureInfo.InvariantCulture),
            AcknowledgementTimeout.ToString("c", CultureInfo.InvariantCulture),
            EvidenceMode.ToString()
        };
        if (communicationBinding is not null)
            profileParts.Add(communicationBinding.BindingHash);
        ContentHash = AlgorithmContractValidation.HashParts(profileParts);
    }

    public string Id { get; }
    public string Version { get; }
    public string ScenarioId { get; }
    public string LoopbackAddress { get; }
    string SharpInspect.Runtime.Plc.IModbusInspectionProfile.Address => LoopbackAddress;
    int SharpInspect.Runtime.Plc.IModbusInspectionProfile.ControllerEndAddressExclusive => ControllerEndAddressExclusive;
    int SharpInspect.Runtime.Plc.IModbusInspectionProfile.RuntimeEndAddressExclusive => RuntimeEndAddressExclusive;
    public int Port { get; }
    public byte UnitId { get; }
    public ushort ControllerStartAddress { get; }
    public ushort RuntimeStartAddress { get; }
    public long TracePolicyVersion { get; }
    public string TracePolicySnapshotHash { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan TransportTimeout { get; }
    public TimeSpan AcknowledgementTimeout { get; }
    public QualificationEvidenceCaptureMode EvidenceMode { get; }
    public ModbusCommunicationBinding? CommunicationBinding { get; }
    public PlcCommunicationPolicy? CommunicationPolicy => CommunicationBinding?.Policy;
    public string EndpointBindingHash { get; }
    public string ContentHash { get; }

    public int ControllerEndAddressExclusive => ControllerStartAddress + ControllerRegisterCount;
    public int RuntimeEndAddressExclusive => RuntimeStartAddress + RuntimeRegisterCount;

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(MinimumTimeoutMilliseconds) || value > MaximumTimeout)
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}
