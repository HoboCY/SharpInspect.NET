using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeActivationStartupBoundaryTests
{
    [Fact]
    public void V132_S03_OperationIdentityCannotLoseTheCorrelationRequiredForRestartAudit()
    {
        var error = Assert.Throws<ArgumentException>(() => new ActivateRecipeCommand(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole), new("fixture", "1", new string('A', 64)), Guid.NewGuid(),
            new string('B', 64), null, null, "Verify durable operation binding", Guid.NewGuid()));
        Assert.StartsWith("RecipeActivationOperationCorrelationMismatch", error.Message);
    }

    [Fact]
    public async Task V132_S01_StartupFenceExistsBeforeActivationServiceIsResolved()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            productionStoreOptions: new ProductionStoreOptions { RecipeActivations = new() });
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        Assert.Contains("RecipeActivationStartupRecoveryPending", snapshot.AdmissionBlockers);
        using var activation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(activation.Available);
        Assert.Equal("RecipeActivationStartupRecoveryPending", activation.Failure);
        using var plc = await runtime.EnterPlcResultContractChangeAsync(CancellationToken.None);
        Assert.Equal("RecipeActivationInProgress", plc.GetBlocker());
    }

    [Fact]
    public async Task V132_S02_PublicReplacementCannotGrantStartupRecoveryAuthority()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            productionStoreOptions: new ProductionStoreOptions { RecipeActivations = new() });
        var replacement = new UntrustedActivationService();
        runtime.ConfigureRecipeActivationService(replacement);
        await runtime.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, replacement.Calls);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.Contains("RecipeActivationStartupRecoveryRequired", snapshot.AdmissionBlockers);
        Assert.DoesNotContain("RecipeActivationStartupRecoveryPending", snapshot.AdmissionBlockers);
        Assert.False(snapshot.Ready);
        Assert.Null(snapshot.ActiveRecipe);
        using var activation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal("RecipeActivationStartupRecoveryRequired", activation.Failure);
        using var plc = await runtime.EnterPlcResultContractChangeAsync(CancellationToken.None);
        Assert.Equal("RecipeActivationInProgress", plc.GetBlocker());
    }

    private sealed class UntrustedActivationService : IRecipeActivationService
    {
        internal int Calls { get; private set; }
        public ValueTask<RecipeActivationAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) { Calls++; return ValueTask.FromResult(new RecipeActivationAccess(true, "Untrusted")); }
        public ValueTask<RecipeActivationReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new RecipeActivationReadResult(true, "Untrusted")); }
        public ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
            CancellationToken cancellationToken = default) => ReadCurrentAsync(cancellationToken);
        public ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
            CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new RecipeActivationPage(true, "Untrusted", Array.Empty<RecipeActivationRecord>(), 0, null)); }
        public ValueTask<RecipeActivationResult> ActivateAsync(ActivateRecipeCommand command,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("UntrustedActivationInvoked");
    }
}
