using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Production;

/// <summary>An immutable, versioned deployment policy declaration. It is not qualification evidence.</summary>
public sealed class ProductionPolicyDocument
{
    public ProductionPolicyDocument(string id, string version, string content)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        Content = AlgorithmContractValidation.BoundedText(content, nameof(content), 32768);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
            { "sharpinspect-production-policy-document-v1", Id, Version, Content });
    }
    public string Id { get; }
    public string Version { get; }
    public string Content { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Host declarations that complete the deployment target. Qualification is independently
/// checked against this target and the actual loaded resources. Declaring a backup or
/// diagnostics policy does not assert that its execution or qualification has succeeded.
/// </summary>
public sealed class ProductionDeploymentManifest
{
    public ProductionDeploymentManifest(string id, string version,
        ProductionPolicyDocument logging, ProductionPolicyDocument diagnostics,
        ProductionPolicyDocument backup, ProductionPolicyDocument startup,
        ProductionPolicyDocument performance, ProductionPolicyDocument conformance,
        ProductionPolicyDocument uiWorkload, IEnumerable<string> vendorDependencyPaths)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Backup = backup ?? throw new ArgumentNullException(nameof(backup));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        Performance = performance ?? throw new ArgumentNullException(nameof(performance));
        Conformance = conformance ?? throw new ArgumentNullException(nameof(conformance));
        UiWorkload = uiWorkload ?? throw new ArgumentNullException(nameof(uiWorkload));
        ArgumentNullException.ThrowIfNull(vendorDependencyPaths);
        var paths = vendorDependencyPaths.Take(33).ToArray();
        if (paths.Length > 32 || paths.Any(path => string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("ProductionVendorDependencyPathsInvalid", nameof(vendorDependencyPaths));
        VendorDependencyPaths = Array.AsReadOnly(paths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-deployment-manifest-v1", Id, Version, Logging.ContentHash,
            Diagnostics.ContentHash, Backup.ContentHash, Startup.ContentHash, Performance.ContentHash,
            Conformance.ContentHash, UiWorkload.ContentHash
        }.Concat(VendorDependencyPaths));
    }
    public string Id { get; }
    public string Version { get; }
    public ProductionPolicyDocument Logging { get; }
    public ProductionPolicyDocument Diagnostics { get; }
    public ProductionPolicyDocument Backup { get; }
    public ProductionPolicyDocument Startup { get; }
    public ProductionPolicyDocument Performance { get; }
    public ProductionPolicyDocument Conformance { get; }
    public ProductionPolicyDocument UiWorkload { get; }
    public ReadOnlyCollection<string> VendorDependencyPaths { get; }
    public string ContentHash { get; }
}
