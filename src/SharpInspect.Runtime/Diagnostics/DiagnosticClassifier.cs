using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>Only this boundary may turn unclassified requests into serializable properties.</summary>
internal sealed class DiagnosticClassifier
{
    private readonly LoggingDiagnosticsPolicy _policy;
    private readonly Dictionary<(string, int), DiagnosticEventContract> _catalog;
    internal DiagnosticClassifier(LoggingDiagnosticsPolicy policy)
    { _policy = policy; _catalog = policy.Contracts.ToDictionary(value => (value.Code, value.SchemaVersion)); }

    // Defense against accidentally approving well-known forbidden semantic fields. This is
    // not a secret detector: arbitrary text is forbidden even under an innocuous approved key.
    internal static bool ForbiddenName(string name)
    {
        var normalized = name.Replace("_", "").Replace("-", "").Replace(".", "").ToLowerInvariant();
        if (new[] { "password", "token", "secret", "credential", "connectionstring", "privatekey", "verifier", "recoverycode" }
            .Any(term => normalized.Contains(term, StringComparison.Ordinal))) return true;
        return normalized is "password" or "token" or "accesstoken" or "refreshtoken" or "apikey" or
            "secret" or "recoverycode" or "recoverycodes" or "privatekey" or "verifier" or
            "partidentity" or "rawpartidentity" or "partcode" or "barcode" or "imagebytes" or "image" or
            "recipepayload" or "recipe" or "databasepage" or "databasepages" or "memorydump";
    }

    internal static bool ProtectedName(string name) => new[] { "exception", "vendor", "network", "address", "path", "serial", "hresult" }
        .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    internal bool TryClassify(DiagnosticEmissionRequest request, bool algorithm,
        out DiagnosticEventContract? contract, out DiagnosticProperty[] safe, out DiagnosticProperty[] protectedValues)
    {
        contract = null; safe = protectedValues = Array.Empty<DiagnosticProperty>();
        if (!_catalog.TryGetValue((request.Code, request.SchemaVersion), out var schema) ||
            algorithm && !schema.AlgorithmAllowed || schema.Level < _policy.Baseline ||
            request.Properties.Count > _policy.Producers.MaximumProperties) return false;
        if (!TryClassifyProjection(request, schema, out var values)) return false;
        contract = schema;
        safe = values.Where(value => schema.Fields.Single(field => field.Name == value.Name).Classification == DiagnosticDataClass.Safe).ToArray();
        protectedValues = values.Where(value => schema.Fields.Single(field => field.Name == value.Name).Classification == DiagnosticDataClass.Protected).ToArray();
        return true;
    }

    internal bool TryClassifyProjection(DiagnosticEmissionRequest request, DiagnosticEventContract schema,
        out DiagnosticProperty[] values)
    {
        values = Array.Empty<DiagnosticProperty>();
        if (request.Properties.Count > _policy.Producers.MaximumProperties) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var safeList = new List<DiagnosticProperty>(32);
        var protectedList = new List<DiagnosticProperty>(32);
        foreach (var property in request.Properties)
        {
            if (!seen.Add(property.Name) || ForbiddenName(property.Name)) return false;
            var field = schema.Fields.FirstOrDefault(value => value.Name == property.Name);
            if (field is null || field.Classification == DiagnosticDataClass.Prohibited ||
                field.Classification == DiagnosticDataClass.Safe && ProtectedName(field.Name) ||
                !TryScalar(property.Value, field, _policy.Producers.MaximumSymbolBytes, out var scalar)) return false;
            (field.Classification == DiagnosticDataClass.Safe ? safeList : protectedList).Add(new(field.Name, scalar!));
        }
        if (schema.Fields.Any(field => field.Required && !seen.Contains(field.Name))) return false;
        values = safeList.Concat(protectedList).ToArray(); return true;
    }

    private static bool TryScalar(object? value, DiagnosticFieldContract field, int maxSymbol,
        out DiagnosticScalar? scalar)
    {
        scalar = null;
        switch (value)
        {
            case bool boolean when field.Kind == DiagnosticScalarKind.Boolean:
                scalar = DiagnosticScalar.FromBoolean(boolean); break;
            case int integer when field.Kind == DiagnosticScalarKind.Int64 && integer >= field.MinimumInteger && integer <= field.MaximumInteger:
                scalar = DiagnosticScalar.FromInt64(integer); break;
            case long integer when field.Kind == DiagnosticScalarKind.Int64 && integer >= field.MinimumInteger && integer <= field.MaximumInteger:
                scalar = DiagnosticScalar.FromInt64(integer); break;
            case double number when field.Kind == DiagnosticScalarKind.Number && double.IsFinite(number) &&
                number >= field.Minimum && number <= field.Maximum:
                scalar = DiagnosticScalar.FromNumber(number); break;
            case string symbol when field.Kind == DiagnosticScalarKind.Symbol && symbol.Length <= maxSymbol &&
                field.Symbols.Contains(symbol, StringComparer.Ordinal):
                // Use the policy-owned string; never retain a producer's object graph.
                scalar = DiagnosticScalar.FromSymbol(field.Symbols.First(item => item == symbol)); break;
        }
        return scalar is not null;
    }
}
