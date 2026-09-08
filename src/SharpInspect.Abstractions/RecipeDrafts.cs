using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>A required versioned contract identity, never a deployment path or proof of availability.</summary>
public sealed record RecipeContractReference
{
    public RecipeContractReference(string id, string version, string contentHash)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash)).ToUpperInvariant();
    }
    public string Id { get; }
    public string Version { get; }
    public string ContentHash { get; }
}

public enum RecipeAssetKind { Calibration, AlgorithmModel }
public enum RecipePolicyKind { AlgorithmExecution, ImageAcquisition, RecipeGovernance, CalibrationAcceptance }
public enum RecipeDraftValueOrigin { Explicit, AuthoringDefault }
public sealed record RecipeDraftFieldOrigin
{
    public RecipeDraftFieldOrigin(string key, RecipeDraftValueOrigin origin)
    {
        Key = AlgorithmConfigurationValidation.Identifier(key, nameof(key));
        Origin = AlgorithmConfigurationValidation.Enum(origin, nameof(origin));
    }
    public string Key { get; }
    public RecipeDraftValueOrigin Origin { get; }
}

public sealed record RecipeAssetRequirement
{
    public RecipeAssetRequirement(RecipeAssetKind kind, string role, RecipeContractReference contract)
    {
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        Role = AlgorithmConfigurationValidation.Identifier(role, nameof(role));
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
    }
    public RecipeAssetKind Kind { get; }
    public string Role { get; }
    public RecipeContractReference Contract { get; }
}

public sealed record RecipePolicyRequirement
{
    public RecipePolicyRequirement(RecipePolicyKind kind, RecipeContractReference contract)
    {
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
    }
    public RecipePolicyKind Kind { get; }
    public RecipeContractReference Contract { get; }
}

/// <summary>Requested portable process values. Construction does not apply or read back any device.</summary>
public sealed record RequestedCameraConfiguration
{
    public RequestedCameraConfiguration(ProductionAcquisitionMode productionAcquisitionMode,
        double exposureTimeUs, double gainDb, RegionOfInterest regionOfInterest,
        VisionPixelFormat pixelFormat, int? validBits, int acquisitionTimeoutMs,
        double triggerDelayUs, WhiteBalanceRgb? whiteBalanceRgb)
    {
        // Share value-domain validation; do not expose the temporary value as effective device evidence.
        _ = new EffectiveCameraConfiguration(productionAcquisitionMode, exposureTimeUs, gainDb,
            regionOfInterest, pixelFormat, validBits, acquisitionTimeoutMs, triggerDelayUs, whiteBalanceRgb);
        ProductionAcquisitionMode = productionAcquisitionMode;
        ExposureTimeUs = AlgorithmScalarValue.NormalizeFloat(exposureTimeUs);
        GainDb = AlgorithmScalarValue.NormalizeFloat(gainDb);
        RegionOfInterest = regionOfInterest;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        AcquisitionTimeoutMs = acquisitionTimeoutMs;
        TriggerDelayUs = AlgorithmScalarValue.NormalizeFloat(triggerDelayUs);
        WhiteBalanceRgb = whiteBalanceRgb;
    }
    public ProductionAcquisitionMode ProductionAcquisitionMode { get; }
    public double ExposureTimeUs { get; }
    public double GainDb { get; }
    public RegionOfInterest RegionOfInterest { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public int AcquisitionTimeoutMs { get; }
    public double TriggerDelayUs { get; }
    public WhiteBalanceRgb? WhiteBalanceRgb { get; }
}

/// <summary>One exact atomic algorithm and its declared historical schema identities.</summary>
public sealed class RecipeAlgorithmBinding
{
    public RecipeAlgorithmBinding(AlgorithmIdentity algorithm, AlgorithmConfigurationSchema configurationSchema,
        RecipeContractReference resultSchema, RecipeContractReference overlayContract)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        ConfigurationSchema = configurationSchema ?? throw new ArgumentNullException(nameof(configurationSchema));
        ResultSchema = resultSchema ?? throw new ArgumentNullException(nameof(resultSchema));
        OverlayContract = overlayContract ?? throw new ArgumentNullException(nameof(overlayContract));
    }
    public AlgorithmIdentity Algorithm { get; }
    public AlgorithmConfigurationSchema ConfigurationSchema { get; }
    public RecipeContractReference ResultSchema { get; }
    public RecipeContractReference OverlayContract { get; }
    public static RecipeAlgorithmBinding FromDescriptor(AlgorithmDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new(descriptor.Identity, descriptor.ConfigurationSchema,
            new(descriptor.ResultSchema.Id, descriptor.ResultSchema.Version, descriptor.ResultSchema.ContentHash),
            new(descriptor.ResultSchema.OverlayContract.Id, descriptor.ResultSchema.OverlayContract.Version,
                descriptor.ResultSchema.OverlayContract.ContentHash));
    }
}

