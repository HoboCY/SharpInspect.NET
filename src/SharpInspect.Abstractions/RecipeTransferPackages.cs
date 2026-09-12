using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Lifecycle claimed by the exporting station.  This is provenance only; no
/// value in this enum grants a local release, activation, or production right.
/// </summary>
public enum RecipeTransferSourceLifecycle : byte
{
    Draft = 1,
    Released = 2,
    Abandoned = 3,
    Retired = 4
}

/// <summary>Limits shared by the data-only transfer package contract.</summary>
public static class RecipeTransferPackageLimits
{
    public const int CurrentFormatVersion = 1;
    public const int CurrentCanonicalizationVersion = 1;
    public const int MaximumPackageBytes = 64 * 1024 * 1024;
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumRecipeJsonBytes = 2 * 1024 * 1024;
    public const int MaximumManifestDataMemberCount = 1;
    public const int EnvelopeMemberCount = 3;
    public const int MaximumMemberCount = EnvelopeMemberCount;
    public const int MaximumDependencyCount = 64;
    public const int MaximumStringBytes = 4096;
    public const int MaximumJsonDepth = 32;
    public const int SignatureBytes = 64;
    public const string SignatureScheme = "ECDSA-P256-SHA256-P1363";
    public const string MediaType = "application/vnd.sharpinspect.recipe-transfer-package";
    public const string RecipeMemberPath = "recipe.json";
    public const string ManifestMemberPath = "manifest.json";
    public const string SignatureMemberPath = "signature.bin";

    public static bool IsAllowedDependencyKind(string kind) => kind is "Algorithm" or
        "ConfigurationSchema" or "ResultSchema" or "OverlayContract" or "AlgorithmModel" or
        "AlgorithmExecutionPolicy" or "ImageAcquisitionPolicy" or "RecipeGovernancePolicy" or
        "CalibrationAcceptancePolicy" or "CalibrationCoefficientContract" or "PartIdentityFormat" or
        "EvidenceCapturePolicy";
}

/// <summary>
/// Exact source identity retained in a transfer package.  The lifecycle and
/// hashes are external claims and are never local authority.
/// </summary>
public sealed class RecipeTransferSource
{
    public RecipeTransferSource(Guid sourceId, string recipeKey, long revision,
        string revisionContentHash, RecipeTransferSourceLifecycle lifecycle,
        string? sourceStationId = null)
    {
        if (sourceId == Guid.Empty)
            throw new ArgumentException("RecipeTransferSourceIdentityInvalid", nameof(sourceId));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(lifecycle))
            throw new ArgumentOutOfRangeException(nameof(lifecycle));

        SourceId = sourceId;
        RecipeKey = AlgorithmConfigurationValidation.Identifier(recipeKey, nameof(recipeKey));
        Revision = revision;
        RevisionContentHash = AlgorithmConfigurationValidation.Hash(revisionContentHash,
            nameof(revisionContentHash)).ToUpperInvariant();
        Lifecycle = lifecycle;
        SourceStationId = sourceStationId is null ? null :
            AlgorithmConfigurationValidation.Identifier(sourceStationId, nameof(sourceStationId));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-transfer-source-v1", SourceId.ToString("D"), RecipeKey,
            Revision.ToString(CultureInfo.InvariantCulture), RevisionContentHash,
            Lifecycle.ToString(), SourceStationId
        });
    }

    public Guid SourceId { get; }
    public string RecipeKey { get; }
    public long Revision { get; }
    public string RevisionContentHash { get; }
    public RecipeTransferSourceLifecycle Lifecycle { get; }
    public string? SourceStationId { get; }
    public string ContentHash { get; }
}

/// <summary>One dependency identity declared by the source package.</summary>
public sealed class RecipeTransferDependency
{
    public RecipeTransferDependency(string kind, RecipeContractReference contract)
    {
        Kind = AlgorithmConfigurationValidation.Identifier(kind, nameof(kind));
        if (!RecipeTransferPackageLimits.IsAllowedDependencyKind(Kind))
            throw new ArgumentException("RecipeTransferDependencyKindUnsupported", nameof(kind));
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-transfer-dependency-v1", Kind,
            Contract.Id, Contract.Version, Contract.ContentHash
        });
    }

    public string Kind { get; }
    public RecipeContractReference Contract { get; }
    public string ContentHash { get; }
}

/// <summary>Signature metadata. The public key is deliberately absent.</summary>
public sealed class RecipeTransferSignatureInfo
{
    public RecipeTransferSignatureInfo(string keyId, string scope, string scheme =
        RecipeTransferPackageLimits.SignatureScheme)
    {
        KeyId = AlgorithmContractValidation.Identifier(keyId, nameof(keyId),
            RecipeTransferPackageLimits.MaximumStringBytes);
        Scope = AlgorithmContractValidation.Identifier(scope, nameof(scope),
            RecipeTransferPackageLimits.MaximumStringBytes);
        if (!string.Equals(scheme, RecipeTransferPackageLimits.SignatureScheme,
                StringComparison.Ordinal))
            throw new ArgumentException("RecipeTransferSignatureSchemeUnsupported", nameof(scheme));
        Scheme = scheme;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-transfer-signature-v1", KeyId, Scope, Scheme.ToString()
        });
    }

    public string KeyId { get; }
    public string Scope { get; }
    public string Scheme { get; }
    public string ContentHash { get; }
}

