using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Explicit, bounded registry for trusted in-process configuration editors.
/// The registry never scans assemblies or resolves services implicitly.
/// </summary>
public sealed class AlgorithmConfigurationEditorRegistry
{
    internal const int MaximumDescriptors = 64;

    private readonly ReadOnlyCollection<Registration> _registrations;
    private readonly ReadOnlyCollection<AlgorithmConfigurationEditorDescriptor> _descriptors;
    private readonly bool _constructionValid;

    public AlgorithmConfigurationEditorRegistry(
        IEnumerable<IAlgorithmConfigurationEditorFactory>? factories)
    {
        var registrations = new List<Registration>();
        var constructionValid = true;
        var factoryCount = 0;
        try
        {
            if (factories is not null)
            {
                foreach (var factory in factories)
                {
                    factoryCount++;
                    if (factoryCount > MaximumDescriptors)
                    {
                        constructionValid = false;
                        break;
                    }
                    if (factory is null) continue;

                    AlgorithmConfigurationEditorDescriptor? descriptor;
                    try { descriptor = factory.Descriptor; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { continue; }

                    if (descriptor is null || descriptor.Algorithm is null ||
                        descriptor.Schema is null || descriptor.Editor is null)
                        continue;
                    registrations.Add(new Registration(factory, descriptor));
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A partially enumerated registry is not a frozen registry. The
            // host must fall back instead of silently changing ambiguity based
            // on where an untrusted enumerable failed.
            constructionValid = false;
        }

        if (!constructionValid) registrations.Clear();
        _constructionValid = constructionValid;
        _registrations = new ReadOnlyCollection<Registration>(registrations.ToArray());
        _descriptors = new ReadOnlyCollection<AlgorithmConfigurationEditorDescriptor>(
            registrations.Select(item => item.Descriptor).ToArray());
    }

    /// <summary>Frozen descriptors in the explicit registration order.</summary>
    public IReadOnlyList<AlgorithmConfigurationEditorDescriptor> Descriptors => _descriptors;

    internal bool TryResolve(AlgorithmIdentity? algorithm,
        AlgorithmConfigurationSchema? schema,
        out IAlgorithmConfigurationEditorFactory? factory,
        out string reasonCode)
    {
        factory = null;
        reasonCode = "CustomEditorUnavailable";
        if (!_constructionValid)
        {
            reasonCode = "CustomEditorDescriptorInvalid";
            return false;
        }
        if (algorithm is null || schema is null || _registrations.Count == 0)
            return false;

        // Resolve against the frozen descriptors first. A later descriptor
        // getter failure or mutation must never turn an originally ambiguous
        // registration into a unique selection.
        var matches = _registrations
            .Where(registration => registration.Descriptor.Matches(algorithm, schema))
            .ToArray();
        if (matches.Length > 1)
        {
            reasonCode = "CustomEditorAmbiguous";
            return false;
        }
        if (matches.Length == 0)
        {
            reasonCode = "CustomEditorUnavailable";
            return false;
        }

        AlgorithmConfigurationEditorDescriptor? current;
        try { current = matches[0].Factory.Descriptor; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reasonCode = "CustomEditorDescriptorInvalid";
            return false;
        }

        if (current is null || !current.SameAs(matches[0].Descriptor))
        {
            reasonCode = "CustomEditorDescriptorInvalid";
            return false;
        }

        factory = matches[0].Factory;
        reasonCode = "CustomEditorResolved";
        return true;
    }

    internal bool TryResolve(AlgorithmIdentity? algorithm,
        AlgorithmConfigurationSchema? schema,
        out IAlgorithmConfigurationEditorFactory? factory) =>
        TryResolve(algorithm, schema, out factory, out _);

    private sealed record Registration(IAlgorithmConfigurationEditorFactory Factory,
        AlgorithmConfigurationEditorDescriptor Descriptor);
}
