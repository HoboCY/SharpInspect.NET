using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Deployment-owned bounds for one algorithm execution deadline and its bounded
/// cooperative-cancellation grace period. It contains no implicit timing defaults.
/// </summary>
public sealed class AlgorithmExecutionPolicy
{
    private const long MaximumRepresentableDurationTicks =
        (long)int.MaxValue * TimeSpan.TicksPerMillisecond;

    public AlgorithmExecutionPolicy(string id, string version,
        TimeSpan minimumExecutionTimeout, TimeSpan maximumExecutionTimeout,
        TimeSpan cancellationGracePeriod)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        ValidateDuration(minimumExecutionTimeout, nameof(minimumExecutionTimeout));
        ValidateDuration(maximumExecutionTimeout, nameof(maximumExecutionTimeout));
        ValidateDuration(cancellationGracePeriod, nameof(cancellationGracePeriod));
        if (minimumExecutionTimeout > maximumExecutionTimeout)
            throw new ArgumentException("AlgorithmExecutionTimeoutBoundsInvalid", nameof(maximumExecutionTimeout));

        MinimumExecutionTimeout = minimumExecutionTimeout;
        MaximumExecutionTimeout = maximumExecutionTimeout;
        CancellationGracePeriod = cancellationGracePeriod;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-algorithm-execution-policy-v1",
            Id,
            Version,
            minimumExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            maximumExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            cancellationGracePeriod.Ticks.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string Id { get; }
    public string Version { get; }
    public TimeSpan MinimumExecutionTimeout { get; }
    public TimeSpan MaximumExecutionTimeout { get; }
    public TimeSpan CancellationGracePeriod { get; }
    public string ContentHash { get; }

    /// <summary>
    /// Binds a pending Recipe request to this immutable policy. The resulting snapshot
    /// is evidence for a later run; it does not assert that the Recipe is released or
    /// that the station is authorized to execute it.
    /// </summary>
    public bool TryBind(AlgorithmExecutionRequest? request,
        out AlgorithmExecutionTimingSnapshot? snapshot, out string reasonCode)
    {
        snapshot = null;
        if (request is null)
            return Failure("AlgorithmExecutionRequestRequired", out reasonCode);
        if (request.Recipe is null)
            return Failure("RecipeRequired", out reasonCode);

        var recipe = request.Recipe;
        if (!IsMachineIdentity(recipe.Id) || !IsMachineIdentity(recipe.Version))
            return Failure("RecipeIdentityInvalid", out reasonCode);
        if (!IsSha256(recipe.ContentHash))
            return Failure("RecipeContentHashInvalid", out reasonCode);
        if (!IsRepresentableDuration(request.AlgorithmExecutionTimeout))
            return Failure("AlgorithmExecutionTimeoutInvalid", out reasonCode);
        if (request.AlgorithmExecutionTimeout < MinimumExecutionTimeout)
            return Failure("AlgorithmExecutionTimeoutBelowMinimum", out reasonCode);
        if (request.AlgorithmExecutionTimeout > MaximumExecutionTimeout)
            return Failure("AlgorithmExecutionTimeoutAboveMaximum", out reasonCode);

        snapshot = new AlgorithmExecutionTimingSnapshot(recipe, Id, Version, ContentHash,
            request.AlgorithmExecutionTimeout, CancellationGracePeriod);
        reasonCode = "AlgorithmExecutionTimingBound";
        return true;
    }

    internal static bool IsRepresentableDuration(TimeSpan value) =>
        value > TimeSpan.Zero &&
        value.Ticks % TimeSpan.TicksPerMillisecond == 0 &&
        value.Ticks <= MaximumRepresentableDurationTicks;

    private static void ValidateDuration(TimeSpan value, string parameterName)
    {
        if (!IsRepresentableDuration(value))
            throw new ArgumentOutOfRangeException(parameterName, "AlgorithmExecutionDurationInvalid");
    }

    private static bool IsMachineIdentity(string? value)
    {
        if (value is null) return false;
        try
        {
            _ = AlgorithmContractValidation.Identifier(value, nameof(value));
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static bool Failure(string reason, out string reasonCode)
    {
        reasonCode = reason;
        return false;
    }
}

/// <summary>A pending request that has not yet been admitted by a timing policy.</summary>
public sealed record AlgorithmExecutionRequest(RecipeReference Recipe, TimeSpan AlgorithmExecutionTimeout);

/// <summary>
/// Immutable timing evidence created only by <see cref="AlgorithmExecutionPolicy.TryBind"/>.
/// Its constructor is Runtime-only so callers cannot manufacture a bound snapshot.
/// </summary>
public sealed class AlgorithmExecutionTimingSnapshot
{
    private readonly RecipeReference _recipe;

    internal AlgorithmExecutionTimingSnapshot(RecipeReference recipe, string policyId,
        string policyVersion, string policyContentHash, TimeSpan algorithmExecutionTimeout,
        TimeSpan cancellationGracePeriod)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        _recipe = new RecipeReference(recipe.Id, recipe.Version, recipe.ContentHash);
        PolicyId = policyId;
        PolicyVersion = policyVersion;
        PolicyContentHash = policyContentHash;
        AlgorithmExecutionTimeout = algorithmExecutionTimeout;
        CancellationGracePeriod = cancellationGracePeriod;
    }

    /// <summary>Returns a defensive immutable copy of the bound Recipe identity.</summary>
    public RecipeReference Recipe => new(_recipe.Id, _recipe.Version, _recipe.ContentHash);
    public string PolicyId { get; }
    public string PolicyVersion { get; }
    public string PolicyContentHash { get; }
    public TimeSpan AlgorithmExecutionTimeout { get; }
    public TimeSpan CancellationGracePeriod { get; }
}