/// <summary>One bounded recipe-data member described by the canonical manifest.
/// The fixed manifest and signature envelope members are described by the
/// envelope contract itself so their hashes do not form a circular signature.</summary>
public sealed class RecipeTransferMemberManifest
{
    public RecipeTransferMemberManifest(string relativePath, int length, string contentHash)
    {
        RelativePath = ValidatePath(relativePath);
        if (length is < 1 or > RecipeTransferPackageLimits.MaximumRecipeJsonBytes)
            throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash))
            .ToUpperInvariant();
    }

    public string RelativePath { get; }
    public int Length { get; }
    public string ContentHash { get; }

    private static string ValidatePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value != RecipeTransferPackageLimits.RecipeMemberPath)
            throw new ArgumentException("RecipeTransferMemberPathInvalid", nameof(value));
        return value;
    }
}

/// <summary>
/// Canonical manifest for a version-one data-only Recipe Transfer Package.
/// A 64-zero ContentHash is a construction template; the Runtime codec replaces
/// it with the package hash before encoding and never accepts it on read.
/// </summary>
public sealed class RecipeTransferPackageManifest
{
    public const string ZeroContentHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public RecipeTransferPackageManifest(Guid packageId, RecipeTransferSource source,
        DateTimeOffset exportedAtUtc, RecipeTransferSignatureInfo signature,
        IEnumerable<RecipeTransferDependency> dependencies,
        IEnumerable<RecipeTransferMemberManifest> members, string contentHash)
    {
        if (packageId == Guid.Empty)
            throw new ArgumentException("RecipeTransferPackageIdentityInvalid", nameof(packageId));
        if (exportedAtUtc == default || exportedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("RecipeTransferExportTimeInvalid", nameof(exportedAtUtc));

        PackageId = packageId;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ExportedAtUtc = exportedAtUtc;
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));
        Dependencies = CopyDependencies(dependencies);
        Members = CopyMembers(members);
        ContentHash = AlgorithmConfigurationValidation.Hash(contentHash, nameof(contentHash))
            .ToUpperInvariant();
    }

    public int FormatVersion => RecipeTransferPackageLimits.CurrentFormatVersion;
    public int CanonicalizationVersion => RecipeTransferPackageLimits.CurrentCanonicalizationVersion;
    public Guid PackageId { get; }
    public RecipeTransferSource Source { get; }
    public DateTimeOffset ExportedAtUtc { get; }
    public RecipeTransferSignatureInfo Signature { get; }
    public ReadOnlyCollection<RecipeTransferDependency> Dependencies { get; }
    public ReadOnlyCollection<RecipeTransferMemberManifest> Members { get; }
    public string ContentHash { get; }

    internal RecipeTransferPackageManifest WithContentHash(string contentHash) =>
        new(PackageId, Source, ExportedAtUtc, Signature, Dependencies, Members, contentHash);

    private static ReadOnlyCollection<RecipeTransferDependency> CopyDependencies(
        IEnumerable<RecipeTransferDependency> dependencies)
    {
        var copied = AlgorithmContractValidation.Copy(dependencies, nameof(dependencies),
            RecipeTransferPackageLimits.MaximumDependencyCount);
        if (copied.Select(value => (value.Kind, value.Contract.Id, value.Contract.Version,
                value.Contract.ContentHash))
            .Distinct().Count() != copied.Count)
            throw new ArgumentException("RecipeTransferDependencyDuplicate", nameof(dependencies));
        return copied;
    }

    private static ReadOnlyCollection<RecipeTransferMemberManifest> CopyMembers(
        IEnumerable<RecipeTransferMemberManifest> members)
    {
        var copied = AlgorithmContractValidation.Copy(members, nameof(members),
            RecipeTransferPackageLimits.MaximumManifestDataMemberCount);
        if (copied.Count != RecipeTransferPackageLimits.MaximumManifestDataMemberCount ||
            copied.Select(value => value.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Count)
            throw new ArgumentException("RecipeTransferMemberSetInvalid", nameof(members));
        return copied;
    }
}

/// <summary>
/// The decoded package.  The envelope signature and recipe bytes are copied on
/// construction and every getter returns a fresh copy.
/// </summary>
public sealed class RecipeTransferPackage
{
    internal RecipeTransferPackage(RecipeTransferPackageManifest manifest,
        byte[] recipeJson, byte[] signature)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(recipeJson);
        ArgumentNullException.ThrowIfNull(signature);
        RecipeJson = (byte[])recipeJson.Clone();
        SignatureBytes = (byte[])signature.Clone();
    }

    public RecipeTransferPackageManifest Manifest { get; }
    public int RecipeJsonLength => RecipeJson.Length;
    public byte[] GetRecipeJsonBytes() => (byte[])RecipeJson.Clone();
    public byte[] GetSignatureBytes() => (byte[])SignatureBytes.Clone();

    internal byte[] RecipeJson { get; }
    internal byte[] SignatureBytes { get; }
}
