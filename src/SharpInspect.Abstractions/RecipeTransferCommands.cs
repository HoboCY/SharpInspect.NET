using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public enum RecipeTransferSourceKind : byte { Draft = 1, Released = 2 }

/// <summary>An exact local export source. It is read as a frozen snapshot and grants no import authority.</summary>
public sealed record RecipeTransferSourceSelection
{
    public RecipeTransferSourceSelection(RecipeDraftRevisionReference draft)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft)); Kind = RecipeTransferSourceKind.Draft;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-transfer-source-v1",
            Kind.ToString(), draft.DraftId.ToString("D"), draft.Revision.ToString(CultureInfo.InvariantCulture),
            draft.RevisionContentHash });
    }
    public RecipeTransferSourceSelection(RecipeReference recipe, Guid releaseId, string releaseRecordContentHash)
    {
        Recipe = RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash, nameof(releaseRecordContentHash));
        Kind = RecipeTransferSourceKind.Released;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-transfer-source-v1",
            Kind.ToString(), Recipe.Id, Recipe.Version, Recipe.ContentHash, ReleaseId.Value.ToString("D"),
            ReleaseRecordContentHash });
    }
    public RecipeTransferSourceKind Kind { get; }
    public RecipeDraftRevisionReference? Draft { get; }
    public RecipeReference? Recipe { get; }
    public Guid? ReleaseId { get; }
    public string? ReleaseRecordContentHash { get; }
    public string ContentHash { get; }
}

public abstract record RecipeTransferCommand : RuntimeCommand
{
    protected RecipeTransferCommand(Guid correlationId, CommandInvocation invocation, string reason)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("RecipeTransferCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        Reason = RecipeActivationValidation.Reason(reason, nameof(reason));
    }
    public string Reason { get; }
    public abstract string AuthorizationTarget { get; }
    protected string Target(string kind, params string?[] fields) =>
        AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-transfer-command-v1", kind, Reason }.Concat(fields));
}

public sealed record ReplaceRecipeTrustStoreCommand : RecipeTransferCommand
{
    public ReplaceRecipeTrustStoreCommand(Guid correlationId, CommandInvocation invocation, long expectedVersion,
        IEnumerable<RecipeTrustedSigner> signers, string reason) : base(correlationId, invocation, reason)
    {
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        ExpectedVersion = expectedVersion;
        var values = AlgorithmContractValidation.Copy(signers, nameof(signers), 64)
            .OrderBy(item => item.KeyId, StringComparer.Ordinal).ToArray();
        if (values.Select(item => item.KeyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new ArgumentException("RecipeTrustSignerDuplicate");
        Signers = new ReadOnlyCollection<RecipeTrustedSigner>(values);
        AuthorizationTarget = Target("ReplaceTrust", new[] { expectedVersion.ToString(CultureInfo.InvariantCulture) }
            .Concat(values.Select(item => item.ContentHash)).ToArray());
    }
    public long ExpectedVersion { get; }
    public ReadOnlyCollection<RecipeTrustedSigner> Signers { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record CreateRecipeSigningKeyCommand : RecipeTransferCommand
{
    public CreateRecipeSigningKeyCommand(Guid correlationId, CommandInvocation invocation, string keyId,
        string scope, DateTimeOffset notBeforeUtc, DateTimeOffset notAfterUtc, string reason)
        : base(correlationId, invocation, reason)
    {
        KeyId = AlgorithmConfigurationValidation.Identifier(keyId, nameof(keyId));
        Scope = AlgorithmConfigurationValidation.Identifier(scope, nameof(scope));
        if (notBeforeUtc.Offset != TimeSpan.Zero || notAfterUtc.Offset != TimeSpan.Zero || notAfterUtc <= notBeforeUtc)
            throw new ArgumentException("RecipeSigningKeyValidityInvalid");
        NotBeforeUtc = notBeforeUtc; NotAfterUtc = notAfterUtc;
        AuthorizationTarget = Target("CreateSigningKey", KeyId, Scope,
            notBeforeUtc.ToString("O", CultureInfo.InvariantCulture), notAfterUtc.ToString("O", CultureInfo.InvariantCulture));
    }
    public string KeyId { get; }
    public string Scope { get; }
    public DateTimeOffset NotBeforeUtc { get; }
    public DateTimeOffset NotAfterUtc { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record RetireRecipeSigningKeyCommand : RecipeTransferCommand
{
    public RetireRecipeSigningKeyCommand(Guid correlationId, CommandInvocation invocation, string keyId,
        string expectedPublicKeyFingerprint, string reason) : base(correlationId, invocation, reason)
    {
        KeyId = AlgorithmConfigurationValidation.Identifier(keyId, nameof(keyId));
        ExpectedPublicKeyFingerprint = RecipeActivationValidation.Hash(expectedPublicKeyFingerprint, nameof(expectedPublicKeyFingerprint));
        AuthorizationTarget = Target("RetireSigningKey", KeyId, ExpectedPublicKeyFingerprint);
    }
    public string KeyId { get; }
    public string ExpectedPublicKeyFingerprint { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record ExportRecipeTransferCommand : RecipeTransferCommand
{
    public ExportRecipeTransferCommand(Guid correlationId, CommandInvocation invocation,
        RecipeTransferSourceSelection source, string signingKeyId, string expectedPublicKeyFingerprint, string reason)
        : base(correlationId, invocation, reason)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SigningKeyId = AlgorithmConfigurationValidation.Identifier(signingKeyId, nameof(signingKeyId));
        ExpectedPublicKeyFingerprint = RecipeActivationValidation.Hash(expectedPublicKeyFingerprint, nameof(expectedPublicKeyFingerprint));
        AuthorizationTarget = Target("Export", Source.ContentHash, SigningKeyId, ExpectedPublicKeyFingerprint);
    }
    public RecipeTransferSourceSelection Source { get; }
    public string SigningKeyId { get; }
    public string ExpectedPublicKeyFingerprint { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Imports the exact supplied package hash. Runtime generates the new local identities.</summary>
public sealed record ImportRecipeTransferCommand : RecipeTransferCommand
{
    public ImportRecipeTransferCommand(Guid correlationId, CommandInvocation invocation, string packageBytesHash,
        string reason) : base(correlationId, invocation, reason)
    {
        PackageBytesHash = RecipeActivationValidation.Hash(packageBytesHash, nameof(packageBytesHash));
        AuthorizationTarget = Target("Import", PackageBytesHash);
    }
    public string PackageBytesHash { get; }
    public override string AuthorizationTarget { get; }
}
