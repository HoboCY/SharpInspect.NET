using System.Diagnostics;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmPreparationTests
{
    [Fact]
    public async Task V110_R01_SemanticValidationCreatesWarmsAndPublishesOneImmutableHandle()
    {
        var contract = CreateContract("R01");
        var algorithm = new TestAlgorithm();
        var factory = new TestFactory(contract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(algorithm));
        await using var service = Service(factory);

        var result = await service.PrepareAsync(Request(contract));

        Assert.True(result.Succeeded, result.ReasonCode);
        var prepared = Assert.IsType<PreparedAlgorithm>(result.Prepared);
        Assert.NotEqual(Guid.Empty, prepared.InstanceId);
        Assert.Same(contract.Descriptor, prepared.Descriptor);
        Assert.NotSame(contract.Configuration, prepared.Configuration);
        Assert.Equal(contract.Configuration.ContentHash, prepared.Configuration.ContentHash);
        Assert.Equal(1, factory.ValidateCalls);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, algorithm.WarmCalls);
        Assert.Equal(1, service.OwnedInstanceCount);
        Assert.Null(typeof(PreparedAlgorithm).GetProperty("Algorithm", BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(typeof(PreparedAlgorithm).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => typeof(IVisionAlgorithm).IsAssignableFrom(property.PropertyType));

        await prepared.DisposeAsync();
        await prepared.DisposeAsync();
        Assert.True(prepared.IsRetired);
        Assert.Equal(1, algorithm.DisposeCalls);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Fact]
    public async Task V110_R02_TwoDistinctRegisteredFactoriesPrepareIndependently()
    {
        var firstContract = CreateContract("R02A");
        var secondContract = CreateContract("R02B");
        var firstFactory = new TestFactory(firstContract.Descriptor);
        var secondFactory = new TestFactory(secondContract.Descriptor);
        await using var service = Service(firstFactory, secondFactory);

        var first = await service.PrepareAsync(Request(firstContract));
        var second = await service.PrepareAsync(Request(secondContract));

        Assert.True(first.Succeeded, first.ReasonCode);
        Assert.True(second.Succeeded, second.ReasonCode);
        Assert.Equal(2, service.OwnedInstanceCount);
        Assert.Equal(1, firstFactory.CreateCalls);
        Assert.Equal(1, secondFactory.CreateCalls);
        await first.Prepared!.DisposeAsync();
        await second.Prepared!.DisposeAsync();
    }

    [Fact]
    public async Task V110_R03_UnknownAndMismatchedBindingsFailBeforeFactory()
    {
        var contract = CreateContract("R03");
        var factory = new TestFactory(contract.Descriptor);
        await using var service = Service(factory);
        var valid = Request(contract);
        var wrongType = new AlgorithmConfigurationSnapshot(contract.Configuration.SchemaId,
            contract.Configuration.SchemaVersion, contract.Configuration.SchemaContentHash,
            contract.Configuration.CanonicalizationVersion, contract.Configuration.ContentHash,
            new[] { new AlgorithmConfigurationEntry("Threshold", "px", AlgorithmScalarValue.FromString("wrong")) });
        var wrongHash = new AlgorithmConfigurationSnapshot(contract.Configuration.SchemaId,
            contract.Configuration.SchemaVersion, contract.Configuration.SchemaContentHash,
            contract.Configuration.CanonicalizationVersion, new string('0', 64), contract.Configuration.Values);
        var wrongSchema = new AlgorithmConfigurationSnapshot("OtherSchema", contract.Configuration.SchemaVersion,
            contract.Configuration.SchemaContentHash, contract.Configuration.CanonicalizationVersion,
            contract.Configuration.ContentHash, contract.Configuration.Values);

        var cases = new[]
        {
            ("unknown", valid with { Algorithm = new AlgorithmIdentity("UnknownAlgorithm", "1") }, "AlgorithmNotRegistered"),
            ("missing", valid with { Configuration = null! }, "AlgorithmConfigurationRequired"),
            ("wrong-type", valid with { Configuration = wrongType }, "AlgorithmConfigurationInvalid"),
            ("wrong-hash", valid with { Configuration = wrongHash }, "AlgorithmConfigurationInvalid"),
            ("wrong-schema", valid with { Configuration = wrongSchema }, "AlgorithmConfigurationInvalid"),
            ("wrong-result", valid with { ResultSchemaId = "OtherResult" }, "AlgorithmResultBindingMismatch"),
            ("wrong-result-version", valid with { ResultSchemaVersion = "2" }, "AlgorithmResultBindingMismatch"),
            ("wrong-result-hash", valid with { ResultSchemaContentHash = new string('F', 64) }, "AlgorithmResultBindingMismatch"),
            ("wrong-overlay", valid with { OverlayContractId = "OtherOverlay" }, "AlgorithmResultBindingMismatch"),
            ("wrong-overlay-version", valid with { OverlayContractVersion = "2" }, "AlgorithmResultBindingMismatch"),
            ("wrong-overlay-hash", valid with { OverlayContractContentHash = new string('F', 64) }, "AlgorithmResultBindingMismatch")
        };

        foreach (var (_, request, reason) in cases)
        {
            var result = await service.PrepareAsync(request);
            Assert.False(result.Succeeded, reason);
            Assert.Equal(reason, result.ReasonCode);
            Assert.Null(result.Prepared);
        }

        Assert.Equal(0, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task V110_R04_SemanticCreateAndWarmFailuresDoNotPublishOrLeakHandles()
    {
        var semanticContract = CreateContract("R04S");
        var semanticFactory = new TestFactory(semanticContract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                new[] { new AlgorithmValidationIssue("SemanticConfigurationInvalid", "Threshold") }));
        await using (var semanticService = Service(semanticFactory))
        {
            var result = await semanticService.PrepareAsync(Request(semanticContract));
            Assert.False(result.Succeeded);
            Assert.Equal("AlgorithmSemanticValidationFailed", result.ReasonCode);
            Assert.Equal(1, semanticFactory.ValidateCalls);
            Assert.Equal(0, semanticFactory.CreateCalls);
            Assert.Equal(0, semanticService.OwnedInstanceCount);
        }

        var createContract = CreateContract("R04C");
        var createFactory = new TestFactory(createContract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => throw new InvalidOperationException("create failure"));
        await using (var createService = Service(createFactory))
        {
            var result = await createService.PrepareAsync(Request(createContract));
            Assert.False(result.Succeeded);
            Assert.Equal("AlgorithmCreationFailed", result.ReasonCode);
            Assert.Equal(1, createFactory.CreateCalls);
            Assert.Equal(0, createService.OwnedInstanceCount);
        }

        var warmContract = CreateContract("R04W");
        var warmAlgorithm = new TestAlgorithm(warm: _ =>
            throw new InvalidOperationException("warm failure"));
        var warmFactory = new TestFactory(warmContract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(warmAlgorithm));
        await using (var warmService = Service(warmFactory))
        {
            var result = await warmService.PrepareAsync(Request(warmContract));
            Assert.False(result.Succeeded);
            Assert.Equal("AlgorithmWarmUpFailed", result.ReasonCode);
            Assert.Equal(1, warmAlgorithm.WarmCalls);
            await EventuallyAsync(() => warmAlgorithm.DisposeCalls == 1);
            Assert.Null(result.Prepared);
            Assert.Equal(0, warmService.OwnedInstanceCount);
        }
    }

    [Fact]
    public async Task V110_R05_SameFactoryIsSerializedAndConcurrentRequestsDoNotOverlap()
    {
        var contract = CreateContract("R05");
        var factory = new TestFactory(contract.Descriptor,
            async (_, _) =>
            {
                await Task.Delay(20);
                return Array.Empty<AlgorithmValidationIssue>();
            },
            async (_, _) =>
            {
                await Task.Delay(20);
                return (IVisionAlgorithm)new TestAlgorithm();
            });
        await using var service = Service(factory);

        var results = await Task.WhenAll(
            service.PrepareAsync(Request(contract)).AsTask(),
            service.PrepareAsync(Request(contract)).AsTask());

        Assert.All(results, result => Assert.True(result.Succeeded, result.ReasonCode));
        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(1, factory.MaximumConcurrentCalls);
        await Task.WhenAll(results.Select(result => result.Prepared!.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task V110_R06_ReturningAnOwnedInstanceIsRejectedWithoutDisposingTheFirstOwner()
    {
        var contract = CreateContract("R06");
        var shared = new TestAlgorithm();
        var factory = new TestFactory(contract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(shared));
        await using var service = Service(factory);

        var first = await service.PrepareAsync(Request(contract));
        var second = await service.PrepareAsync(Request(contract));

        Assert.True(first.Succeeded, first.ReasonCode);
        Assert.False(second.Succeeded);
        Assert.Equal("AlgorithmInstanceAlreadyOwned", second.ReasonCode);
        Assert.Null(second.Prepared);
        Assert.Equal(0, shared.DisposeCalls);
        Assert.Equal(1, service.OwnedInstanceCount);
        await first.Prepared!.DisposeAsync();
        Assert.Equal(1, shared.DisposeCalls);
    }

    [Fact]
    public async Task V110_R07_UncooperativeCreateAndWarmTimeoutsRetainCapacityUntilLateCleanup()
    {
        var createContract = CreateContract("R07C");
        var createStarted = NewSignal();
        var releaseCreate = NewSignal();
        var lateCreate = new TestAlgorithm();
        var createFactory = new TestFactory(createContract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            async (_, _) =>
            {
                createStarted.TrySetResult(true);
                await releaseCreate.Task;
                return (IVisionAlgorithm)lateCreate;
            });
        await using (var createService = Service(createFactory, maximumConcurrentPreparations: 1))
        {
            var pending = createService.PrepareAsync(Request(createContract, TimeSpan.FromMilliseconds(100))).AsTask();
            await createStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Succeeded);
            Assert.Equal("AlgorithmPreparationTimedOut", result.ReasonCode);
            Assert.Null(result.Prepared);
            Assert.Equal(1, createService.PendingPreparationCount);
            Assert.Equal(0, createService.OwnedInstanceCount);

            releaseCreate.TrySetResult(true);
            await EventuallyAsync(() => lateCreate.DisposeCalls == 1);
            await EventuallyAsync(() => createService.PendingPreparationCount == 0);
            Assert.Equal(0, createService.OwnedInstanceCount);
        }

        var warmContract = CreateContract("R07W");
        var warmStarted = NewSignal();
        var releaseWarm = NewSignal();
        var lateWarm = new TestAlgorithm(warm: async _ =>
        {
            warmStarted.TrySetResult(true);
            await releaseWarm.Task;
        });
        var warmFactory = new TestFactory(warmContract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(lateWarm));
        await using (var warmService = Service(warmFactory, maximumConcurrentPreparations: 1))
        {
            var pending = warmService.PrepareAsync(Request(warmContract, TimeSpan.FromMilliseconds(100))).AsTask();
            await warmStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Succeeded);
            Assert.Equal("AlgorithmPreparationTimedOut", result.ReasonCode);
            Assert.Null(result.Prepared);
            Assert.Equal(1, warmService.OwnedInstanceCount);

            releaseWarm.TrySetResult(true);
            await EventuallyAsync(() => lateWarm.DisposeCalls == 1);
            await EventuallyAsync(() => warmService.PendingPreparationCount == 0);
            Assert.Equal(0, warmService.OwnedInstanceCount);
        }
    }

    [Fact]
    public async Task V110_R08_FourHungFactoriesRetainAllSlotsAndFifthCannotCreate()
    {
        var releases = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var started = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var algorithms = Enumerable.Range(0, 4).Select(_ => new TestAlgorithm()).ToArray();
        var contracts = Enumerable.Range(0, 5).Select(index => CreateContract("R08" + index)).ToArray();
        var factories = new List<TestFactory>();
        for (var index = 0; index < 4; index++)
        {
            var current = index;
            factories.Add(new TestFactory(contracts[current].Descriptor, (_, _) =>
                Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
                async (_, _) =>
                {
                    started[current].TrySetResult(true);
                    await releases[current].Task;
                    return (IVisionAlgorithm)algorithms[current];
                }));
        }

        var fifth = new TestAlgorithm();
        factories.Add(new TestFactory(contracts[4].Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(fifth)));
        await using var service = Service(factories.ToArray(), maximumConcurrentPreparations: 4);

        var firstFour = Enumerable.Range(0, 4).Select(index =>
            service.PrepareAsync(Request(contracts[index], TimeSpan.FromMilliseconds(120))).AsTask()).ToArray();
        try
        {
            await Task.WhenAll(started.Select(signal => signal.Task.WaitAsync(TimeSpan.FromSeconds(2))));
            // Observe all four logical timeouts while their actual factories
            // remain blocked. A separate wait's timeout is not proof that
            // these four requests have reached their own deadlines.
            var firstResults = await Task.WhenAll(firstFour).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(firstResults, result =>
            {
                Assert.False(result.Succeeded);
                Assert.Equal("AlgorithmPreparationTimedOut", result.ReasonCode);
                Assert.Null(result.Prepared);
            });
            Assert.Equal(4, service.PendingPreparationCount);
            var fifthResult = await service.PrepareAsync(Request(contracts[4], TimeSpan.FromMilliseconds(120)));
            Assert.False(fifthResult.Succeeded);
            Assert.Equal("AlgorithmPreparationTimedOut", fifthResult.ReasonCode);
            Assert.Equal(0, factories[4].CreateCalls);
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult(true);
        }
        foreach (var algorithm in algorithms)
            await EventuallyAsync(() => algorithm.DisposeCalls == 1);
        await EventuallyAsync(() => service.PendingPreparationCount == 0);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Fact]
    public async Task V110_R09_CancellationCallbackDoesNotBlockCallerAndRetainsPhysicalSlot()
    {
        var contract = CreateContract("R09");
        var otherContract = CreateContract("R09B");
        var validationStarted = NewSignal();
        var cancellationStarted = NewSignal();
        var releaseCancellation = NewSignal();
        var releaseValidation = NewSignal();
        var factory = new TestFactory(contract.Descriptor,
            async (_, cancellationToken) =>
            {
                validationStarted.TrySetResult(true);
                cancellationToken.Register(() =>
                {
                    cancellationStarted.TrySetResult(true);
                    releaseCancellation.Task.GetAwaiter().GetResult();
                });
                await releaseValidation.Task;
                return Array.Empty<AlgorithmValidationIssue>();
            },
            (_, _) => Task.FromResult<IVisionAlgorithm>(new TestAlgorithm()));
        var otherFactory = new TestFactory(otherContract.Descriptor);
        await using var service = Service(new[] { factory, otherFactory },
            maximumConcurrentPreparations: 1);
        using var cancellation = new CancellationTokenSource();
        var pending = service.PrepareAsync(Request(contract, TimeSpan.FromSeconds(2)), cancellation.Token).AsTask();
        await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();
        Assert.False(result.Succeeded);
        Assert.Equal("AlgorithmPreparationCancelled", result.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
        Assert.Equal(1, service.PendingPreparationCount);

        var blockedSlotResult = await service.PrepareAsync(
            Request(otherContract, TimeSpan.FromMilliseconds(100)));
        Assert.False(blockedSlotResult.Succeeded);
        Assert.Equal("AlgorithmPreparationTimedOut", blockedSlotResult.ReasonCode);
        Assert.Equal(0, otherFactory.CreateCalls);

        releaseCancellation.TrySetResult(true);
        releaseValidation.TrySetResult(true);
        await EventuallyAsync(() => service.PendingPreparationCount == 0);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Fact]
    public async Task V110_R10_UncooperativePublishedDisposeRetainsOwnershipUntilActualReturn()
    {
        var contract = CreateContract("R10");
        var disposeStarted = NewSignal();
        var releaseDispose = NewSignal();
        var algorithm = new TestAlgorithm(dispose: async () =>
        {
            disposeStarted.TrySetResult(true);
            await releaseDispose.Task;
        });
        var factory = new TestFactory(contract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(algorithm));
        var service = Service(factory, shutdownWaitTimeout: TimeSpan.FromMilliseconds(100));
        var result = await service.PrepareAsync(Request(contract));
        Assert.True(result.Succeeded, result.ReasonCode);
        var prepared = result.Prepared!;
        var disposal = prepared.DisposeAsync().AsTask();
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = service.DisposeAsync().AsTask();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);
        Assert.Equal(1, service.OwnedInstanceCount);
        Assert.Equal(1, algorithm.DisposeCalls);

        releaseDispose.TrySetResult(true);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => service.OwnedInstanceCount == 0);
        Assert.Equal(1, algorithm.DisposeCalls);
    }

    [Fact]
    public async Task V110_R11_PreCancelledRequestDoesNotEnterFactory()
    {
        var contract = CreateContract("R11");
        var factory = new TestFactory(contract.Descriptor);
        await using var service = Service(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.PrepareAsync(Request(contract), cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("AlgorithmPreparationCancelled", result.ReasonCode);
        Assert.Null(result.Prepared);
        Assert.Equal(0, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, service.PendingPreparationCount);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Fact]
    public async Task V110_R12_ReusingDisposedInstanceIsRejectedWithoutSecondWarmOrDispose()
    {
        var contract = CreateContract("R12");
        var algorithm = new TestAlgorithm();
        var factory = new TestFactory(contract.Descriptor, (_, _) =>
            Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>()),
            (_, _) => Task.FromResult<IVisionAlgorithm>(algorithm));
        await using var service = Service(factory);

        var first = await service.PrepareAsync(Request(contract));
        Assert.True(first.Succeeded, first.ReasonCode);
        await first.Prepared!.DisposeAsync();
        Assert.True(first.Prepared.IsRetired);
        Assert.Equal(1, algorithm.WarmCalls);
        Assert.Equal(1, algorithm.DisposeCalls);
        Assert.Equal(0, service.OwnedInstanceCount);

        var second = await service.PrepareAsync(Request(contract));

        Assert.False(second.Succeeded);
        Assert.Equal("AlgorithmInstanceRetired", second.ReasonCode);
        Assert.Null(second.Prepared);
        Assert.Equal(2, factory.ValidateCalls);
        Assert.Equal(2, factory.CreateCalls);
        Assert.Equal(1, algorithm.WarmCalls);
        Assert.Equal(1, algorithm.DisposeCalls);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("create")]
    [InlineData("warm")]
    public async Task V135_PR01_LogicalCancellationRetainsTheExactPreparationUntilActualCleanup(string blockedStage)
    {
        var contract = CreateContract("ManualOwned" + blockedStage);
        var entered = NewSignal();
        var release = NewSignal();
        var aborted = 0;
        async Task BlockAsync(string stage)
        {
            if (stage != blockedStage) return;
            entered.TrySetResult(true);
            await release.Task;
        }
        var algorithm = new TestAlgorithm(warm: _ => BlockAsync("warm"));
        var factory = new TestFactory(contract.Descriptor, async (_, _) =>
        {
            await BlockAsync("validate");
            return Array.Empty<AlgorithmValidationIssue>();
        }, async (_, _) => { await BlockAsync("create"); return algorithm; });
        await using var service = Service(factory);
        using var cancellation = new CancellationTokenSource();
        Task? retirement = null;
        var pending = service.PrepareOwnedAsync(Request(contract), cancellation.Token, () =>
        {
            if (Volatile.Read(ref aborted) != 0) throw new OperationCanceledException();
            return new PreparationPhaseLease();
        }, work => retirement = work).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref aborted, 1);
            cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(result.Succeeded);
            Assert.Null(result.Prepared);
            Assert.NotNull(retirement);
            Assert.False(retirement!.IsCompleted);
            Assert.Equal(1, service.PendingPreparationCount);
        }
        finally { release.TrySetResult(true); }
        await retirement!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(blockedStage == "validate" ? 0 : 1, factory.CreateCalls);
        Assert.Equal(blockedStage == "warm" ? 1 : 0, algorithm.WarmCalls);
        Assert.Equal(blockedStage == "validate" ? 0 : 1, algorithm.DisposeCalls);
        Assert.Equal(0, service.OwnedInstanceCount);
    }

    [Fact]
    public async Task V135_PR02_UnpublishedDisposalFailureIsVisibleToTheOwningAttempt()
    {
        var contract = CreateContract("ManualRetirementFailure");
        var entered = NewSignal();
        var release = NewSignal();
        var algorithm = new TestAlgorithm(warm: async _ =>
        { entered.TrySetResult(true); await release.Task; },
            dispose: () => throw new InvalidOperationException("test disposal failure"));
        var factory = new TestFactory(contract.Descriptor, create: (_, _) => Task.FromResult<IVisionAlgorithm>(algorithm));
        await using var service = Service(factory);
        using var cancellation = new CancellationTokenSource();
        Task? retirement = null;
        var pending = service.PrepareOwnedAsync(Request(contract), cancellation.Token,
            () => new PreparationPhaseLease(), work => retirement = work).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Succeeded);
            Assert.False(retirement!.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => retirement!.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("AlgorithmUnpublishedRetirementFailed", error.Message);
        Assert.Equal(1, service.OwnedInstanceCount);
    }

    private sealed class PreparationPhaseLease : IDisposable
    {
        public void Dispose() { }
    }

    private static AlgorithmPreparationService Service(TestFactory factory,
        int maximumConcurrentPreparations = 4, TimeSpan? shutdownWaitTimeout = null) =>
        new(new[] { factory }, new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5),
            maximumConcurrentPreparations, maximumOwnedInstances: 16, shutdownWaitTimeout: shutdownWaitTimeout));

    private static AlgorithmPreparationService Service(params TestFactory[] factories) =>
        new(factories, new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));

    private static AlgorithmPreparationService Service(TestFactory[] factories,
        int maximumConcurrentPreparations, TimeSpan? shutdownWaitTimeout = null) =>
        new(factories, new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5),
            maximumConcurrentPreparations, maximumOwnedInstances: 16, shutdownWaitTimeout: shutdownWaitTimeout));

    private static AlgorithmPreparationRequest Request(Contract contract, TimeSpan? timeout = null)
    {
        var result = contract.Descriptor.ResultSchema;
        var overlay = result.OverlayContract;
        return new(contract.Descriptor.Identity, contract.Configuration, result.Id, result.Version,
            result.ContentHash, overlay.Id, overlay.Version, overlay.ContentHash,
            timeout ?? TimeSpan.FromSeconds(2));
    }

    private static Contract CreateContract(string suffix)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Config" + suffix, "1",
            new[] { new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "px", required: true) });
        var overlay = new OverlayContract("Overlay" + suffix, "1");
        var resultSchema = new AlgorithmResultSchema("Result" + suffix, "1",
            Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoIssue" }, overlay);
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("Algorithm" + suffix, "1"),
            configurationSchema, resultSchema);
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            new[] { new AlgorithmConfigurationEntry("Threshold", "px", AlgorithmScalarValue.FromInt64(1)) });
        return new Contract(descriptor, configuration);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        Assert.True(predicate(), "The expected asynchronous cleanup did not complete.");
    }

    private sealed record Contract(AlgorithmDescriptor Descriptor, AlgorithmConfigurationSnapshot Configuration);

    private sealed class TestFactory : IVisionAlgorithmFactory
    {
        private readonly Func<AlgorithmConfigurationSnapshot, CancellationToken, Task<IReadOnlyList<AlgorithmValidationIssue>>> _validate;
        private readonly Func<AlgorithmConfigurationSnapshot, CancellationToken, Task<IVisionAlgorithm>> _create;
        private int _active;
        private int _maximumConcurrent;
        private int _validateCalls;
        private int _createCalls;

        public TestFactory(AlgorithmDescriptor descriptor,
            Func<AlgorithmConfigurationSnapshot, CancellationToken, Task<IReadOnlyList<AlgorithmValidationIssue>>>? validate = null,
            Func<AlgorithmConfigurationSnapshot, CancellationToken, Task<IVisionAlgorithm>>? create = null)
        {
            Descriptor = descriptor;
            _validate = validate ?? ((_, _) => Task.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>()));
            _create = create ?? ((_, _) => Task.FromResult<IVisionAlgorithm>(new TestAlgorithm()));
        }

        public AlgorithmDescriptor Descriptor { get; }
        public int ValidateCalls => Volatile.Read(ref _validateCalls);
        public int CreateCalls => Volatile.Read(ref _createCalls);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrent);

        public async ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            Enter();
            Interlocked.Increment(ref _validateCalls);
            try { return await _validate(configuration, cancellationToken); }
            finally { Exit(); }
        }

        public async ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            Enter();
            Interlocked.Increment(ref _createCalls);
            try { return await _create(configuration, cancellationToken); }
            finally { Exit(); }
        }

        private void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrent);
                if (active <= maximum || Interlocked.CompareExchange(ref _maximumConcurrent, active, maximum) == maximum)
                    break;
            }
        }

        private void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class TestAlgorithm : IVisionAlgorithm
    {
        private readonly Func<CancellationToken, Task>? _warm;
        private readonly Func<Task>? _dispose;
        private int _warmCalls;
        private int _disposeCalls;

        public TestAlgorithm(Func<CancellationToken, Task>? warm = null, Func<Task>? dispose = null)
        { _warm = warm; _dispose = dispose; }

        public int WarmCalls => Volatile.Read(ref _warmCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _warmCalls);
            if (_warm is not null) await _warm(cancellationToken);
        }

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Pass, null,
                Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet("Overlay", "1")));

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (_dispose is not null) await _dispose();
        }
    }
}
