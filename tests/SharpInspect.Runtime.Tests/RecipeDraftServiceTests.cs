using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftServiceTests
{
    [Fact]
    public async Task V115_S01_SemanticValidationDoesNotCreateOrWarmAnAlgorithm()
    {
        var policy = ExecutionPolicy();
        var factory = new CountingFactory(Contract(policy));
        await using var service = CreateService(factory, policy);

        var result = await service.ValidateAsync(factory.Content);

        Assert.True(result.Valid, result.ReasonCode);
        Assert.Equal("RecipeDraftValid", result.ReasonCode);
        Assert.Equal(1, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, factory.WarmUpCalls);
    }

    [Fact]
    public async Task V115_S02_MismatchedRegistrationIsRejectedBeforeFactoryValidation()
    {
        var policy = ExecutionPolicy();
        var registered = new CountingFactory(Contract(policy));
        var content = registered.Content;
        var differentBinding = new RecipeAlgorithmBinding(new AlgorithmIdentity("Different.Algorithm", "1"),
            content.Algorithm.ConfigurationSchema, content.Algorithm.ResultSchema, content.Algorithm.OverlayContract);
        content = new RecipeDraftContent(content.RecipeKey, content.DisplayName, differentBinding,
            content.Configuration, content.CameraRole, content.Camera, content.AlgorithmExecutionTimeout,
            content.AssetRequirements, content.PolicyRequirements, content.ValueOrigins);
        await using var service = CreateService(registered, policy);

        var result = await service.ValidateAsync(content);

        Assert.False(result.Valid);
        Assert.Equal("RecipeDraftAlgorithmNotRegistered", result.ReasonCode);
        Assert.Equal(0, registered.ValidateCalls);
        Assert.Equal(0, registered.CreateCalls);
    }

    [Fact]
    public async Task V115_S03_ValidatorTimeoutRetainsSharedFactoryGateUntilLateWorkEnds()
    {
        var policy = ExecutionPolicy();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CountingFactory(Contract(policy), async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return Array.Empty<AlgorithmValidationIssue>();
        });
        // Warm the structural path and deliberately hold the physical factory
        // gate with a wide budget. The short-budget assertion below must measure
        // gate retention, not cold JIT/codec work before Task.Run reaches the
        // factory callback.
        var holdingOptions = new ProductionStoreOptions
        {
            CommitTimeout = TimeSpan.FromSeconds(2),
            RecipeDrafts = new RecipeDraftStoreOptions(policy)
        };
        var shortOptions = new ProductionStoreOptions
        {
            CommitTimeout = TimeSpan.FromMilliseconds(250),
            RecipeDrafts = new RecipeDraftStoreOptions(policy)
        };
        await using var holdingService = new RecipeDraftService(new[] { factory }, holdingOptions,
            UninitializedAuthorization(), new EmptyHistory());
        await using var shortService = new RecipeDraftService(new[] { factory }, shortOptions,
            UninitializedAuthorization(), new EmptyHistory());

        var first = holdingService.ValidateAsync(factory.Content).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(firstResult.Valid);
            Assert.Equal("RecipeDraftSemanticValidationTimedOut", firstResult.ReasonCode);
            Assert.False(factory.ValidationFinished);

            var second = await shortService.ValidateAsync(factory.Content);
            Assert.False(second.Valid);
            Assert.Equal("RecipeDraftSemanticValidationTimedOut", second.ReasonCode);
            Assert.Equal(1, factory.MaximumConcurrentValidationCalls);
            Assert.Equal(1, factory.ValidateCalls);

            // The short service's physical wait is also a late continuation.
            // Drain it before releasing the first callback so the recovery call
            // below has an exact, observable factory-call count.
            await shortService.DisposeAsync();
            Assert.Equal(1, factory.ValidateCalls);

            release.TrySetResult(true);
            await EventuallyAsync(() => factory.MaximumConcurrentValidationCalls == 1 && factory.ValidationFinished);

            await using var recoveredService = new RecipeDraftService(new[] { factory }, shortOptions,
                UninitializedAuthorization(), new EmptyHistory());
            var recovered = await recoveredService.ValidateAsync(factory.Content);
            Assert.True(recovered.Valid, recovered.ReasonCode);
            Assert.Equal(2, factory.ValidateCalls);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task V115_S04_SavePerformsAuthorizationBeforeCallingFactory()
    {
        var policy = ExecutionPolicy();
        var factory = new CountingFactory(Contract(policy));
        await using var service = CreateService(factory, policy);
        var request = new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            factory.Content, "测试保存", new(CommandSource.Integration));

        var result = await service.SaveAsync(request);

        Assert.False(result.Saved);
        Assert.StartsWith("RecipeDraftAuthorization", result.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(0, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task V115_S05_SixteenValidationReservationsBoundConcurrentStructuralWork()
    {
        var policy = ExecutionPolicy();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CountingFactory(Contract(policy), async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return Array.Empty<AlgorithmValidationIssue>();
        });
        var options = new ProductionStoreOptions
        {
            CommitTimeout = TimeSpan.FromSeconds(2), RecipeDrafts = new RecipeDraftStoreOptions(policy)
        };
        await using var service = new RecipeDraftService(new[] { factory }, options, UninitializedAuthorization(),
            new EmptyHistory());

        var pending = Enumerable.Range(0, 16)
            .Select(_ => service.ValidateAsync(factory.Content).AsTask()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await service.ValidateAsync(factory.Content);

        Assert.False(rejected.Valid);
        Assert.Equal("RecipeDraftValidationCapacityExceeded", rejected.ReasonCode);
        release.TrySetResult(true);
        var results = await Task.WhenAll(pending);
        Assert.All(results, result => Assert.True(result.Valid, result.ReasonCode));
    }

    [Fact]
    public async Task V115_S06_ConcurrentDisposeDoesNotLeakRawErrorsOrAdmitLaterSaves()
    {
        var policy = ExecutionPolicy();
        var factory = new CountingFactory(Contract(policy));
        var service = CreateService(factory, policy);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 32)
            .Select(_ => new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                factory.Content, "并发关闭", new(CommandSource.Integration)))
            .ToArray();

        try
        {
            var saves = requests.Select(request => Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                return await service.SaveAsync(request).ConfigureAwait(false);
            })).ToArray();
            var dispose = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await service.DisposeAsync().ConfigureAwait(false);
            });

            start.TrySetResult(true);
            await Task.WhenAll(saves);
            await dispose;

            Assert.All(saves, task =>
            {
                var result = task.Result;
                Assert.False(result.Saved);
                Assert.DoesNotContain("ObjectDisposedException", result.ReasonCode,
                    StringComparison.Ordinal);
            });

            var afterDispose = await service.SaveAsync(requests[0]);
            Assert.False(afterDispose.Saved);
            Assert.Equal("RecipeDraftServiceDisposed", afterDispose.ReasonCode);
            Assert.Equal(0, factory.ValidateCalls);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    private static RecipeDraftService CreateService(CountingFactory factory, AlgorithmExecutionPolicy policy) =>
        new(new[] { factory }, new ProductionStoreOptions
        {
            RecipeDrafts = new RecipeDraftStoreOptions(policy), CommitTimeout = TimeSpan.FromSeconds(1)
        }, UninitializedAuthorization(), new EmptyHistory());

    private static LocalAuthorizationService UninitializedAuthorization() =>
        (LocalAuthorizationService)RuntimeHelpers.GetUninitializedObject(typeof(LocalAuthorizationService));

    private static AlgorithmExecutionPolicy ExecutionPolicy() =>
        new("Draft.Execution", "1", TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1));

    private static ContractDescriptor Contract(AlgorithmExecutionPolicy policy)
    {
        var schema = new AlgorithmConfigurationSchema("Draft.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100), AlgorithmScalarValue.FromInt64(5))
        });
        var configuration = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            new AlgorithmConfigurationEntry("Threshold", "items", AlgorithmScalarValue.FromInt64(5))
        });
        var overlay = new OverlayContract("Draft.Overlay", "1", 0, 64, 16);
        var result = new AlgorithmResultSchema("Draft.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "NoDefect" }, overlay);
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("Draft.Algorithm", "1"), schema, result);
        var binding = RecipeAlgorithmBinding.FromDescriptor(descriptor);
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            100, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var requirement = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
            new RecipeContractReference(policy.Id, policy.Version, policy.ContentHash));
        var content = new RecipeDraftContent("Draft.Recipe", "Draft recipe", binding, configuration,
            "TopCamera", camera, TimeSpan.FromMilliseconds(100), null, new[] { requirement });
        return new ContractDescriptor(descriptor, content);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate());
    }

    private sealed record ContractDescriptor(AlgorithmDescriptor Descriptor, RecipeDraftContent Content);

    private sealed class CountingFactory : IVisionAlgorithmFactory
    {
        private readonly Func<AlgorithmConfigurationSnapshot, CancellationToken,
            Task<IReadOnlyList<AlgorithmValidationIssue>>> _validate;
        private int _active;
        private int _maximumConcurrent;
        private int _finished;
        private int _validateCalls;
        private int _createCalls;
        private int _warmUpCalls;

        internal CountingFactory(ContractDescriptor contract,
            Func<AlgorithmConfigurationSnapshot, CancellationToken,
                Task<IReadOnlyList<AlgorithmValidationIssue>>>? validate = null)
        {
            Descriptor = contract.Descriptor;
            Content = contract.Content;
            _validate = validate ?? ((_, _) => Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>()));
        }

        internal RecipeDraftContent Content { get; }
        internal int ValidateCalls => Volatile.Read(ref _validateCalls);
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal int WarmUpCalls => Volatile.Read(ref _warmUpCalls);
        internal int MaximumConcurrentValidationCalls => Volatile.Read(ref _maximumConcurrent);
        internal bool ValidationFinished => Volatile.Read(ref _finished) > 0;
        public AlgorithmDescriptor Descriptor { get; }

        public async ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _validateCalls);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrent);
                if (active <= maximum || Interlocked.CompareExchange(ref _maximumConcurrent, active, maximum) == maximum)
                    break;
            }
            try { return await _validate(configuration, cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); Interlocked.Increment(ref _finished); }
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCalls);
            return ValueTask.FromResult<IVisionAlgorithm>(new CountingAlgorithm(this));
        }

        private sealed class CountingAlgorithm : IVisionAlgorithm
        {
            private readonly CountingFactory _owner;
            internal CountingAlgorithm(CountingFactory owner) => _owner = owner;
            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _owner._warmUpCalls);
                return ValueTask.CompletedTask;
            }
            public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyHistory : IRecipeDraftHistoryQuery
    {
        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeDraftReadResult(false, "RecipeDraftQueryUnavailable", null));
        public ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeDraftPage(false, "RecipeDraftQueryUnavailable",
                Array.Empty<RecipeDraftRevision>(), 0, null));
    }
}
