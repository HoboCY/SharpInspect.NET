using System.Collections.ObjectModel;
using System.Text;

namespace SharpInspect.Abstractions;

public enum ConformanceOutcome { Pass, Fail, NotApplicable, NotRun, Blocked, InvalidHarness }
public enum QualificationLayer { Framework, Provider, Station }
public enum VerificationMethod { Executable, GovernedInspection }
public enum ConformanceClaim { DevelopmentOnly, FrameworkRelease, ProviderQualification, StationAcceptance }

public sealed record ConformanceRequirement
{
    public ConformanceRequirement(string requirementId, string sourceReference, string invariant, bool mandatory)
    {
        RequirementId = ConformanceValidation.Identifier(requirementId, nameof(requirementId), 128);
        SourceReference = ConformanceValidation.Text(sourceReference, nameof(sourceReference), 1024);
        Invariant = ConformanceValidation.Text(invariant, nameof(invariant), 8192);
        Mandatory = mandatory;
    }

    public string RequirementId { get; }
    public string SourceReference { get; }
    public string Invariant { get; }
    public bool Mandatory { get; }
}

public sealed record VerificationCase
{
    public VerificationCase(string testId, QualificationLayer layer, VerificationMethod method,
        IReadOnlyList<string> requirementIds, bool applicable, string applicabilityRule,
        string exclusionEvidence, string requiredEnvironment, string preconditions, string stimulus,
        string expectedObservable, string evidenceContract, string acceptanceRule)
    {
        TestId = ConformanceValidation.Identifier(testId, nameof(testId), 128);
        Layer = ConformanceValidation.Enum(layer, nameof(layer));
        Method = ConformanceValidation.Enum(method, nameof(method));
        RequirementIds = ConformanceValidation.Identifiers(requirementIds, nameof(requirementIds), 256, 1);
        Applicable = applicable;
        ApplicabilityRule = ConformanceValidation.Text(applicabilityRule, nameof(applicabilityRule), 4096);
        ExclusionEvidence = ConformanceValidation.OptionalText(exclusionEvidence, nameof(exclusionEvidence), 8192);
        RequiredEnvironment = ConformanceValidation.Text(requiredEnvironment, nameof(requiredEnvironment), 8192);
        Preconditions = ConformanceValidation.Text(preconditions, nameof(preconditions), 8192);
        Stimulus = ConformanceValidation.Text(stimulus, nameof(stimulus), 8192);
        ExpectedObservable = ConformanceValidation.Text(expectedObservable, nameof(expectedObservable), 8192);
        EvidenceContract = ConformanceValidation.Text(evidenceContract, nameof(evidenceContract), 8192);
        AcceptanceRule = ConformanceValidation.Text(acceptanceRule, nameof(acceptanceRule), 8192);
        if (!Applicable && ExclusionEvidence.Length == 0)
            throw new ArgumentException("ConformanceExclusionEvidenceRequired", nameof(exclusionEvidence));
    }

    public string TestId { get; }
    public QualificationLayer Layer { get; }
    public VerificationMethod Method { get; }
    public IReadOnlyList<string> RequirementIds { get; }
    public bool Applicable { get; }
    public string ApplicabilityRule { get; }
    public string ExclusionEvidence { get; }
    public string RequiredEnvironment { get; }
    public string Preconditions { get; }
    public string Stimulus { get; }
    public string ExpectedObservable { get; }
    public string EvidenceContract { get; }
    public string AcceptanceRule { get; }
}