/// <summary>Immutable authoring candidate. Save independently validates its snapshot and registered factory.</summary>
public sealed class RecipeDraftContent
{
    public const string CanonicalizationVersion = "sharpinspect-recipe-draft-v1";
    public RecipeDraftContent(string recipeKey, string displayName, RecipeAlgorithmBinding algorithm,
        AlgorithmConfigurationSnapshot configuration, string cameraRole, RequestedCameraConfiguration camera,
        TimeSpan algorithmExecutionTimeout, IEnumerable<RecipeAssetRequirement>? assetRequirements,
        IEnumerable<RecipePolicyRequirement>? policyRequirements,
        IEnumerable<RecipeDraftFieldOrigin>? valueOrigins = null,
        CameraProviderExtensionRequirement? cameraProviderExtension = null)
    {
        RecipeKey = AlgorithmConfigurationValidation.Identifier(recipeKey, nameof(recipeKey));
        DisplayName = AlgorithmContractValidation.BoundedText(displayName, nameof(displayName), 128);
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        CameraRole = AlgorithmConfigurationValidation.Identifier(cameraRole, nameof(cameraRole));
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        CameraProviderExtension = cameraProviderExtension;
        if (!AlgorithmExecutionPolicy.IsRepresentableDuration(algorithmExecutionTimeout))
            throw new ArgumentOutOfRangeException(nameof(algorithmExecutionTimeout));
        AlgorithmExecutionTimeout = algorithmExecutionTimeout;
        AssetRequirements = AlgorithmContractValidation.Copy(assetRequirements, nameof(assetRequirements), 32);
        PolicyRequirements = AlgorithmContractValidation.Copy(policyRequirements, nameof(policyRequirements), 16);
        ValueOrigins = AlgorithmContractValidation.Copy(valueOrigins ?? configuration.Values.Select(
            entry => new RecipeDraftFieldOrigin(entry.Key, RecipeDraftValueOrigin.Explicit)), nameof(valueOrigins), 256);
        var valueKeys = configuration.Values.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (valueKeys.Count != configuration.Values.Count || ValueOrigins.Count != valueKeys.Count ||
            ValueOrigins.Select(origin => origin.Key).Distinct(StringComparer.Ordinal).Count() != ValueOrigins.Count ||
            ValueOrigins.Any(origin => !valueKeys.Contains(origin.Key)))
            throw new ArgumentException("RecipeDraftValueOriginsMismatch", nameof(valueOrigins));
        foreach (var origin in ValueOrigins.Where(origin => origin.Origin == RecipeDraftValueOrigin.AuthoringDefault))
        {
            var field = algorithm.ConfigurationSchema.Fields.SingleOrDefault(field => field.Key == origin.Key);
            var entry = configuration.Values.Single(entry => entry.Key == origin.Key);
            if (field?.AuthoringDefault is null || !field.AuthoringDefault.Equals(entry.Value))
                throw new ArgumentException("RecipeDraftAuthoringDefaultMismatch", nameof(valueOrigins));
        }
        if (AssetRequirements.Select(item => (item.Kind, item.Role)).Distinct().Count() != AssetRequirements.Count ||
            PolicyRequirements.Select(item => item.Kind).Distinct().Count() != PolicyRequirements.Count)
            throw new ArgumentException("RecipeRequirementDuplicate");
        ContentHash = ComputeHash();
    }
    public string RecipeKey { get; }
    public string DisplayName { get; }
    public RecipeAlgorithmBinding Algorithm { get; }
    public AlgorithmConfigurationSnapshot Configuration { get; }
    public string CameraRole { get; }
    public RequestedCameraConfiguration Camera { get; }
    /// <summary>Explicit provider-bound dependency, never a deployment device binding.</summary>
    public CameraProviderExtensionRequirement? CameraProviderExtension { get; }
    public bool IsCameraConfigurationPortable => CameraProviderExtension is null;
    public TimeSpan AlgorithmExecutionTimeout { get; }
    public ReadOnlyCollection<RecipeAssetRequirement> AssetRequirements { get; }
    public ReadOnlyCollection<RecipePolicyRequirement> PolicyRequirements { get; }
    public ReadOnlyCollection<RecipeDraftFieldOrigin> ValueOrigins { get; }
    public string ContentHash { get; }

