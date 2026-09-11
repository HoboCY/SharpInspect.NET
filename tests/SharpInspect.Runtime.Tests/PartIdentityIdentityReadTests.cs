using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PartIdentityIdentityReadTests
{
    [Fact]
    public async Task V143_S10_IdentityOnlySchema29ReadsBeyondDefaultAuditPage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect-V143-S10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var policy = new AuditIntegrityPolicy("V143S10", "1", "V143S10-" + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 1000,
            BackgroundVerificationEntries = 1000
        };
        var options = new ProductionStoreOptions(Path.Combine(directory, "identity.sqlite"))
        {
            AuditIntegrityPolicy = policy,
            LocalIdentity = new LocalIdentityOptions(policy.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("V143S10", "1", new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
            PartIdentities = new PartIdentityStoreOptions(),
            CommitTimeout = TimeSpan.FromSeconds(5),
            QueryTimeout = TimeSpan.FromSeconds(5)
        };
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        Assert.Null(options.ProductionInspections);
        Assert.Null(options.AlarmPolicy);
        Assert.Null(options.RecipeDrafts);
        for (var index = 0; index < 210; index++)
        {
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), DateTimeOffset.UtcNow, AuditedCommandKind.GracefulProductionStop,
                CommandSource.PhysicalConsole, null, null, null, CommandAuditPhase.Outcome,
                CommandDisposition.Rejected, "PhysicalSessionRequired");
            var written = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(5)));
            Assert.True(written.Committed, written.ReasonCode);
        }

        // Each API must verify the complete schema29 snapshot, even when no
        // other optional ledger selects the full verification request.
        var identity = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(policy.StationId, identity.StationId);
        Assert.Null(await store.ReadRecoveryOperationAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