public sealed record ConformanceProfile
{
    public ConformanceProfile(string profileId, int version, ConformanceClaim claim, string claimScope,
        IReadOnlyList<string> supportedPlatforms, IReadOnlyList<string> features,
        IReadOnlyList<QualificationLayer> requiredLayers, string evidenceRetentionRule,
        string releaseAuthority, IReadOnlyList<ConformanceRequirement> requirements,
        IReadOnlyList<VerificationCase> cases)
    {
        ProfileId = ConformanceValidation.Identifier(profileId, nameof(profileId), 128);
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        Claim = ConformanceValidation.Enum(claim, nameof(claim));
        ClaimScope = ConformanceValidation.Text(claimScope, nameof(claimScope), 8192);
        SupportedPlatforms = ConformanceValidation.Identifiers(supportedPlatforms, nameof(supportedPlatforms), 128, 0);
        Features = ConformanceValidation.Identifiers(features, nameof(features), 256, 0);
        RequiredLayers = ConformanceValidation.Enums(requiredLayers, nameof(requiredLayers), 3, 1);
        EvidenceRetentionRule = ConformanceValidation.Text(evidenceRetentionRule, nameof(evidenceRetentionRule), 8192);
        ReleaseAuthority = ConformanceValidation.Text(releaseAuthority, nameof(releaseAuthority), 1024);
        Requirements = ConformanceValidation.Copy(requirements, nameof(requirements), 256, 1);
        Cases = ConformanceValidation.Copy(cases, nameof(cases), 256, 1);
        ConformanceValidation.ValidateProfile(this);
    }

    public string ProfileId { get; }
    public int Version { get; }
    public ConformanceClaim Claim { get; }
    public string ClaimScope { get; }
    public IReadOnlyList<string> SupportedPlatforms { get; }
    public IReadOnlyList<string> Features { get; }
    public IReadOnlyList<QualificationLayer> RequiredLayers { get; }
    public string EvidenceRetentionRule { get; }
    public string ReleaseAuthority { get; }
    public IReadOnlyList<ConformanceRequirement> Requirements { get; }
    public IReadOnlyList<VerificationCase> Cases { get; }

    public IReadOnlyList<VerificationCase> GetCasesForRequirement(string requirementId)
    {
        var id = ConformanceValidation.Identifier(requirementId, nameof(requirementId), 128);
        return new ReadOnlyCollection<VerificationCase>(
            Cases.Where(item => item.RequirementIds.Contains(id, StringComparer.Ordinal)).ToArray());
    }

    public IReadOnlyList<ConformanceRequirement> GetRequirementsForCase(string testId)
    {
        var id = ConformanceValidation.Identifier(testId, nameof(testId), 128);
        var verification = Cases.SingleOrDefault(item => string.Equals(item.TestId, id, StringComparison.Ordinal));
        if (verification is null) throw new KeyNotFoundException("ConformanceTestIdNotFound");
        var byId = Requirements.ToDictionary(item => item.RequirementId, StringComparer.Ordinal);
        return new ReadOnlyCollection<ConformanceRequirement>(
            verification.RequirementIds.Select(requirementId => byId[requirementId]).ToArray());
    }
}

public sealed record FingerprintComponent
{
    public FingerprintComponent(string name, string sha256)
    {
        Name = ConformanceValidation.Identifier(name, nameof(name), 128);
        Sha256 = ConformanceValidation.Hash(sha256, nameof(sha256));
    }

    public string Name { get; }
    public string Sha256 { get; }
}

public sealed record ReleaseCandidateDefinition
{
    public ReleaseCandidateDefinition(string sourceRevision, IReadOnlyList<FingerprintComponent> publicApi,
        IReadOnlyList<FingerprintComponent> schemas, IReadOnlyList<FingerprintComponent> packages,
        IReadOnlyList<FingerprintComponent> migrations, IReadOnlyList<FingerprintComponent> embeddedAssets,
        IReadOnlyList<FingerprintComponent> dependencyLocks, IReadOnlyList<FingerprintComponent> buildConfiguration)
    {
        SourceRevision = ConformanceValidation.Identifier(sourceRevision, nameof(sourceRevision), 256);
        PublicApi = ConformanceValidation.Components(publicApi, nameof(publicApi), 256, 0, allowNull: false)!;
        Schemas = ConformanceValidation.Components(schemas, nameof(schemas), 256, 0, allowNull: false)!;
        Packages = ConformanceValidation.Components(packages, nameof(packages), 256, 1, allowNull: false)!;
        Migrations = ConformanceValidation.Components(migrations, nameof(migrations), 256, 0, allowNull: false)!;
        EmbeddedAssets = ConformanceValidation.Components(embeddedAssets, nameof(embeddedAssets), 256, 0, allowNull: false)!;
        DependencyLocks = ConformanceValidation.Components(dependencyLocks, nameof(dependencyLocks), 256, 1, allowNull: false)!;
        BuildConfiguration = ConformanceValidation.Components(buildConfiguration, nameof(buildConfiguration), 256, 1, allowNull: false)!;
    }

