using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Editable, intentionally blank policy input.  It does not supply production
/// defaults; callers must load an approved policy or enter every required value.
/// </summary>
public sealed partial class TraceStoragePolicyEditor : ObservableObject
{
    private string _policyId = string.Empty;
    private string _version = string.Empty;
    private string _approvalReference = string.Empty;
    private string _approvalVersion = string.Empty;
    private string _rationale = string.Empty;
    private string _minimumReserveBytes = string.Empty;
    private string _minimumReservePercent = string.Empty;
    private string _evidenceStageTimeout = string.Empty;
    private string _traceCommitTimeout = string.Empty;
    private string _maximumWalBytes = string.Empty;

    public TraceStoragePolicyEditor()
    {
        RetentionRules = new ObservableCollection<TraceRetentionRuleEditor>(
            Enum.GetValues<TraceRetentionClass>().Select(value => new TraceRetentionRuleEditor(value)));
        RequiredRoutes = new ObservableCollection<TraceStorageRouteEditor>();
        ImageBacklog = new TraceBacklogEditor();
        Scrubber = new TraceStorageMaintenanceBudgetEditor();
        Checkpoint = new TraceStorageMaintenanceBudgetEditor();
    }

    public string PolicyId { get => _policyId; set => SetProperty(ref _policyId, value ?? string.Empty); }
    public string Version { get => _version; set => SetProperty(ref _version, value ?? string.Empty); }
    public string ApprovalReference { get => _approvalReference; set => SetProperty(ref _approvalReference, value ?? string.Empty); }
    public string ApprovalVersion { get => _approvalVersion; set => SetProperty(ref _approvalVersion, value ?? string.Empty); }
    public string Rationale { get => _rationale; set => SetProperty(ref _rationale, value ?? string.Empty); }
    public string MinimumReserveBytes { get => _minimumReserveBytes; set => SetProperty(ref _minimumReserveBytes, value ?? string.Empty); }
    public string MinimumReservePercent { get => _minimumReservePercent; set => SetProperty(ref _minimumReservePercent, value ?? string.Empty); }
    public string EvidenceStageTimeout { get => _evidenceStageTimeout; set => SetProperty(ref _evidenceStageTimeout, value ?? string.Empty); }
    public string TraceCommitTimeout { get => _traceCommitTimeout; set => SetProperty(ref _traceCommitTimeout, value ?? string.Empty); }
    public string MaximumWalBytes { get => _maximumWalBytes; set => SetProperty(ref _maximumWalBytes, value ?? string.Empty); }

    public ObservableCollection<TraceRetentionRuleEditor> RetentionRules { get; }
    public ObservableCollection<TraceStorageRouteEditor> RequiredRoutes { get; }
    public TraceBacklogEditor ImageBacklog { get; }
    public TraceStorageMaintenanceBudgetEditor Scrubber { get; }
    public TraceStorageMaintenanceBudgetEditor Checkpoint { get; }

    public void Load(TraceStoragePolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PolicyId = policy.PolicyId;
        Version = policy.Version;
        ApprovalReference = policy.ApprovalReference;
        ApprovalVersion = policy.ApprovalVersion;
        Rationale = policy.Rationale;
        MinimumReserveBytes = policy.MinimumReserveBytes.ToString(CultureInfo.InvariantCulture);
        MinimumReservePercent = policy.MinimumReservePercent.ToString("G29", CultureInfo.InvariantCulture);
        EvidenceStageTimeout = policy.EvidenceStageTimeout.ToString("c", CultureInfo.InvariantCulture);
        TraceCommitTimeout = policy.TraceCommitTimeout.ToString("c", CultureInfo.InvariantCulture);
        MaximumWalBytes = policy.MaximumWalBytes.ToString(CultureInfo.InvariantCulture);

        RetentionRules.Clear();
        foreach (var rule in policy.RetentionRules)
            RetentionRules.Add(new TraceRetentionRuleEditor(rule));
        RequiredRoutes.Clear();
        foreach (var route in policy.RequiredRoutes)
            RequiredRoutes.Add(new TraceStorageRouteEditor(route));
        ImageBacklog.Load(policy.ImageBacklog);
        Scrubber.Load(policy.Scrubber);
        Checkpoint.Load(policy.Checkpoint);
    }