    private string ComputeHash()
    {
        var parts = new List<string?> { CanonicalizationVersion, RecipeKey, DisplayName,
            Algorithm.Algorithm.Id, Algorithm.Algorithm.Version,
            Algorithm.ConfigurationSchema.Id, Algorithm.ConfigurationSchema.Version, Algorithm.ConfigurationSchema.ContentHash,
            Algorithm.ResultSchema.Id, Algorithm.ResultSchema.Version, Algorithm.ResultSchema.ContentHash,
            Algorithm.OverlayContract.Id, Algorithm.OverlayContract.Version, Algorithm.OverlayContract.ContentHash,
            Configuration.SchemaId, Configuration.SchemaVersion, Configuration.SchemaContentHash,
            Configuration.CanonicalizationVersion, Configuration.ContentHash, CameraRole,
            Camera.ProductionAcquisitionMode.ToString(), Number(Camera.ExposureTimeUs), Number(Camera.GainDb),
            Number(Camera.RegionOfInterest.OffsetX), Number(Camera.RegionOfInterest.OffsetY),
            Number(Camera.RegionOfInterest.Width), Number(Camera.RegionOfInterest.Height), Camera.PixelFormat.ToString(),
            Camera.ValidBits?.ToString(CultureInfo.InvariantCulture), Number(Camera.AcquisitionTimeoutMs),
            Number(Camera.TriggerDelayUs), Camera.WhiteBalanceRgb is null ? null : Number(Camera.WhiteBalanceRgb.Red),
            Camera.WhiteBalanceRgb is null ? null : Number(Camera.WhiteBalanceRgb.Green),
            Camera.WhiteBalanceRgb is null ? null : Number(Camera.WhiteBalanceRgb.Blue),
            AlgorithmExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture), Number(AssetRequirements.Count) };
        foreach (var asset in AssetRequirements.OrderBy(item => item.Kind).ThenBy(item => item.Role, StringComparer.Ordinal))
            parts.AddRange(new[] { asset.Kind.ToString(), asset.Role, asset.Contract.Id, asset.Contract.Version, asset.Contract.ContentHash });
        parts.Add(Number(PolicyRequirements.Count));
        foreach (var policy in PolicyRequirements.OrderBy(item => item.Kind))
            parts.AddRange(new[] { policy.Kind.ToString(), policy.Contract.Id, policy.Contract.Version, policy.Contract.ContentHash });
        parts.Add(Number(ValueOrigins.Count));
        foreach (var origin in ValueOrigins.OrderBy(item => item.Key, StringComparer.Ordinal))
            parts.AddRange(new[] { origin.Key, origin.Origin.ToString() });
        // Preserve historical common-only draft hashes exactly. Extension dependencies
        // add a separately versioned suffix and cannot disappear without changing identity.
        if (CameraProviderExtension is { } extension)
            parts.AddRange(new[] { "sharpinspect-camera-provider-extension-v1", extension.Provider.Id,
                extension.Provider.Version, extension.Provider.AdapterPackageId, extension.Provider.AdapterVersion,
                extension.ContractId, extension.ContractVersion, extension.ConfigurationContentHash });
        return AlgorithmContractValidation.HashParts(parts);
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>CAS compares both head revision number and its full revision hash, never only the candidate content hash.</summary>
public sealed record RecipeDraftSaveRequest(Guid OperationId, Guid DraftId, long ExpectedRevision,
    string? ExpectedRevisionContentHash, RecipeDraftContent Content, string ChangeReason,
    CommandInvocation Invocation, Guid? StepUpGrantId = null);

public sealed class RecipeDraftRevision
{
    internal RecipeDraftRevision(long position, Guid draftId, long revision, Guid operationId,
        string? previousRevisionContentHash, string revisionContentHash, RecipeDraftContent content,
        Guid authorPrincipalId, Guid authorSessionId, long authorAuthorizationRevision,
        string changeReason, DateTimeOffset recordedAtUtc)
    {
        Position = position; DraftId = draftId; Revision = revision; OperationId = operationId;
        PreviousRevisionContentHash = previousRevisionContentHash; RevisionContentHash = revisionContentHash;
        Content = content; AuthorPrincipalId = authorPrincipalId; AuthorSessionId = authorSessionId;
        AuthorAuthorizationRevision = authorAuthorizationRevision; ChangeReason = changeReason; RecordedAtUtc = recordedAtUtc;
    }
    public long Position { get; }
    public Guid DraftId { get; }
    public long Revision { get; }
    public Guid OperationId { get; }
    public string? PreviousRevisionContentHash { get; }
    /// <summary>Hash of the revision identity, predecessor, content, verified author/session, authorization revision and audit time.</summary>
    public string RevisionContentHash { get; }
    public RecipeDraftContent Content { get; }
    public Guid AuthorPrincipalId { get; }
    public Guid AuthorSessionId { get; }
    public long AuthorAuthorizationRevision { get; }
    public string ChangeReason { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public bool Published => false;
    public bool Active => false;
    public bool CanRelease => false;
    public string DependencyValidation => "NotRun";
}

public sealed record RecipeDraftValidationResult(bool Valid, string ReasonCode,
    IReadOnlyList<AlgorithmValidationIssue> Issues);
public sealed record RecipeDraftSaveResult(bool Saved, string ReasonCode, RecipeDraftRevision? Revision,
    IReadOnlyList<AlgorithmValidationIssue> Issues);
public sealed record RecipeDraftReadResult(bool Available, string ReasonCode, RecipeDraftRevision? Revision);
public sealed record RecipeDraftAccess(bool CanSave, string ReasonCode, bool RequiresStepUp);
public sealed record RecipeDraftFilter(Guid? DraftId = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 20);
public sealed record RecipeDraftPage(bool Available, string ReasonCode, IReadOnlyList<RecipeDraftRevision> Revisions,
    long ThroughPosition, long? NextAfterPosition);

/// <summary>Independent read-only history that does not initialize a writer or require an algorithm factory.</summary>
public interface IRecipeDraftHistoryQuery
{
    ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null, CancellationToken cancellationToken = default);
    ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>Only authoring, validation and bounded history; no production or device capability.</summary>
public interface IRecipeDraftEditor : IRecipeDraftHistoryQuery
{
    IReadOnlyList<AlgorithmDescriptor> Algorithms { get; }
    IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(AlgorithmIdentity algorithm);
    ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content, CancellationToken cancellationToken = default);
    ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request, CancellationToken cancellationToken = default);
}