    public string SourceRevision { get; }
    public IReadOnlyList<FingerprintComponent> PublicApi { get; }
    public IReadOnlyList<FingerprintComponent> Schemas { get; }
    public IReadOnlyList<FingerprintComponent> Packages { get; }
    public IReadOnlyList<FingerprintComponent> Migrations { get; }
    public IReadOnlyList<FingerprintComponent> EmbeddedAssets { get; }
    public IReadOnlyList<FingerprintComponent> DependencyLocks { get; }
    public IReadOnlyList<FingerprintComponent> BuildConfiguration { get; }
}

public sealed record QualificationContextDefinition
{
    public QualificationContextDefinition(string profileHash, IReadOnlyList<FingerprintComponent> harnesses,
        IReadOnlyList<FingerprintComponent> scenarios, IReadOnlyList<FingerprintComponent> datasets,
        IReadOnlyList<FingerprintComponent> seeds, IReadOnlyList<FingerprintComponent> thresholds,
        IReadOnlyList<FingerprintComponent> calculationRules,
        IReadOnlyList<FingerprintComponent> environmentConfiguration)
    {
        ProfileHash = ConformanceValidation.Hash(profileHash, nameof(profileHash));
        Harnesses = ConformanceValidation.Components(harnesses, nameof(harnesses), 256, 1, allowNull: false)!;
        Scenarios = ConformanceValidation.Components(scenarios, nameof(scenarios), 256, 1, allowNull: false)!;
        Datasets = ConformanceValidation.Components(datasets, nameof(datasets), 8, 0, allowNull: false)!;
        Seeds = ConformanceValidation.Components(seeds, nameof(seeds), 256, 0, allowNull: false)!;
        Thresholds = ConformanceValidation.Components(thresholds, nameof(thresholds), 256, 0, allowNull: false)!;
        CalculationRules = ConformanceValidation.Components(calculationRules, nameof(calculationRules), 256, 0, allowNull: false)!;
        EnvironmentConfiguration = ConformanceValidation.Components(environmentConfiguration, nameof(environmentConfiguration), 256, 1, allowNull: false)!;
    }

    public string ProfileHash { get; }
    public IReadOnlyList<FingerprintComponent> Harnesses { get; }
    public IReadOnlyList<FingerprintComponent> Scenarios { get; }
    public IReadOnlyList<FingerprintComponent> Datasets { get; }
    public IReadOnlyList<FingerprintComponent> Seeds { get; }
    public IReadOnlyList<FingerprintComponent> Thresholds { get; }
    public IReadOnlyList<FingerprintComponent> CalculationRules { get; }
    public IReadOnlyList<FingerprintComponent> EnvironmentConfiguration { get; }
}

public sealed record FrozenConformanceDocument(string Kind, string CanonicalJson, string Sha256);

internal static class ConformanceValidation
{
    internal static string Identifier(string value, string parameterName, int maximum)
    {
        var result = Text(value, parameterName, maximum);
        if (result.Length == 0 || result.Trim() != result ||
            result.Any(character => !((character is >= 'A' and <= 'Z') ||
                (character is >= 'a' and <= 'z') || (character is >= '0' and <= '9') ||
                character is '.' or '_' or '-')))
            throw new ArgumentException("ConformanceIdentifierInvalid", parameterName);
        return result;
    }