    public bool TryBuild(out TraceStoragePolicyDefinition? policy,
        out IReadOnlyList<string> errors)
    {
        policy = null;
        var collected = new List<string>();
        var rules = new List<TraceRetentionRule>();
        foreach (var editor in RetentionRules)
        {
            if (!TryParseRetentionRule(editor, out var rule, collected) || rule is null) continue;
            rules.Add(rule);
        }

        var routes = new List<TraceStorageRouteLimit>();
        for (var index = 0; index < RequiredRoutes.Count; index++)
        {
            if (!RequiredRoutes[index].TryBuild(index, out var route, collected) || route is null) continue;
            routes.Add(route);
        }

        var imageBacklog = ImageBacklog.TryBuild("ImageBacklog", collected);
        var scrubber = Scrubber.TryBuild("Scrubber", collected);
        var checkpoint = Checkpoint.TryBuild("Checkpoint", collected);
        var reserveBytes = ParseLong(MinimumReserveBytes, "MinimumReserveBytes", collected);
        var reservePercent = ParseDecimal(MinimumReservePercent, "MinimumReservePercent", collected);
        var stageTimeout = ParseDuration(EvidenceStageTimeout, "EvidenceStageTimeout", collected);
        var commitTimeout = ParseDuration(TraceCommitTimeout, "TraceCommitTimeout", collected);
        var walBytes = ParseLong(MaximumWalBytes, "MaximumWalBytes", collected);

        if (collected.Count == 0 && imageBacklog is not null && scrubber is not null &&
            checkpoint is not null && reserveBytes is not null && reservePercent is not null &&
            stageTimeout is not null && commitTimeout is not null && walBytes is not null)
        {
            try
            {
                policy = new TraceStoragePolicyDefinition(PolicyId, Version, ApprovalReference,
                    ApprovalVersion, Rationale, rules, reserveBytes.Value, reservePercent.Value,
                    routes, imageBacklog, stageTimeout.Value, commitTimeout.Value, scrubber,
                    checkpoint, walBytes.Value);
            }
            catch (ArgumentException exception)
            {
                collected.Add($"Policy: {exception.Message}");
            }
            catch (OverflowException exception)
            {
                collected.Add($"Policy: {exception.Message}");
            }
        }

        errors = new ReadOnlyCollection<string>(collected);
        return policy is not null && collected.Count == 0;
    }

    private static bool TryParseRetentionRule(TraceRetentionRuleEditor editor,
        out TraceRetentionRule? rule, ICollection<string> errors)
    {
        rule = null;
        var valid = true;
        if (!Enum.TryParse<RetentionStartEvent>(editor.StartsAtText, true, out var start) ||
            !Enum.IsDefined(typeof(RetentionStartEvent), start))
        {
            errors.Add($"RetentionRules.{editor.EvidenceClass}.StartsAt: invalid");
            valid = false;
        }
        var duration = ParseDuration(editor.MinimumRetention, $"RetentionRules.{editor.EvidenceClass}.MinimumRetention", errors);
        if (duration is null) valid = false;
        if (!valid || duration is null) return false;
        try
        {
            rule = new TraceRetentionRule(editor.EvidenceClass, start, duration.Value);
            return true;
        }
        catch (ArgumentException exception)
        {
            errors.Add($"RetentionRules.{editor.EvidenceClass}: {exception.Message}");
            return false;
        }
    }

    private static long? ParseLong(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) { errors.Add($"{name}: required"); return null; }
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        { errors.Add($"{name}: invalid"); return null; }
        return value;
    }

    private static decimal? ParseDecimal(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) { errors.Add($"{name}: required"); return null; }
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        { errors.Add($"{name}: invalid"); return null; }
        return value;
    }

    private static TimeSpan? ParseDuration(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) { errors.Add($"{name}: required"); return null; }
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        { errors.Add($"{name}: invalid"); return null; }
        return value;
    }
}

