using System.Diagnostics;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.PartIdentity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PartIdentityContractTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void V143_C01_FormatAndPlcBytesAreBoundedAndDefensivelyCopied()
    {
        var format = Format();
        var cycle = Cycle();
        var bytes = Encoding.UTF8.GetBytes("P-ABC");
        var snapshot = new PartIdentityStablePlcSnapshot(cycle, Hash, 2, 1,
            PartIdentityObservationStatus.Present, bytes, 1, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, Hash);
        var hash = snapshot.ContentHash;

        bytes[0] = (byte)'X';
        var returned = snapshot.GetRawUtf8Bytes();
        returned[0] = (byte)'X';

        Assert.Equal("P-ABC", Encoding.UTF8.GetString(snapshot.GetRawUtf8Bytes()));
        Assert.Equal(Hash, snapshot.ReadEvidenceHash);
        Assert.Equal(hash, snapshot.ContentHash);
        Assert.True(format.TryValidate("P-ABC", out _));
        Assert.False(format.TryValidate("p-ABC", out var reason));
        Assert.Equal("PartIdentityValuePrefixInvalid", reason);
    }

    [Fact]
    public async Task V143_C02_SameStageTokenIsSingleUseUnderConcurrency()
    {
        var binding = Binding();
        var cycle = Cycle();
        var token = Guid.NewGuid();
        var provider = new StagedPartIdentityProvider(binding);
        Assert.True(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);
        var request = Request(provider, binding, cycle, token);

        var observations = await Task.WhenAll(
            provider.TryLatchAsync(request).AsTask(), provider.TryLatchAsync(request).AsTask());

        Assert.Single(observations, value => value.Status == PartIdentityObservationStatus.Present);
        Assert.Single(observations, value => value.Status == PartIdentityObservationStatus.Missing);
        var present = observations.Single(value => value.Status == PartIdentityObservationStatus.Present);
        var result = PartIdentityEvidenceValidator.Validate(Required(), binding, provider.Capabilities,
            request, present,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Assert.True(result.Accepted, result.ReasonCode);
        Assert.Equal("P-ABC", result.Evidence!.Value);
    }

    [Fact]
    public async Task V143_C03_SameBusinessValueDifferentTokensAndCyclesRemainDistinct()
    {
        var binding = Binding();
        var cycle1 = Cycle(cycleSequence: 7);
        var cycle2 = Cycle(cycleSequence: 8);
        var provider = new StagedPartIdentityProvider(binding);
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();
        Assert.True(provider.Stage(new PartIdentityStageRequest(token1, cycle1, "P-ABC")).Accepted);
        Assert.True(provider.Stage(new PartIdentityStageRequest(token2, cycle2, "P-ABC")).Accepted);

        var first = await provider.TryLatchAsync(Request(provider, binding, cycle1, token1));
        var second = await provider.TryLatchAsync(Request(provider, binding, cycle2, token2));

        Assert.Equal(PartIdentityObservationStatus.Present, first.Status);
        Assert.Equal(PartIdentityObservationStatus.Present, second.Status);
        Assert.Equal(first.Value, second.Value);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.Cycle.ContentHash, second.Cycle.ContentHash);
    }

    [Fact]
    public async Task V143_C04_FreshnessUsesMonotonicTimeWhenUtcMovesBackwards()
    {
        var binding = Binding(freshness: TimeSpan.FromSeconds(2));
        var cycle = Cycle();
        var provider = new StagedPartIdentityProvider(binding);
        var token = Guid.NewGuid();
        Assert.True(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);
        var observation = await provider.TryLatchAsync(Request(provider, binding, cycle, token));
        var now = Stopwatch.GetTimestamp();
        var fresh = PartIdentityEvidenceValidator.Validate(Required(), binding,
            provider.Capabilities, Request(provider, binding, cycle, token), observation,
            observation.ObservedAtUtc.AddHours(-1), now, Stopwatch.Frequency);
        Assert.True(fresh.Accepted, fresh.ReasonCode);

        var stale = PartIdentityEvidenceValidator.Validate(Required(), binding,
            provider.Capabilities, Request(provider, binding, cycle, token), observation,
            DateTimeOffset.UtcNow, observation.MonotonicTimestamp + Stopwatch.Frequency * 3,
            Stopwatch.Frequency);
        Assert.False(stale.Accepted);
        Assert.Equal("PartIdentityObservationStale", stale.ReasonCode);
    }

    [Fact]
    public async Task V143_C05_RequiredOptionalAndNoneHaveDifferentMissingSemantics()
    {
        var binding = Binding();
        var cycle = Cycle();
        var provider = new StagedPartIdentityProvider(binding);
        var token = Guid.NewGuid();
        var request = Request(provider, binding, cycle, token);
        Assert.True(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);
        _ = await provider.TryLatchAsync(request);
        var missing = await provider.TryLatchAsync(request);

        var optional = PartIdentityEvidenceValidator.Validate(Optional(), binding, provider.Capabilities,
            request, missing,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Assert.True(optional.Accepted);
        Assert.Equal(PartIdentityEvidenceState.NotProvided, optional.Evidence!.State);

        var required = PartIdentityEvidenceValidator.Validate(Required(), binding, provider.Capabilities,
            request, missing,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Assert.False(required.Accepted);
        Assert.Equal("PartIdentityRequiredMissing", required.ReasonCode);

        var none = PartIdentityEvidenceValidator.Validate(PartIdentityRequirement.None,
            null, null, null, null, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Assert.True(none.Accepted);
        Assert.Null(none.Evidence);
        Assert.Equal("PartIdentityNotRequired", none.ReasonCode);
    }

    [Theory]
    [InlineData(PartIdentityObservationStatus.Ambiguous, "PartIdentityObservationAmbiguous")]
    [InlineData(PartIdentityObservationStatus.Invalid, "PartIdentityObservationInvalid")]
    [InlineData(PartIdentityObservationStatus.Stale, "PartIdentityObservationStale")]
    [InlineData(PartIdentityObservationStatus.Error, "PartIdentityObservationError")]
    public void V143_C06_OptionalDoesNotTurnProviderFailuresIntoEvidence(
        PartIdentityObservationStatus status, string reason)
    {
        var binding = Binding();
        var cycle = Cycle();
        var sourceEpoch = Guid.Parse("14314314-1431-4314-8314-143143143144");
        var request = new PartIdentityLatchRequest(binding, cycle, 1, sourceEpoch, 1, Guid.NewGuid());
        var observation = new PartIdentityProviderObservation(binding, cycle, status, null,
            "PartIdentityProviderFailure", 1, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, sourceEpoch, 1, request.StageToken);

        var result = PartIdentityEvidenceValidator.Validate(Optional(), binding,
            Capabilities(binding, sourceEpoch), request, observation,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

        Assert.False(result.Accepted);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void V143_C07_BindingAndCycleMismatchesAreRejectedAtRuntimeBoundary()
    {
        var binding = Binding();
        var otherBinding = new PartIdentityProviderBinding("V143.OtherBinding", "1", "PartCode",
            PartIdentityProviderSourceKind.Staged, "V143.Staged", "1", Hash, Format(),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 2);
        var cycle = Cycle();
        var otherCycle = Cycle(connectionGeneration: cycle.ConnectionGeneration + 1);
        var sourceEpoch = Guid.Parse("14314314-1431-4314-8314-143143143144");
        var request = new PartIdentityLatchRequest(binding, cycle, 1, sourceEpoch, 1, Guid.NewGuid());
        var observation = new PartIdentityProviderObservation(otherBinding, otherCycle,
            PartIdentityObservationStatus.Present, "P-ABC", "PartIdentityStaged",
            1, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            sourceEpoch, 1, request.StageToken);

        var result = PartIdentityEvidenceValidator.Validate(Required(), binding,
            Capabilities(binding, sourceEpoch), request, observation,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

        Assert.False(result.Accepted);
        Assert.Equal("PartIdentityObservationBindingMismatch", result.ReasonCode);
    }

    [Fact]
    public void V143_C08_StablePlcProofBindsRawUtf8RevisionAndCycle()
    {
        var format = Format();
        var binding = new PartIdentityProviderBinding("V143.PlcBinding", "1", "PartCode",
            PartIdentityProviderSourceKind.StablePlc, "V143.Plc", "1", Hash, format,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 1);
        var cycle = Cycle();
        var sourceEpoch = Guid.Parse("14314314-1431-4314-8314-143143143144");
        var now = Stopwatch.GetTimestamp();
        var snapshot = new PartIdentityStablePlcSnapshot(cycle, Hash, 2, 1,
            PartIdentityObservationStatus.Present, Encoding.UTF8.GetBytes("P-ABC"), 1,
            DateTimeOffset.UtcNow, now, Stopwatch.Frequency, Hash);
        var request = new PartIdentityLatchRequest(binding, cycle, 1, sourceEpoch, 1,
            stablePlcSnapshot: snapshot);
        var observation = new PartIdentityProviderObservation(binding, cycle,
            PartIdentityObservationStatus.Present, "P-ABC", "PartIdentityPlcStable",
            1, snapshot.ObservedAtUtc, now, Stopwatch.Frequency,
            sourceEpoch, 1,
            stablePlcSnapshot: snapshot);

        var result = PartIdentityEvidenceValidator.Validate(Required(), binding,
            Capabilities(binding, sourceEpoch), request, observation,
            DateTimeOffset.UtcNow, now, Stopwatch.Frequency);
        Assert.True(result.Accepted, result.ReasonCode);
        Assert.Equal(PartIdentityProviderSourceKind.StablePlc, result.Evidence!.SourceKind);

        var unstable = new PartIdentityStablePlcSnapshot(cycle, Hash, 3, 1,
            PartIdentityObservationStatus.Present, Encoding.UTF8.GetBytes("P-ABC"), 1,
            snapshot.ObservedAtUtc, now, Stopwatch.Frequency, Hash);
        var badRequest = new PartIdentityLatchRequest(binding, cycle, 1, sourceEpoch, 1,
            stablePlcSnapshot: unstable);
        var badObservation = new PartIdentityProviderObservation(binding, cycle,
            PartIdentityObservationStatus.Present, "P-ABC", "PartIdentityPlcStable",
            1, snapshot.ObservedAtUtc, now, Stopwatch.Frequency,
            sourceEpoch, 1,
            stablePlcSnapshot: unstable);
        var rejected = PartIdentityEvidenceValidator.Validate(Required(), binding,
            Capabilities(binding, sourceEpoch), badRequest,
            badObservation, DateTimeOffset.UtcNow, now, Stopwatch.Frequency);
        Assert.False(rejected.Accepted);
        Assert.Equal("PartIdentityPlcRevisionUnstable", rejected.ReasonCode);
    }

    [Fact]
    public async Task V143_C09_WrongAssociationDoesNotConsumeStagedValue()
    {
        var binding = Binding();
        var cycle = Cycle(cycleSequence: 7);
        var wrongCycle = Cycle(cycleSequence: 8);
        var token = Guid.NewGuid();
        var provider = new StagedPartIdentityProvider(binding);
        Assert.True(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);

        var wrong = await provider.TryLatchAsync(Request(provider, binding, wrongCycle, token));
        Assert.Equal(PartIdentityObservationStatus.Invalid, wrong.Status);
        var correct = await provider.TryLatchAsync(Request(provider, binding, cycle, token));
        Assert.Equal(PartIdentityObservationStatus.Present, correct.Status);
        var replay = await provider.TryLatchAsync(Request(provider, binding, cycle, token));
        Assert.Equal(PartIdentityObservationStatus.Missing, replay.Status);
        Assert.False(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);
    }

    [Fact]
    public async Task V143_C10_ExactCycleMissingAndAmbiguousAreExplicit()
    {
        var binding = Binding();
        var cycle = Cycle();
        var provider = new StagedPartIdentityProvider(binding);
        var firstToken = Guid.NewGuid();
        var secondToken = Guid.NewGuid();
        Assert.True(provider.Stage(new PartIdentityStageRequest(firstToken, cycle, "P-ABC")).Accepted);
        Assert.True(provider.Stage(new PartIdentityStageRequest(secondToken, cycle, "P-XYZ")).Accepted);

        var exactCycleRequest = Request(provider, binding, cycle);
        var ambiguous = await provider.TryLatchAsync(exactCycleRequest);
        Assert.Equal(PartIdentityObservationStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.StageToken);

        var first = await provider.TryLatchAsync(Request(provider, binding, cycle, firstToken));
        Assert.Equal(PartIdentityObservationStatus.Present, first.Status);
        var second = await provider.TryLatchAsync(Request(provider, binding, cycle, secondToken));
        Assert.Equal(PartIdentityObservationStatus.Present, second.Status);

        var missing = await provider.TryLatchAsync(Request(provider, binding, Cycle(cycleSequence: 8)));
        Assert.Equal(PartIdentityObservationStatus.Missing, missing.Status);
        Assert.Null(missing.StageToken);
        var missingResult = PartIdentityEvidenceValidator.Validate(Optional(), binding,
            provider.Capabilities, Request(provider, binding, Cycle(cycleSequence: 8)), missing,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Assert.True(missingResult.Accepted, missingResult.ReasonCode);
        Assert.Equal(PartIdentityEvidenceState.NotProvided, missingResult.Evidence!.State);
    }

    [Fact]
    public async Task V143_C11_SourceRestartChangesEpochAndRevokesPendingValue()
    {
        var binding = Binding();
        var cycle = Cycle();
        var provider = new StagedPartIdentityProvider(binding);
        var token = Guid.NewGuid();
        Assert.True(provider.Stage(new PartIdentityStageRequest(token, cycle, "P-ABC")).Accepted);
        var before = provider.Capabilities;
        var oldRequest = new PartIdentityLatchRequest(binding, cycle, 1,
            before.SourceEpoch, before.SourceGeneration, token);
        var oldObservation = await provider.TryLatchAsync(oldRequest);
        PartIdentitySourceChangedEventArgs? changed = null;
        provider.SourceChanged += (_, args) => changed = args;

        var after = provider.RestartSource();

        Assert.NotEqual(before.SourceEpoch, after.SourceEpoch);
        Assert.Equal(before.SourceGeneration + 1, after.SourceGeneration);
        Assert.Same(after, changed!.Capabilities);
        var revoked = await provider.TryLatchAsync(oldRequest);
        Assert.Equal(PartIdentityObservationStatus.Error, revoked.Status);
        Assert.Equal("PartIdentitySourceGenerationMismatch", revoked.ReasonCode);
        var rejected = PartIdentityEvidenceValidator.Validate(Required(), binding, after,
            oldRequest, oldObservation, DateTimeOffset.UtcNow,
            oldObservation.MonotonicTimestamp, oldObservation.MonotonicFrequency);
        Assert.False(rejected.Accepted);
        Assert.Equal("PartIdentityRequestSourceGenerationMismatch", rejected.ReasonCode);
    }

    private static PartIdentityFormat Format() => new("V143.PartIdentityFormat", "1", 3, 32,
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-", "P-");

    private static PartIdentityProviderBinding Binding(TimeSpan? freshness = null) =>
        new("V143.PartIdentityBinding", "1", "PartCode", PartIdentityProviderSourceKind.Staged,
            "V143.StagedProvider", "1", Hash, Format(), freshness ?? TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), 2);

    private static PartIdentityLatchRequest Request(StagedPartIdentityProvider provider,
        PartIdentityProviderBinding binding, PartIdentityCycleBinding cycle, Guid? stageToken = null) =>
        new(binding, cycle, 1, provider.Capabilities.SourceEpoch,
            provider.Capabilities.SourceGeneration, stageToken);

    private static PartIdentityProviderCapabilities Capabilities(
        PartIdentityProviderBinding binding, Guid sourceEpoch) => new(true, true,
        binding.SourceKind, binding.MaximumCallsPerCycle, binding.ContentHash, sourceEpoch, 1);

    private static PartIdentityCycleBinding Cycle(long connectionGeneration = 3,
        uint controllerEpoch = 61, uint cycleSequence = 7) => new(
        Guid.Parse("14314314-1431-4314-8314-143143143143"), Hash, connectionGeneration,
        controllerEpoch, cycleSequence);

    private static PartIdentityRequirement Required() => new(PartIdentityRequirementMode.Required,
        "PartCode", Format().ToContractReference());

    private static PartIdentityRequirement Optional() => new(PartIdentityRequirementMode.Optional,
        "PartCode", Format().ToContractReference());
}