    internal static string Text(string value, string parameterName, int maximum)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length == 0)
            throw new ArgumentException("ConformanceTextInvalid", parameterName);
        var bytes = StrictUtf8(value, parameterName);
        if (bytes.Length > maximum)
            throw new ArgumentException("ConformanceTextInvalid", parameterName);
        return value;
    }

    internal static string OptionalText(string? value, string parameterName, int maximum)
    {
        if (value is null) return string.Empty;
        var bytes = StrictUtf8(value, parameterName);
        if (bytes.Length > maximum) throw new ArgumentException("ConformanceTextInvalid", parameterName);
        return value;
    }

    internal static string Hash(string value, string parameterName)
    {
        var result = Text(value, parameterName, 64);
        if (result.Length != 64 || result.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("ConformanceSha256Invalid", parameterName);
        return result;
    }

    internal static T Enum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!System.Enum.IsDefined(typeof(T), value)) throw new ArgumentException("ConformanceEnumInvalid", parameterName);
        return value;
    }

    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? source, string parameterName, int maximum, int minimum)
    {
        if (source is null) throw new ArgumentNullException(parameterName);
        if (source.Count < minimum || source.Count > maximum)
            throw new ArgumentException("ConformanceCollectionBounds", parameterName);
        return new ReadOnlyCollection<T>(source.ToArray());
    }

    internal static IReadOnlyList<string> Identifiers(IReadOnlyList<string>? source, string parameterName,
        int maximum, int minimum)
    {
        var copy = Copy(source, parameterName, maximum, minimum).ToArray();
        for (var index = 0; index < copy.Length; index++) copy[index] = Identifier(copy[index], parameterName, 256);
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("ConformanceDuplicateId", parameterName);
        return new ReadOnlyCollection<string>(copy);
    }

    internal static IReadOnlyList<T> Enums<T>(IReadOnlyList<T>? source, string parameterName,
        int maximum, int minimum) where T : struct, Enum
    {
        var copy = Copy(source, parameterName, maximum, minimum).ToArray();
        foreach (var value in copy) Enum(value, parameterName);
        if (copy.Distinct().Count() != copy.Length)
            throw new ArgumentException("ConformanceDuplicateEnum", parameterName);
        return new ReadOnlyCollection<T>(copy);
    }

    internal static IReadOnlyList<FingerprintComponent>? Components(IReadOnlyList<FingerprintComponent>? source,
        string parameterName, int maximum, int minimum, bool allowNull)
    {
        if (source is null)
        {
            if (allowNull) return null;
            throw new ArgumentNullException(parameterName);
        }
        var copy = Copy(source, parameterName, maximum, minimum).ToArray();
        if (copy.Any(component => component is null)) throw new ArgumentException("ConformanceComponentInvalid", parameterName);
        if (copy.Select(component => component.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("ConformanceDuplicateComponent", parameterName);
        return new ReadOnlyCollection<FingerprintComponent>(copy);
    }

    internal static void ValidateProfile(ConformanceProfile profile)
    {
        var requirements = profile.Requirements.ToDictionary(item => item.RequirementId, StringComparer.Ordinal);
        var cases = new HashSet<string>(StringComparer.Ordinal);
        var coveredLayers = new HashSet<QualificationLayer>();
        var applicableRequirements = new HashSet<string>(StringComparer.Ordinal);
        foreach (var verification in profile.Cases)
        {
            if (!cases.Add(verification.TestId)) throw new ArgumentException("ConformanceDuplicateTestId", nameof(profile));
            if (verification.Applicable && verification.ExclusionEvidence.Length != 0)
                throw new ArgumentException("ConformanceExclusionEvidenceContradictory", nameof(profile));
            foreach (var requirementId in verification.RequirementIds)
            {
                if (!requirements.ContainsKey(requirementId))
                    throw new ArgumentException("ConformanceRequirementMappingMissing", nameof(profile));
                if (!verification.Applicable && requirements[requirementId].Mandatory)
                    throw new ArgumentException("ConformanceMandatoryRequirementNotApplicable", nameof(profile));
                if (verification.Applicable) applicableRequirements.Add(requirementId);
            }
            if (verification.Applicable) coveredLayers.Add(verification.Layer);
        }

        foreach (var requirement in profile.Requirements)
            if (requirement.Mandatory && !applicableRequirements.Contains(requirement.RequirementId))
                throw new ArgumentException("ConformanceMandatoryRequirementUncovered", nameof(profile));
        foreach (var layer in profile.RequiredLayers)
            if (!coveredLayers.Contains(layer)) throw new ArgumentException("ConformanceRequiredLayerUncovered", nameof(profile));
    }

    private static byte[] StrictUtf8(string value, string parameterName)
    {
        try { return new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException exception)
        { throw new ArgumentException("ConformanceUtf8Invalid", parameterName, exception); }
    }
}