public sealed class TraceRetentionRuleEditor : ObservableObject
{
    private string _startsAtText = string.Empty;
    private string _minimumRetention = string.Empty;

    public TraceRetentionRuleEditor(TraceRetentionClass evidenceClass) => EvidenceClass = evidenceClass;
    internal TraceRetentionRuleEditor(TraceRetentionRule rule)
    {
        EvidenceClass = rule.EvidenceClass;
        _startsAtText = rule.StartsAt.ToString();
        _minimumRetention = rule.MinimumRetention.ToString("c", CultureInfo.InvariantCulture);
    }

    public TraceRetentionClass EvidenceClass { get; }
    public string StartsAtText { get => _startsAtText; set => SetProperty(ref _startsAtText, value ?? string.Empty); }
    public string MinimumRetention { get => _minimumRetention; set => SetProperty(ref _minimumRetention, value ?? string.Empty); }
}

public sealed class TraceStorageRouteEditor : ObservableObject
{
    private string _routeId = string.Empty;
    private string _contractVersion = string.Empty;
    private string _contractHash = string.Empty;

    public TraceStorageRouteEditor() { Limits = new TraceBacklogEditor(); }
    internal TraceStorageRouteEditor(TraceStorageRouteLimit route)
    {
        _routeId = route.RouteId; _contractVersion = route.ContractVersion; _contractHash = route.ContractHash;
        Limits = new TraceBacklogEditor(route.Limits);
    }

    public string RouteId { get => _routeId; set => SetProperty(ref _routeId, value ?? string.Empty); }
    public string ContractVersion { get => _contractVersion; set => SetProperty(ref _contractVersion, value ?? string.Empty); }
    public string ContractHash { get => _contractHash; set => SetProperty(ref _contractHash, value ?? string.Empty); }
    public TraceBacklogEditor Limits { get; }

    internal bool TryBuild(int index, out TraceStorageRouteLimit? route, ICollection<string> errors)
    {
        route = null;
        var maximumItems = TraceStoragePolicyEditor.ParseLongForChild(Limits.MaximumItems, $"RequiredRoutes[{index}].MaximumItems", errors);
        var maximumBytes = TraceStoragePolicyEditor.ParseLongForChild(Limits.MaximumBytes, $"RequiredRoutes[{index}].MaximumBytes", errors);
        var maximumAge = TraceStoragePolicyEditor.ParseDurationForChild(Limits.MaximumOldestAge, $"RequiredRoutes[{index}].MaximumOldestAge", errors);
        if (maximumItems is null || maximumBytes is null || maximumAge is null) return false;
        try
        {
            route = new TraceStorageRouteLimit(RouteId, ContractVersion, ContractHash,
                new TraceBacklogLimits(maximumItems.Value, maximumBytes.Value, maximumAge.Value));
            return true;
        }
        catch (ArgumentException exception)
        {
            errors.Add($"RequiredRoutes[{index}]: {exception.Message}");
            return false;
        }
    }
}

public sealed class TraceBacklogEditor : ObservableObject
{
    private string _maximumItems = string.Empty;
    private string _maximumBytes = string.Empty;
    private string _maximumOldestAge = string.Empty;

