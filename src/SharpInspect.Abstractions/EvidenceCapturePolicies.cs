using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The V1 production image-retention obligation carried by a Released Recipe.</summary>
public enum EvidenceCaptureMode : byte
{
    None = 0,
    All = 1,
    FailOrUnknown = 2
}

/// <summary>
/// Immutable descriptive Evidence Capture Policy identity from the local deployment
/// catalog. It carries no storage path, execution setting or timestamp, and it is
/// never proof that an image was staged, encoded or retained.
/// </summary>
public sealed class EvidenceCapturePolicySnapshot
{
    public EvidenceCapturePolicySnapshot(string id, string version, EvidenceCaptureMode mode)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-evidence-capture-policy-v1", Id, Version, Mode.ToString()
        });
    }

    public string Id { get; }
    public string Version { get; }
    public EvidenceCaptureMode Mode { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference => new(Id, Version, ContentHash);

    /// <summary>
    /// Descriptive mode question only. A run without a frame never claims an image
    /// obligation, and the evidence of an admitted run remains the audited admission's
    /// frozen snapshot rather than a later read of this deployment policy.
    /// </summary>
    public bool RequiresImage(bool hasFrame, ExecutionStatus status, InspectionDecision decision)
    {
        _ = AlgorithmConfigurationValidation.Enum(status, nameof(status));
        _ = AlgorithmConfigurationValidation.Enum(decision, nameof(decision));
        if (!hasFrame || Mode == EvidenceCaptureMode.None) return false;
        return Mode == EvidenceCaptureMode.All || status != ExecutionStatus.Success ||
            decision != InspectionDecision.Pass;
    }
}

/// <summary>
/// Current deployment resolution input: the exact Evidence Capture Policy identities
/// this station currently accepts. It grants no execution authority, is not part of a
/// historical Recipe or store binding, and never resolves by name or latest version.
/// </summary>
public sealed class EvidenceCapturePolicyCatalog
{
    public const int MaximumPolicies = 64;

    private readonly IReadOnlyDictionary<(string Id, string Version), EvidenceCapturePolicySnapshot> _policies;

    public EvidenceCapturePolicyCatalog(IEnumerable<EvidenceCapturePolicySnapshot> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var copied = new Dictionary<(string Id, string Version), EvidenceCapturePolicySnapshot>();
        foreach (var policy in policies)
        {
            if (policy is null || copied.Count == MaximumPolicies ||
                !copied.TryAdd((policy.Id, policy.Version), policy))
                throw new ArgumentException("EvidenceCapturePolicyCatalogInvalid", nameof(policies));
        }
        _policies = copied;
        Policies = Array.AsReadOnly(copied.Values
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-evidence-capture-policy-catalog-v1",
            Policies.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Policies.Select(value => value.ContentHash)));
    }

    public IReadOnlyList<EvidenceCapturePolicySnapshot> Policies { get; }
    public string ContentHash { get; }

    /// <summary>Exact id, version and content hash only; a name or latest version never resolves.</summary>
    public EvidenceCapturePolicySnapshot? Resolve(RecipeContractReference reference) =>
        TryResolve(reference, out var policy) ? policy : null;

    public bool TryResolve(RecipeContractReference reference, out EvidenceCapturePolicySnapshot? policy)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (_policies.TryGetValue((reference.Id, reference.Version), out var resolved) &&
            resolved.ContentHash == reference.ContentHash)
        {
            policy = resolved;
            return true;
        }
        policy = null;
        return false;
    }
}
