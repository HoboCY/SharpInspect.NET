using System.Collections.ObjectModel;
using System.Windows;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>One explicitly registered, exact algorithm and schema compatibility claim.</summary>
public sealed class AlgorithmConfigurationEditorDescriptor
{
    public AlgorithmConfigurationEditorDescriptor(RecipeContractReference editor,
        AlgorithmIdentity algorithm, RecipeContractReference schema)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public RecipeContractReference Editor { get; }
    public AlgorithmIdentity Algorithm { get; }
    public RecipeContractReference Schema { get; }

    internal bool Matches(AlgorithmIdentity algorithm, AlgorithmConfigurationSchema schema) =>
        Algorithm.Id == algorithm.Id && Algorithm.Version == algorithm.Version &&
        Schema.Id == schema.Id && Schema.Version == schema.Version && Schema.ContentHash == schema.ContentHash;

    internal bool SameAs(AlgorithmConfigurationEditorDescriptor? other) => other is not null &&
        Editor == other.Editor && Algorithm == other.Algorithm && Schema == other.Schema;
}

/// <summary>An immutable copy of the configuration editing buffer; no recipe authority or services.</summary>
public sealed class AlgorithmConfigurationEditorSnapshot
{
    internal AlgorithmConfigurationEditorSnapshot(long editRevision,
        AlgorithmIdentity algorithm, AlgorithmConfigurationSchema schema,
        IEnumerable<AlgorithmConfigurationEntry> values, AlgorithmConfigurationSnapshot? configuration,
        IEnumerable<AlgorithmValidationIssue> issues, bool pendingInvalid)
    {
        EditRevision = editRevision;
        Algorithm = algorithm;
        Schema = schema;
        Values = new ReadOnlyCollection<AlgorithmConfigurationEntry>(values.ToArray());
        Configuration = configuration;
        Issues = new ReadOnlyCollection<AlgorithmValidationIssue>(issues.Take(256).ToArray());
        PendingInvalid = pendingInvalid;
    }

    public long EditRevision { get; }
    public AlgorithmIdentity Algorithm { get; }
    public AlgorithmConfigurationSchema Schema { get; }
    public IReadOnlyList<AlgorithmConfigurationEntry> Values { get; }
    public AlgorithmConfigurationSnapshot? Configuration { get; }
    public IReadOnlyList<AlgorithmValidationIssue> Issues { get; }
    public bool PendingInvalid { get; }
}

public sealed record AlgorithmConfigurationEditorEditResult(bool Applied, string ReasonCode);

/// <summary>
/// Revocable UI-thread capability for one configuration buffer. Complete replacements use the
/// same schema and authoring buffer as the generic editor. Validation uses the host's existing
/// complete-draft validation path. Saving and reauthentication remain host-owned commands.
/// </summary>
public interface IAlgorithmConfigurationDraftEditContext
{
    AlgorithmConfigurationEditorSnapshot? Read();
    AlgorithmConfigurationEditorEditResult TryReplace(long expectedEditRevision,
        IReadOnlyList<AlgorithmConfigurationEntry> completeValues);
    Task<RecipeDraftValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
    void ReportFailure();
}

/// <summary>
/// Explicitly supplied, trusted in-process WPF extension. Create and session callbacks run on
/// the UI thread and must return promptly. This capability contract is not a code sandbox.
/// </summary>
public interface IAlgorithmConfigurationEditorFactory
{
    AlgorithmConfigurationEditorDescriptor Descriptor { get; }
    IAlgorithmConfigurationEditorSession Create(IAlgorithmConfigurationDraftEditContext context);
}

public interface IAlgorithmConfigurationEditorSession : IDisposable
{
    FrameworkElement View { get; }
    void Refresh(AlgorithmConfigurationEditorSnapshot snapshot);
}