    public TraceBacklogEditor() { }
    internal TraceBacklogEditor(TraceBacklogLimits value) => Load(value);
    public string MaximumItems { get => _maximumItems; set => SetProperty(ref _maximumItems, value ?? string.Empty); }
    public string MaximumBytes { get => _maximumBytes; set => SetProperty(ref _maximumBytes, value ?? string.Empty); }
    public string MaximumOldestAge { get => _maximumOldestAge; set => SetProperty(ref _maximumOldestAge, value ?? string.Empty); }
    internal void Load(TraceBacklogLimits value)
    {
        MaximumItems = value.MaximumItems.ToString(CultureInfo.InvariantCulture);
        MaximumBytes = value.MaximumBytes.ToString(CultureInfo.InvariantCulture);
        MaximumOldestAge = value.MaximumOldestAge.ToString("c", CultureInfo.InvariantCulture);
    }
    internal TraceBacklogLimits? TryBuild(string name, ICollection<string> errors)
    {
        var items = TraceStoragePolicyEditor.ParseLongForChild(MaximumItems, $"{name}.MaximumItems", errors);
        var bytes = TraceStoragePolicyEditor.ParseLongForChild(MaximumBytes, $"{name}.MaximumBytes", errors);
        var age = TraceStoragePolicyEditor.ParseDurationForChild(MaximumOldestAge, $"{name}.MaximumOldestAge", errors);
        if (items is null || bytes is null || age is null) return null;
        try { return new TraceBacklogLimits(items.Value, bytes.Value, age.Value); }
        catch (ArgumentException exception) { errors.Add($"{name}: {exception.Message}"); return null; }
    }
}

public sealed class TraceStorageMaintenanceBudgetEditor : ObservableObject
{
    private string _interval = string.Empty;
    private string _maximumRunTime = string.Empty;
    private string _maximumBytes = string.Empty;
    private string _maximumItems = string.Empty;

    public TraceStorageMaintenanceBudgetEditor() { }
    internal TraceStorageMaintenanceBudgetEditor(TraceStorageMaintenanceBudget value) => Load(value);
    public string Interval { get => _interval; set => SetProperty(ref _interval, value ?? string.Empty); }
    public string MaximumRunTime { get => _maximumRunTime; set => SetProperty(ref _maximumRunTime, value ?? string.Empty); }
    public string MaximumBytes { get => _maximumBytes; set => SetProperty(ref _maximumBytes, value ?? string.Empty); }
    public string MaximumItems { get => _maximumItems; set => SetProperty(ref _maximumItems, value ?? string.Empty); }
    internal void Load(TraceStorageMaintenanceBudget value)
    {
        Interval = value.Interval.ToString("c", CultureInfo.InvariantCulture);
        MaximumRunTime = value.MaximumRunTime.ToString("c", CultureInfo.InvariantCulture);
        MaximumBytes = value.MaximumBytes.ToString(CultureInfo.InvariantCulture);
        MaximumItems = value.MaximumItems.ToString(CultureInfo.InvariantCulture);
    }
    internal TraceStorageMaintenanceBudget? TryBuild(string name, ICollection<string> errors)
    {
        var interval = TraceStoragePolicyEditor.ParseDurationForChild(Interval, $"{name}.Interval", errors);
        var runtime = TraceStoragePolicyEditor.ParseDurationForChild(MaximumRunTime, $"{name}.MaximumRunTime", errors);
        var bytes = TraceStoragePolicyEditor.ParseLongForChild(MaximumBytes, $"{name}.MaximumBytes", errors);
        var items = TraceStoragePolicyEditor.ParseLongForChild(MaximumItems, $"{name}.MaximumItems", errors);
        if (interval is null || runtime is null || bytes is null || items is null || items.Value > int.MaxValue)
        {
            if (items is > int.MaxValue) errors.Add($"{name}.MaximumItems: out of range");
            return null;
        }
        try { return new TraceStorageMaintenanceBudget(interval.Value, runtime.Value, bytes.Value, (int)items.Value); }
        catch (ArgumentException exception) { errors.Add($"{name}: {exception.Message}"); return null; }
    }
}

// These parsing helpers remain internal to the editor so route/budget rows
// produce every field error in one TryBuild pass.
public sealed partial class TraceStoragePolicyEditor
{
    internal static long? ParseLongForChild(string text, string name, ICollection<string> errors) =>
        ParseLong(text, name, errors);
    internal static TimeSpan? ParseDurationForChild(string text, string name, ICollection<string> errors) =>
        ParseDuration(text, name, errors);
}
