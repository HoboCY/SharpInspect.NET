using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

public enum VirtualCameraSignalKind { Frame, Disconnect }
public enum VirtualFrameAssociation { CurrentRequest, Uncorrelated, PreviousRequest }
public enum VirtualCameraConfigurationOutcome { Success, WriteFailure, ReadBackFailure }
public enum VirtualCameraOpenOutcome { Success, DeviceMissing, ConnectionFailure }

/// <summary>One ordered simulator observation, relative to the accepted acquisition.</summary>
public sealed class VirtualCameraSignal
{
    public VirtualCameraSignal(TimeSpan offset, VirtualCameraSignalKind kind,
        string? imageId = null, VirtualFrameAssociation association = VirtualFrameAssociation.CurrentRequest)
    {
        VirtualCameraContract.Duration(offset, nameof(offset));
        if (!Enum.IsDefined(typeof(VirtualCameraSignalKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(VirtualFrameAssociation), association)) throw new ArgumentOutOfRangeException(nameof(association));
        if (kind == VirtualCameraSignalKind.Frame)
            ImageId = VirtualCameraContract.Identifier(imageId, nameof(imageId));
        else if (imageId is not null || association != VirtualFrameAssociation.CurrentRequest)
            throw new ArgumentException("VirtualDisconnectFrameDataForbidden");
        Offset = offset; Kind = kind; Association = association;
    }
    public TimeSpan Offset { get; }
    public VirtualCameraSignalKind Kind { get; }
    public string? ImageId { get; }
    public VirtualFrameAssociation Association { get; }
}

/// <summary>One attempt. An empty plan waits until its acquisition deadline.</summary>
public sealed class VirtualCameraAcquisitionPlan
{
    public VirtualCameraAcquisitionPlan(IEnumerable<VirtualCameraSignal> signals)
    {
        Signals = VirtualCameraContract.Copy(signals, 32, nameof(signals));
        long previous = -1;
        foreach (var signal in Signals)
        {
            if (signal.Offset.Ticks < previous) throw new ArgumentException("VirtualSignalsNotOrdered", nameof(signals));
            previous = signal.Offset.Ticks;
        }
    }
    public IReadOnlyList<VirtualCameraSignal> Signals { get; }
}

/// <summary>A complete simulated application/read-back attempt, never per-field partial success.</summary>
public sealed class VirtualCameraConfigurationPlan
{
    public VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome outcome, TimeSpan delay)
    {
        if (!Enum.IsDefined(typeof(VirtualCameraConfigurationOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        VirtualCameraContract.Duration(delay, nameof(delay));
        Outcome = outcome; Delay = delay;
    }
    public VirtualCameraConfigurationOutcome Outcome { get; }
    public TimeSpan Delay { get; }
}

/// <summary>
/// Immutable development input. Acquisition/configuration/open cursors belong to a
/// provider session and survive device reopen; a scenario never loops acquisitions.
/// </summary>
public sealed partial class VirtualCameraScenario
{
    public const string SimulatorVersion = "SharpInspect.VirtualCamera.v1";
    public const int MaximumImages = 64;
    public const int MaximumAcquisitions = 64;
    public const int MaximumSignals = 64;
    public const long MaximumImageBytes = 64L * 1024 * 1024;

    public VirtualCameraScenario(string id, string version, uint seed, string stableDeviceIdentity,
        CameraCapabilities capabilities, IEnumerable<VirtualCameraImage> images,
        IEnumerable<VirtualCameraAcquisitionPlan> acquisitions,
        IEnumerable<VirtualCameraConfigurationPlan>? configurations = null,
        IEnumerable<VirtualCameraOpenOutcome>? openingOutcomes = null,
        IEnumerable<VirtualCameraSignal>? unsolicitedSignals = null, string? reportedModel = null)
        : this(id, version, seed, stableDeviceIdentity, capabilities, images, acquisitions,
            configurations, openingOutcomes, unsolicitedSignals, reportedModel, null)
    {
    }

    /// <summary>
    /// Creates a scenario with an explicit development-only preview extension.
    /// This is a factory so the historical eleven-argument constructor retains
    /// its CLR signature and its default byte stream.
    /// </summary>
    public static VirtualCameraScenario WithPreview(string id, string version, uint seed,
        string stableDeviceIdentity, CameraCapabilities capabilities,
        IEnumerable<VirtualCameraImage> images,
        IEnumerable<VirtualCameraAcquisitionPlan> acquisitions,
        VirtualCameraPreviewScenario preview,
        IEnumerable<VirtualCameraConfigurationPlan>? configurations = null,
        IEnumerable<VirtualCameraOpenOutcome>? openingOutcomes = null,
        IEnumerable<VirtualCameraSignal>? unsolicitedSignals = null, string? reportedModel = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new VirtualCameraScenario(id, version, seed, stableDeviceIdentity, capabilities,
            images, acquisitions, configurations, openingOutcomes, unsolicitedSignals,
            reportedModel, preview);
    }

    private VirtualCameraScenario(string id, string version, uint seed, string stableDeviceIdentity,
        CameraCapabilities capabilities, IEnumerable<VirtualCameraImage> images,
        IEnumerable<VirtualCameraAcquisitionPlan> acquisitions,
        IEnumerable<VirtualCameraConfigurationPlan>? configurations,
        IEnumerable<VirtualCameraOpenOutcome>? openingOutcomes,
        IEnumerable<VirtualCameraSignal>? unsolicitedSignals, string? reportedModel,
        VirtualCameraPreviewScenario? preview)
    {
        Id = VirtualCameraContract.Identifier(id, nameof(id));
        Version = VirtualCameraContract.Identifier(version, nameof(version));
        StableDeviceIdentity = VirtualCameraContract.Identifier(stableDeviceIdentity, nameof(stableDeviceIdentity), 128);
        ReportedModel = reportedModel is null ? null : VirtualCameraContract.Text(reportedModel, nameof(reportedModel), 128);
        Seed = seed; Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Images = VirtualCameraContract.Copy(images, MaximumImages, nameof(images));
        if (Images.Count == 0) throw new ArgumentException("VirtualScenarioImagesRequired", nameof(images));
        var byId = new Dictionary<string, VirtualCameraImage>(StringComparer.Ordinal);
        long bytes = 0;
        long maximumFrameBytes = 0;
        foreach (var image in Images)
        {
            if (!byId.TryAdd(image.Id, image)) throw new ArgumentException("VirtualScenarioImageIdDuplicate", nameof(images));
            bytes = checked(bytes + (long)image.StrideBytes * image.Height);
            if (bytes > MaximumImageBytes) throw new ArgumentException("VirtualScenarioImageCapacityExceeded", nameof(images));
            // The shared Runtime pool aligns odd Mono16 row steps for native consumers.
            var stride = image.PixelFormat == VisionPixelFormat.Mono16
                ? checked(image.StrideBytes + (image.StrideBytes & 1)) : image.StrideBytes;
            maximumFrameBytes = Math.Max(maximumFrameBytes, checked((long)stride * image.Height));
        }
        ImageBytes = bytes; MaximumFrameBytes = checked((int)maximumFrameBytes);
        ImagesById = new ReadOnlyDictionary<string, VirtualCameraImage>(byId);
        Acquisitions = VirtualCameraContract.Copy(acquisitions, MaximumAcquisitions, nameof(acquisitions));
        Configurations = VirtualCameraContract.Copy(configurations ?? Array.Empty<VirtualCameraConfigurationPlan>(), 64, nameof(configurations));
        var opens = new List<VirtualCameraOpenOutcome>();
        foreach (var outcome in openingOutcomes ?? Array.Empty<VirtualCameraOpenOutcome>())
        {
            if (opens.Count == 64) throw new ArgumentException("VirtualOpenPlanCapacityExceeded", nameof(openingOutcomes));
            if (!Enum.IsDefined(typeof(VirtualCameraOpenOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(openingOutcomes));
            opens.Add(outcome);
        }
        OpeningOutcomes = opens.AsReadOnly();
        UnsolicitedSignals = VirtualCameraContract.Copy(unsolicitedSignals ?? Array.Empty<VirtualCameraSignal>(), 16, nameof(unsolicitedSignals));
        Preview = preview;
        long previousOffset = -1;
        foreach (var signal in UnsolicitedSignals)
        {
            if (signal.Kind != VirtualCameraSignalKind.Frame || signal.Association != VirtualFrameAssociation.Uncorrelated)
                throw new ArgumentException("VirtualUnsolicitedSignalInvalid", nameof(unsolicitedSignals));
            if (signal.Offset.Ticks < previousOffset) throw new ArgumentException("VirtualSignalsNotOrdered", nameof(unsolicitedSignals));
            previousOffset = signal.Offset.Ticks;
        }
        var signalCount = UnsolicitedSignals.Count;
        foreach (var plan in Acquisitions) signalCount = checked(signalCount + plan.Signals.Count);
        if (signalCount > MaximumSignals) throw new ArgumentException("VirtualScenarioSignalCapacityExceeded");
        foreach (var signal in Acquisitions.SelectMany(plan => plan.Signals).Concat(UnsolicitedSignals))
            if (signal.Kind == VirtualCameraSignalKind.Frame && !byId.ContainsKey(signal.ImageId!))
                throw new ArgumentException("VirtualScenarioImageMissing");
        ContentHash = ComputeContentHash();
    }

    public string Id { get; }
    public string Version { get; }
    public uint Seed { get; }
    public string StableDeviceIdentity { get; }
    public string? ReportedModel { get; }
    public CameraCapabilities Capabilities { get; }
    public IReadOnlyList<VirtualCameraImage> Images { get; }
    public IReadOnlyList<VirtualCameraAcquisitionPlan> Acquisitions { get; }
    public IReadOnlyList<VirtualCameraConfigurationPlan> Configurations { get; }
    public IReadOnlyList<VirtualCameraOpenOutcome> OpeningOutcomes { get; }
    /// <summary>Relative to the first successful open only, not replayed on recovery.</summary>
    public IReadOnlyList<VirtualCameraSignal> UnsolicitedSignals { get; }
    public string ContentHash { get; }
    public long ImageBytes { get; }
    public int MaximumFrameBytes { get; }
    internal IReadOnlyDictionary<string, VirtualCameraImage> ImagesById { get; }

    private string ComputeContentHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        // v1 uses BinaryWriter's defined little-endian numbers and length-prefixed UTF-8.
        writer.Write(SimulatorVersion); writer.Write(Id); writer.Write(Version); writer.Write(Seed);
        writer.Write(StableDeviceIdentity); writer.Write(ReportedModel is not null);
        if (ReportedModel is not null) writer.Write(ReportedModel);
        writer.Write(Capabilities.ContentHash);
        writer.Write(Images.Count);
        foreach (var image in Images.OrderBy(image => image.Id, StringComparer.Ordinal))
        {
            writer.Write(image.Id); writer.Write(image.ContentHash); writer.Write(image.SourceDataHash is not null);
            if (image.SourceDataHash is not null) writer.Write(image.SourceDataHash);
        }
        writer.Write(Acquisitions.Count);
        foreach (var plan in Acquisitions) WriteSignals(writer, plan.Signals);
        writer.Write(Configurations.Count);
        foreach (var plan in Configurations) { writer.Write((int)plan.Outcome); writer.Write(plan.Delay.Ticks); }
        writer.Write(OpeningOutcomes.Count);
        foreach (var outcome in OpeningOutcomes) writer.Write((int)outcome);
        WriteSignals(writer, UnsolicitedSignals);
        // Preserve the historical byte stream for scenarios without preview;
        // explicitly configured preview is an independent, hashed extension.
        if (Preview is not null)
            writer.Write(Preview.ContentHash);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
    private static void WriteSignals(BinaryWriter writer, IReadOnlyList<VirtualCameraSignal> signals)
    {
        writer.Write(signals.Count);
        foreach (var signal in signals)
        {
            writer.Write(signal.Offset.Ticks); writer.Write((int)signal.Kind); writer.Write((int)signal.Association);
            writer.Write(signal.ImageId is not null);
            if (signal.ImageId is not null) writer.Write(signal.ImageId);
        }
    }
}

internal static class VirtualCameraContract
{
    internal static string Identifier(string? value, string parameter, int maximum = 64)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum || value.Any(c =>
            !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or ':')))
            throw new ArgumentException("VirtualCameraIdentifierInvalid", parameter);
        return value;
    }
    internal static string Text(string value, string parameter, int maximumBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes || value.Any(char.IsControl))
                throw new ArgumentException("VirtualCameraTextInvalid", parameter);
            return value;
        }
        catch (EncoderFallbackException) { throw new ArgumentException("VirtualCameraTextInvalid", parameter); }
    }
    internal static void Duration(TimeSpan value, string parameter)
    {
        if (value < TimeSpan.Zero || value > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(parameter);
    }
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, int maximum, string parameter) where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new List<T>();
        foreach (var value in values)
        {
            if (copied.Count == maximum) throw new ArgumentException("VirtualCameraCollectionCapacityExceeded", parameter);
            if (value is null) throw new ArgumentException("VirtualCameraCollectionItemInvalid", parameter);
            copied.Add(value);
        }
        return copied.AsReadOnly();
    }
}
