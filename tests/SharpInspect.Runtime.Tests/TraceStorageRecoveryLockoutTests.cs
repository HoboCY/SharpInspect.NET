using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStorageRecoveryLockoutTests
{
    [Fact, Trait("VerificationId", "V155_R10")]
    public async Task V155_R10_FinalPasswordFailurePersistsCredentialLockoutAboveOrdinaryWalAcrossRestart()
    {
        long wal = 0;
        await using var fixture = await TraceCheckpointTests.CreateFixtureAsync(readWalLength: _ => Volatile.Read(ref wal));
        var publication = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0, TraceCheckpointTests.Policy()));
        Assert.True(publication.Succeeded, publication.Outcome.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var identity = new LocalIdentityService(fixture.Store, fixture.IdentityOptions, utcNow: () => now);
        var policy = fixture.IdentityOptions.AuthenticationPolicy;
        for (var failure = 1; failure < policy.AccountFailureLimit; failure++)
        {
            var result = await identity.AuthenticateAsync(new(fixture.UserName, "wrong controlled fixture password"));
            Assert.False(result.Succeeded);
            Assert.Equal("AuthenticationRejected", result.ReasonCode);
            await fixture.WaitForVerifiedAsync();
            now += policy.MaximumDelay + TimeSpan.FromSeconds(1);
        }
        var before = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        Assert.True(before.Administrator!.Enabled);
        Assert.Equal(policy.AccountFailureLimit - 1, before.Administrator.Throttle.ConsecutiveFailures);
        var sequence = await fixture.ScalarAsync("SELECT MAX(Sequence) FROM audit_entries;");
        Volatile.Write(ref wal, 65L << 20);
        var last = await identity.AuthenticateAsync(new(fixture.UserName, "wrong controlled fixture password"));
        Assert.False(last.Succeeded);
        Assert.Equal("AuthenticationRejected", last.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        using (var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            var payloads = AuditChainDatabase.Read(connection.Handle!,
                "SELECT Payload FROM audit_entries WHERE Kind='IdentityEvent' AND Sequence>? ORDER BY Sequence;",
                new StoreDeadline(TimeSpan.FromSeconds(10)), row => Encoding.UTF8.GetString(
                    Convert.FromBase64String(SqliteNative.ColumnText(row, 0)!)),
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(2, payloads.Count);
            Assert.Contains("CredentialDisabled", payloads[0]);
            Assert.Contains("CredentialFailureLimitReached", payloads[0]);
            Assert.Contains("AuthenticationRejected", payloads[1]);
        }
        await fixture.Store.DisposeAsync();
        await using var restarted = new SqliteCommandStore(fixture.Options, _ => Volatile.Read(ref wal));
        var initialized = await restarted.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(restarted);
        var cold = await restarted.ReadIdentityAsync(CancellationToken.None);
        Assert.False(cold.Administrator!.Enabled);
        Assert.Equal(policy.AccountFailureLimit, cold.Administrator.Throttle.ConsecutiveFailures);
        Assert.NotNull(cold.Administrator.DisabledAtUtc);
        now += policy.MaximumDelay + TimeSpan.FromSeconds(1);
        var coldIdentity = new LocalIdentityService(restarted, fixture.IdentityOptions, utcNow: () => now);
        var correct = await coldIdentity.AuthenticateAsync(new(fixture.UserName, fixture.Password));
        Assert.False(correct.Succeeded);
        Assert.Equal("AuthenticationRejected", correct.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(restarted);
        Assert.False((await restarted.ReadIdentityAsync(CancellationToken.None)).Administrator!.Enabled);
    }
}
