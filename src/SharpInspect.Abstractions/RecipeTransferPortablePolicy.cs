using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// Host-approved classification of configuration fields as portable Recipe data for one exact schema.
/// Credentials, addresses, paths and device bindings must never be classified as portable fields.
/// This declaration is local deployment policy; a transfer package cannot supply or broaden it.
/// </summary>
public sealed class RecipeTransferPortableContract
{
    public RecipeTransferPortableContract(AlgorithmIdentity algorithm, RecipeContractReference configurationSchema,
        IEnumerable<string> portableFieldKeys)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        ConfigurationSchema = configurationSchema ?? throw new ArgumentNullException(nameof(configurationSchema));
        var keys = AlgorithmContractValidation.Copy(portableFieldKeys, nameof(portableFieldKeys), 256)
            .Select(key => AlgorithmConfigurationValidation.Identifier(key, nameof(portableFieldKeys)))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException("RecipeTransferPortableFieldDuplicate");
        PortableFieldKeys = new ReadOnlyCollection<string>(keys);
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-portable-contract-v1",
            algorithm.Id, algorithm.Version, configurationSchema.Id, configurationSchema.Version,
            configurationSchema.ContentHash }.Concat(keys));
    }
    public AlgorithmIdentity Algorithm { get; }
    public RecipeContractReference ConfigurationSchema { get; }
    public ReadOnlyCollection<string> PortableFieldKeys { get; }
    public string ContentHash { get; }
}

public sealed class RecipeTransferPortablePolicy
{
    public RecipeTransferPortablePolicy(string id, string version, IEnumerable<RecipeTransferPortableContract> contracts)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        var values = AlgorithmContractValidation.Copy(contracts, nameof(contracts), 64)
            .OrderBy(item => item.Algorithm.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Algorithm.Version, StringComparer.Ordinal).ToArray();
        if (values.Select(item => (item.Algorithm.Id, item.Algorithm.Version)).Distinct().Count() != values.Length)
            throw new ArgumentException("RecipeTransferPortableContractDuplicate");
        Contracts = new ReadOnlyCollection<RecipeTransferPortableContract>(values);
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-portable-policy-v1", Id, Version }
            .Concat(values.Select(item => item.ContentHash)));
    }
    public string Id { get; }
    public string Version { get; }
    public ReadOnlyCollection<RecipeTransferPortableContract> Contracts { get; }
    public string ContentHash { get; }
}
