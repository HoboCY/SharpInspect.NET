using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V105 observable authentication-throttle acceptance coverage.  Each test has its own
/// machine-protected key and SQLite database so restart and rollback assertions exercise the
/// production persistence boundary rather than an in-memory substitute.
/// </summary>
public sealed class AuthenticationThrottleTests
{
    [Fact]
    public async Task V105_S01_KnownFailurePersistsAcrossRestartAndExpiresWithoutChangingVerifier()
    {
        RequireWindows();
        await using var fixture = TestFixture.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();
        var expectedPrincipal = Guid.Empty;
        AuthenticationThrottleStateSnapshot failedThrottle = default;
        PasswordVerifierSnapshot verifier = default;

        await using (var store = await fixture.OpenStoreAsync())
        {
            var identity = fixture.Identity(store, authority, clock);
            expectedPrincipal = await fixture.BootstrapAsync(identity, store);
            await WaitForVerifiedAsync(store);

            var before = await store.ReadIdentityAsync(CancellationToken.None);
            verifier = PasswordVerifierSnapshot.From(before.Administrator!.Password);
            var failed = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
            Assert.False(failed.Succeeded);
            Assert.Equal("AuthenticationRejected", failed.ReasonCode);
            await WaitForVerifiedAsync(store);

            var afterFailure = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.Equal(1, afterFailure.Administrator!.Throttle.ConsecutiveFailures);
            Assert.Equal(1, afterFailure.StationThrottle.ConsecutiveFailures);
            failedThrottle = AuthenticationThrottleStateSnapshot.From(afterFailure);
            Assert.Equal(verifier, PasswordVerifierSnapshot.From(afterFailure.Administrator.Password));

            var eventFields = ReadAuthenticationRejectedFields(fixture.DatabasePath);
            Assert.Contains(fixture.AuthenticationPolicy.Id, eventFields);
            Assert.Contains(fixture.AuthenticationPolicy.Version, eventFields);
            Assert.Contains(fixture.AuthenticationPolicy.ContentHash, eventFields);
            Assert.Equal("1", eventFields[22]);
            Assert.Equal("1", eventFields[23]);
            var protectedAttempt = eventFields[21];
            Assert.NotNull(protectedAttempt);
            Assert.Matches("^[0-9A-F]{64}$", protectedAttempt!);
            Assert.NotEqual(fixture.UserName, protectedAttempt);
            var rawPayloadText = ReadAuditPayloadText(fixture.DatabasePath);
            Assert.DoesNotContain(fixture.UserName, rawPayloadText, StringComparison.Ordinal);
        }

        await using (var restarted = await fixture.OpenStoreAsync())
        {
            var identity = fixture.Identity(restarted, authority, clock);
            var beforeBlockedAttempt = await restarted.ReadIdentityAsync(CancellationToken.None);
            var blocked = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.False(blocked.Succeeded);
            Assert.Equal("AuthenticationRejected", blocked.ReasonCode);
            await WaitForVerifiedAsync(restarted);

            var afterBlockedAttempt = await restarted.ReadIdentityAsync(CancellationToken.None);
            Assert.Equal(failedThrottle.AccountFailures, afterBlockedAttempt.Administrator!.Throttle.ConsecutiveFailures);
            Assert.Equal(failedThrottle.StationFailures, afterBlockedAttempt.StationThrottle.ConsecutiveFailures);
            Assert.Equal(failedThrottle.AccountNextAllowedAtUtc,
                afterBlockedAttempt.Administrator.Throttle.NextAllowedAtUtc);
            Assert.Equal(failedThrottle.StationNextAllowedAtUtc,
                afterBlockedAttempt.StationThrottle.NextAllowedAtUtc);
            Assert.Equal(verifier, PasswordVerifierSnapshot.From(afterBlockedAttempt.Administrator.Password));
            Assert.True(afterBlockedAttempt.Revision > beforeBlockedAttempt.Revision);

            clock.UtcNow = failedThrottle.LatestNextAllowedAtUtc;
            await WaitForVerifiedAsync(restarted);
            var accepted = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.True(accepted.Succeeded, accepted.ReasonCode);
            Assert.Equal(expectedPrincipal, accepted.Identity!.PrincipalId);
            await WaitForVerifiedAsync(restarted);

            var afterSuccess = await restarted.ReadIdentityAsync(CancellationToken.None);
            Assert.Equal(0, afterSuccess.StationThrottle.ConsecutiveFailures);
            Assert.Equal(0, afterSuccess.Administrator!.Throttle.ConsecutiveFailures);
            Assert.Equal(default, afterSuccess.StationThrottle.NextAllowedAtUtc);
            Assert.Equal(default, afterSuccess.Administrator.Throttle.NextAllowedAtUtc);
            Assert.Equal(verifier, PasswordVerifierSnapshot.From(afterSuccess.Administrator.Password));
        }
    }

    [Fact]
    public async Task V105_S02_AccountLimitDisablesCredentialAndRestartCannotUnlockIt()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with
        {
            AccountFailureLimit = 2,
            StationFailureLimit = 5,
            InitialDelay = TimeSpan.FromSeconds(1)
        };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using (var store = await fixture.OpenStoreAsync())
        {
            var identity = fixture.Identity(store, authority, clock);
            var principal = await fixture.BootstrapAsync(identity, store);

            var first = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
            Assert.False(first.Succeeded);
            await WaitForVerifiedAsync(store);
            var afterFirst = await store.ReadIdentityAsync(CancellationToken.None);
            clock.UtcNow = afterFirst.Administrator!.Throttle.NextAllowedAtUtc;
            await WaitForVerifiedAsync(store);

            var second = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
            Assert.False(second.Succeeded);
            Assert.Equal("AuthenticationRejected", second.ReasonCode);
            await WaitForVerifiedAsync(store);
            var disabled = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.NotNull(disabled.Administrator);
            Assert.False(disabled.Administrator!.Enabled);
            Assert.NotNull(disabled.Administrator.DisabledAtUtc);
            Assert.Equal(principal, disabled.Administrator.PrincipalId);
        }

        await using (var restarted = await fixture.OpenStoreAsync())
        {
            var before = await restarted.ReadIdentityAsync(CancellationToken.None);
            var identity = fixture.Identity(restarted, authority, clock);
            clock.UtcNow = clock.UtcNow.AddHours(1);
            var stillRejected = await identity.AuthenticateAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.False(stillRejected.Succeeded);
            Assert.Equal("AuthenticationRejected", stillRejected.ReasonCode);
            await WaitForVerifiedAsync(restarted);

            var after = await restarted.ReadIdentityAsync(CancellationToken.None);
            Assert.NotNull(after.Administrator);
            Assert.False(after.Administrator!.Enabled);
            Assert.Equal(before.Administrator!.PrincipalId, after.Administrator.PrincipalId);
            Assert.Equal(before.Administrator.DisabledAtUtc, after.Administrator.DisabledAtUtc);
        }
    }

    [Fact]
    public async Task V105_S03_UnknownAccountsUseFixedBucketAndStationMaximumDelayDoesNotDisableKnownAdmin()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with
        {
            AccountFailureLimit = 10,
            StationFailureLimit = 2,
            InitialDelay = TimeSpan.FromSeconds(1)
        };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var identity = fixture.Identity(store, authority, clock);
        var principal = await fixture.BootstrapAsync(identity, store);

        var unknown1 = await identity.AuthenticateAsync(new PasswordSignInRequest("wrong-one", "wrong"));
        Assert.False(unknown1.Succeeded);
        Assert.Equal("AuthenticationRejected", unknown1.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterUnknown1 = await store.ReadIdentityAsync(CancellationToken.None);
        clock.UtcNow = LatestNextAllowed(afterUnknown1);
        await WaitForVerifiedAsync(store);

        var unknown2 = await identity.AuthenticateAsync(new PasswordSignInRequest("wrong-two", "wrong"));
        Assert.False(unknown2.Succeeded);
        Assert.Equal("AuthenticationRejected", unknown2.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterUnknown2 = await store.ReadIdentityAsync(CancellationToken.None);

        Assert.Equal(2, afterUnknown2.StationThrottle.ConsecutiveFailures);
        Assert.Equal(policy.MaximumDelay.Ticks, afterUnknown2.StationThrottle.DelayTicks);
        Assert.Equal(2, afterUnknown2.UnknownAccountThrottle.ConsecutiveFailures);
        Assert.Equal(0, afterUnknown2.Administrator!.Throttle.ConsecutiveFailures);
        Assert.Equal(principal, afterUnknown2.Administrator.PrincipalId);
        Assert.True(afterUnknown2.Administrator.Enabled);

        clock.UtcNow = afterUnknown2.StationThrottle.NextAllowedAtUtc;
        await WaitForVerifiedAsync(store);
        var accepted = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, fixture.Password));
        Assert.True(accepted.Succeeded, accepted.ReasonCode);
        Assert.Equal(principal, accepted.Identity!.PrincipalId);
        await WaitForVerifiedAsync(store);

        var afterKnown = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.True(afterKnown.Administrator!.Enabled);
        Assert.Equal(0, afterKnown.StationThrottle.ConsecutiveFailures);
        Assert.Equal(0, afterKnown.Administrator.Throttle.ConsecutiveFailures);
        Assert.Equal(2, afterKnown.UnknownAccountThrottle.ConsecutiveFailures);
    }

    [Fact]
    public async Task V105_S04_IdentityAuthorityAbortRollsBackThrottleAndAuditChain()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with { AccountFailureLimit = 1 };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var identity = fixture.Identity(store, authority, clock);
        await fixture.BootstrapAsync(identity, store);
        await WaitForVerifiedAsync(store);
        var before = await store.ReadIdentityAsync(CancellationToken.None);
        var beforeEntries = CountAuditEntries(fixture.DatabasePath);
        var trigger = "v105_identity_authority_abort_" + Guid.NewGuid().ToString("N");
        Execute(fixture.DatabasePath, $@"
            CREATE TRIGGER {trigger}
            BEFORE UPDATE ON identity_authority
            BEGIN
                SELECT RAISE(ABORT, 'V105IdentityAuthorityAbort');
            END;");

        try
        {
            var failed = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
            Assert.False(failed.Succeeded);
            Assert.NotEqual("Authenticated", failed.ReasonCode);
            await WaitForVerifiedAsync(store);

            var after = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.LastIdentityAuditHash, after.LastIdentityAuditHash);
            Assert.Equal(before.StationThrottle.ConsecutiveFailures, after.StationThrottle.ConsecutiveFailures);
            Assert.Equal(before.Administrator!.Throttle.ConsecutiveFailures,
                after.Administrator!.Throttle.ConsecutiveFailures);
            Assert.True(after.Administrator.Enabled);
            Assert.Equal(beforeEntries, CountAuditEntries(fixture.DatabasePath));
        }
        finally
        {
            Execute(fixture.DatabasePath, $"DROP TRIGGER {trigger};");
        }

        await WaitForVerifiedAsync(store);
        var recovered = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, fixture.Password));
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        await WaitForVerifiedAsync(store);
        Assert.True((await store.ReadIdentityAsync(CancellationToken.None)).Administrator!.Enabled);
    }

    [Fact]
    public async Task V105_S05_LegacyHeaderPreflightRejectsWithoutChangingDatabaseOrKey()
    {
        RequireWindows();
        await using var fixture = TestFixture.Create();
        await using (var initial = await fixture.OpenStoreAsync())
        {
            // Opening is enough to create and seal the schema-4 identity fixture.
        }

        Execute(fixture.DatabasePath, "PRAGMA user_version=3;");
        var databaseHash = FileHash(fixture.DatabasePath);
        var keyPath = WindowsMachineAuditKey.GetKeyPath(fixture.AuditPolicy);
        Assert.True(File.Exists(keyPath));
        var keyHash = FileHash(keyPath);

        await using (var rejected = new SqliteCommandStore(fixture.StoreOptions()))
        {
            var initialization = await rejected.Initialization;
            Assert.False(initialization.Committed);
            Assert.Equal("IdentityAuthenticationGovernedMigrationRequired", initialization.ReasonCode);
            Assert.Equal(AuditIntegrityState.Faulted, rejected.Integrity?.State);
        }

        var queryReport = await new SqliteAuditIntegrityQuery(fixture.StoreOptions())
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, queryReport.State);
        Assert.Equal("IdentityAuthenticationGovernedMigrationRequired", queryReport.ReasonCode);

        Assert.Equal(databaseHash, FileHash(fixture.DatabasePath));
        Assert.Equal(keyHash, FileHash(keyPath));
    }

    [Fact]
    public async Task V105_S06_ConcurrentVerifierUpgradeDoesNotCountAsCredentialFailure()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with { AccountFailureLimit = 1 };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();
        var upgradedHasher = new Pbkdf2PasswordHasher(new PasswordHashBaseline(
            PasswordHashBaseline.DevelopmentVersion, PasswordHashBaseline.SecurityFloorIterations + 1));

        await using (var initial = await fixture.OpenStoreAsync())
        {
            var initialIdentity = fixture.Identity(initial, authority, clock);
            await fixture.BootstrapAsync(initialIdentity, initial);
        }

        var upgradedOptions = fixture.IdentityOptions(upgradedHasher);
        await using var store = await fixture.OpenStoreAsync(upgradedOptions);
        using var firstObserved = new ManualResetEventSlim();
        using var secondObserved = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        void ObserveFirst(PasswordVerificationWork work)
        {
            if (work.Cost != PasswordHashBaseline.SecurityFloorIterations) return;
            firstObserved.Set();
            if (!secondObserved.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The concurrent second verifier did not observe the old record.");
        }

        void ObserveSecond(PasswordVerificationWork work)
        {
            if (work.Cost != PasswordHashBaseline.SecurityFloorIterations) return;
            secondObserved.Set();
            if (!releaseSecond.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The concurrent second verifier was not released.");
        }

        var firstIdentity = fixture.Identity(store, authority, clock, upgradedOptions, ObserveFirst);
        var secondIdentity = fixture.Identity(store, authority, clock, upgradedOptions, ObserveSecond);
        await WaitForVerifiedAsync(store);

        Task<AuthenticationResult>? first = null;
        Task<AuthenticationResult>? second = null;
        try
        {
            // Start the first service only after its observer has a deterministic opportunity
            // to wait for the second service. The second observer then holds its old snapshot
            // until the first upgrade is committed and the integrity monitor is Verified.
            first = firstIdentity.AuthenticateAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password)).AsTask();
            Assert.True(firstObserved.Wait(TimeSpan.FromSeconds(5)));
            second = secondIdentity.AuthenticateAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password)).AsTask();
            Assert.True(secondObserved.Wait(TimeSpan.FromSeconds(5)));

            var firstResult = await first;
            Assert.True(firstResult.Succeeded, firstResult.ReasonCode);
            await WaitForVerifiedAsync(store);

            releaseSecond.Set();
            var secondResult = await second;
            Assert.False(secondResult.Succeeded);
            Assert.Equal("AuthenticationRejected", secondResult.ReasonCode);
            await WaitForVerifiedAsync(store);

            var state = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.True(state.Administrator!.Enabled);
            Assert.Equal(0, state.Administrator.Throttle.ConsecutiveFailures);
            Assert.Equal(PasswordHashBaseline.SecurityFloorIterations + 1, state.Administrator.Password.Cost);

            await WaitForVerifiedAsync(store);
            var retry = await firstIdentity.AuthenticateAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.True(retry.Succeeded, retry.ReasonCode);
            await WaitForVerifiedAsync(store);
            var afterRetry = await store.ReadIdentityAsync(CancellationToken.None);
            Assert.True(afterRetry.Administrator!.Enabled);
            Assert.Equal(0, afterRetry.Administrator.Throttle.ConsecutiveFailures);
        }
        finally
        {
            // Do not leave a verifier callback blocked if an assertion fails.
            secondObserved.Set();
            releaseSecond.Set();
            if (first is not null)
            {
                try { await first.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (second is not null)
            {
                try { await second.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
    }

    [Fact]
    public async Task V105_S07_CoolingBoundaryCannotTurnAStaleReadIntoAnotherFailure()
    {
        RequireWindows();
        await using var fixture = TestFixture.Create();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();

        await using var store = await fixture.OpenStoreAsync();
        var identity = fixture.Identity(store, authority, clock);
        await fixture.BootstrapAsync(identity, store);

        var failed = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
        Assert.False(failed.Succeeded);
        Assert.Equal("AuthenticationRejected", failed.ReasonCode);
        await WaitForVerifiedAsync(store);
        var afterFailure = await store.ReadIdentityAsync(CancellationToken.None);
        var nextAllowed = afterFailure.Administrator!.Throttle.NextAllowedAtUtc;
        var beforeBoundary = AuthenticationThrottleSnapshot.From(afterFailure);
        var boundaryClock = new BoundaryClock(nextAllowed - TimeSpan.FromTicks(1),
            nextAllowed + TimeSpan.FromTicks(1));
        var boundaryIdentity = fixture.Identity(store, authority, boundaryClock);

        await WaitForVerifiedAsync(store);
        var rejected = await boundaryIdentity.AuthenticateAsync(
            new PasswordSignInRequest(fixture.UserName, fixture.Password));
        Assert.False(rejected.Succeeded);
        Assert.Equal("AuthenticationRejected", rejected.ReasonCode);
        await WaitForVerifiedAsync(store);

        var afterBoundary = await store.ReadIdentityAsync(CancellationToken.None);
        var afterSnapshot = AuthenticationThrottleSnapshot.From(afterBoundary);
        Assert.Equal(beforeBoundary, afterSnapshot);
        Assert.True(afterBoundary.Administrator!.Enabled);
    }

    [Fact]
    public async Task V105_S08_KnownUnknownAndDisabledAttemptsUseEquivalentUpgradeDummyWork()
    {
        RequireWindows();
        var policy = AuthenticationPolicy.Development with { AccountFailureLimit = 1 };
        await using var fixture = TestFixture.Create(policy);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var authority = new FakeConsoleAuthority();
        var expectedCost = 0;
        var expectedSaltBytes = 0;
        var expectedOutputBytes = 0;

        await using (var initial = await fixture.OpenStoreAsync())
        {
            var initialIdentity = fixture.Identity(initial, authority, clock);
            await fixture.BootstrapAsync(initialIdentity, initial);
            var state = await initial.ReadIdentityAsync(CancellationToken.None);
            expectedCost = state.Administrator!.Password.Cost;
            expectedSaltBytes = Convert.FromBase64String(state.Administrator.Password.Salt).Length;
            expectedOutputBytes = Convert.FromBase64String(state.Administrator.Password.Derived).Length;
        }

        var upgradedHasher = new Pbkdf2PasswordHasher(new PasswordHashBaseline(
            PasswordHashBaseline.DevelopmentVersion, expectedCost + 1));
        var upgradedOptions = fixture.IdentityOptions(upgradedHasher);
        await using var store = await fixture.OpenStoreAsync(upgradedOptions);
        var profiles = new VerificationProfileCollector();
        var identity = fixture.Identity(store, authority, clock, upgradedOptions, profiles.Add);
        var expectedProfile = new[]
        {
            new VerificationProfile(expectedCost, expectedSaltBytes, expectedOutputBytes),
            new VerificationProfile(expectedCost + 1, expectedSaltBytes, expectedOutputBytes)
        };

        profiles.Clear();
        var knownWrong = await identity.AuthenticateAsync(new PasswordSignInRequest(fixture.UserName, "wrong"));
        Assert.False(knownWrong.Succeeded);
        Assert.Equal("AuthenticationRejected", knownWrong.ReasonCode);
        Assert.Equal(expectedProfile, profiles.Snapshot());
        await WaitForVerifiedAsync(store);
        var afterKnownWrong = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.False(afterKnownWrong.Administrator!.Enabled);

        clock.UtcNow = LatestNextAllowed(afterKnownWrong);
        await WaitForVerifiedAsync(store);
        profiles.Clear();
        var unknown = await identity.AuthenticateAsync(new PasswordSignInRequest("unknown-throttle", "wrong"));
        Assert.False(unknown.Succeeded);
        Assert.Equal("AuthenticationRejected", unknown.ReasonCode);
        Assert.Equal(expectedProfile, profiles.Snapshot());
        await WaitForVerifiedAsync(store);
        var afterUnknown = await store.ReadIdentityAsync(CancellationToken.None);

        clock.UtcNow = LatestNextAllowed(afterUnknown);
        await WaitForVerifiedAsync(store);
        profiles.Clear();
        var disabled = await identity.AuthenticateAsync(
            new PasswordSignInRequest(fixture.UserName, fixture.Password));
        Assert.False(disabled.Succeeded);
        Assert.Equal("AuthenticationRejected", disabled.ReasonCode);
        Assert.Equal(expectedProfile, profiles.Snapshot());
        await WaitForVerifiedAsync(store);

        var final = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.False(final.Administrator!.Enabled);
        Assert.Equal(1, final.Administrator.Throttle.ConsecutiveFailures);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Local identity acceptance requires Windows DPAPI machine protection.");
    }

    private static DateTimeOffset LatestNextAllowed(IdentityAuthorityState state) =>
        state.StationThrottle.NextAllowedAtUtc > state.UnknownAccountThrottle.NextAllowedAtUtc
            ? state.StationThrottle.NextAllowedAtUtc : state.UnknownAccountThrottle.NextAllowedAtUtc;

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < deadline)
        {
            var report = store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException($"Audit integrity faulted: {report.ReasonCode}");
            await Task.Delay(20);
        }

        throw new XunitException($"Audit integrity did not become Verified. Last state: {store.Integrity?.State}, " +
            $"reason: {store.Integrity?.ReasonCode}");
    }

    private static IReadOnlyList<string?> ReadAuthenticationRejectedFields(string path)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM audit_entries ORDER BY Sequence;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var payload = Convert.FromBase64String(reader.GetString(0));
            var decoded = DecodeCanonical(payload);
            if (decoded.Kind == "IdentityEvent" && decoded.Fields.Count > 2 &&
                decoded.Fields[2] == IdentityEventKind.AuthenticationRejected.ToString())
                return decoded.Fields;
        }

        throw new XunitException("The authentication rejection event was not found in audit payloads.");
    }

    private static string ReadAuditPayloadText(string path)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM audit_entries ORDER BY Sequence;";
        using var reader = command.ExecuteReader();
        var builder = new StringBuilder();
        while (reader.Read()) builder.Append(Encoding.UTF8.GetString(Convert.FromBase64String(reader.GetString(0))));
        return builder.ToString();
    }

    private static (string Kind, IReadOnlyList<string?> Fields) DecodeCanonical(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        Span<byte> integer = stackalloc byte[4];
        ReadExactly(stream, integer);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(integer));
        var kind = ReadCanonicalValue(stream);
        Assert.NotNull(kind);
        ReadExactly(stream, integer);
        var count = BinaryPrimitives.ReadInt32BigEndian(integer);
        Assert.InRange(count, 1, 64);
        var fields = new string?[count];
        for (var index = 0; index < count; index++) fields[index] = ReadCanonicalValue(stream);
        Assert.Equal(stream.Length, stream.Position);
        return (kind!, fields);
    }

    private static string? ReadCanonicalValue(Stream stream)
    {
        var marker = stream.ReadByte();
        Assert.True(marker is 0 or 1);
        if (marker == 0) return null;
        Span<byte> integer = stackalloc byte[4];
        ReadExactly(stream, integer);
        var length = BinaryPrimitives.ReadInt32BigEndian(integer);
        Assert.InRange(length, 0, 4096);
        var bytes = new byte[length];
        ReadExactly(stream, bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            Assert.True(read > 0);
            offset += read;
        }
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path, readOnly: false);
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountAuditEntries(string path)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_entries;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private readonly record struct AuthenticationThrottleStateSnapshot(int AccountFailures, int StationFailures,
        DateTimeOffset AccountNextAllowedAtUtc, DateTimeOffset StationNextAllowedAtUtc,
        DateTimeOffset LatestNextAllowedAtUtc)
    {
        public static AuthenticationThrottleStateSnapshot From(IdentityAuthorityState state)
        {
            var account = state.Administrator!.Throttle;
            var latest = account.NextAllowedAtUtc > state.StationThrottle.NextAllowedAtUtc
                ? account.NextAllowedAtUtc : state.StationThrottle.NextAllowedAtUtc;
            return new(account.ConsecutiveFailures, state.StationThrottle.ConsecutiveFailures,
                account.NextAllowedAtUtc, state.StationThrottle.NextAllowedAtUtc, latest);
        }
    }

    private readonly record struct PasswordVerifierSnapshot(string Algorithm, int FormatVersion, int ParameterVersion,
        int Cost, string Salt, string Derived)
    {
        public static PasswordVerifierSnapshot From(PasswordVerifierState state) =>
            new(state.Algorithm, state.FormatVersion, state.ParameterVersion, state.Cost, state.Salt, state.Derived);
    }

    private readonly record struct VerificationProfile(int Cost, int SaltBytes, int OutputBytes);

    private sealed class VerificationProfileCollector
    {
        private readonly object _gate = new();
        private readonly List<VerificationProfile> _profiles = new();

        public void Add(PasswordVerificationWork work)
        {
            lock (_gate) _profiles.Add(new VerificationProfile(work.Cost, work.SaltBytes, work.OutputBytes));
        }

        public void Clear()
        {
            lock (_gate) _profiles.Clear();
        }

        public VerificationProfile[] Snapshot()
        {
            lock (_gate) return _profiles.ToArray();
        }
    }

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V105-TEST");
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class BoundaryClock
    {
        private readonly DateTimeOffset _before;
        private readonly DateTimeOffset _after;
        private int _calls;

        public BoundaryClock(DateTimeOffset before, DateTimeOffset after)
        {
            _before = before;
            _after = after;
        }

        public DateTimeOffset Next() => Interlocked.Increment(ref _calls) == 1 ? _before : _after;
    }

    private readonly record struct AuthenticationThrottleSnapshot(int AccountFailures, int StationFailures,
        DateTimeOffset AccountNextAllowedAtUtc, DateTimeOffset StationNextAllowedAtUtc, bool Enabled)
    {
        public static AuthenticationThrottleSnapshot From(IdentityAuthorityState state) => new(
            state.Administrator!.Throttle.ConsecutiveFailures,
            state.StationThrottle.ConsecutiveFailures,
            state.Administrator.Throttle.NextAllowedAtUtc,
            state.StationThrottle.NextAllowedAtUtc,
            state.Administrator.Enabled);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(string directoryPath, AuditIntegrityPolicy auditPolicy,
            AuthenticationPolicy authenticationPolicy)
        {
            DirectoryPath = directoryPath;
            DatabasePath = Path.Combine(directoryPath, "identity.sqlite");
            AuditPolicy = auditPolicy;
            AuthenticationPolicy = authenticationPolicy;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public AuditIntegrityPolicy AuditPolicy { get; }
        public AuthenticationPolicy AuthenticationPolicy { get; }
        public string StationId => AuditPolicy.StationId;
        public string UserName => "alice-throttle";
        public string Password => "V105 correct horse battery";

        public static TestFixture Create(AuthenticationPolicy? authenticationPolicy = null)
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V105-AuthenticationThrottle",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var stationId = "V105Station";
            var policy = new AuditIntegrityPolicy(stationId, "v1", "SharpInspect.Test.V105." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directoryPath, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            return new TestFixture(directoryPath, policy, authenticationPolicy ?? AuthenticationPolicy.Development);
        }

        public LocalIdentityOptions IdentityOptions(Pbkdf2PasswordHasher? hasher = null) => new(
            StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "v105-test-blocklist", "v1", new[] { "known-compromised-value" })
            },
            hasher ?? new Pbkdf2PasswordHasher(), AuthenticationPolicy, AuthorizationPolicy.Development);

        public ProductionStoreOptions StoreOptions(LocalIdentityOptions? identity = null) => new(DatabasePath)
        {
            AuditIntegrityPolicy = AuditPolicy,
            LocalIdentity = identity ?? IdentityOptions(),
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };

        public async Task<SqliteCommandStore> OpenStoreAsync(LocalIdentityOptions? identity = null)
        {
            var store = new SqliteCommandStore(StoreOptions(identity));
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
            if (!initialized.Committed)
            {
                await store.DisposeAsync();
                throw new XunitException($"Store initialization failed: {initialized.ReasonCode}");
            }

            await WaitForVerifiedAsync(store);
            return store;
        }

        public LocalIdentityService Identity(SqliteCommandStore store, FakeConsoleAuthority authority,
            MutableClock clock, LocalIdentityOptions? options = null,
            Action<PasswordVerificationWork>? verificationObserver = null) =>
            new(store, options ?? IdentityOptions(), authority, () => clock.UtcNow, verificationObserver);

        public LocalIdentityService Identity(SqliteCommandStore store, FakeConsoleAuthority authority,
            BoundaryClock clock, LocalIdentityOptions? options = null,
            Action<PasswordVerificationWork>? verificationObserver = null) =>
            new(store, options ?? IdentityOptions(), authority, clock.Next, verificationObserver);

        public async Task<Guid> BootstrapAsync(LocalIdentityService identity, SqliteCommandStore store)
        {
            var issued = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(issued.Succeeded, issued.ReasonCode);
            var token = issued.Token!.TakeForDisplay();
            issued.Token.Dispose();
            await WaitForVerifiedAsync(store);

            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                StationId, token, UserName, "Alice Throttle", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            var principal = created.Identity!.PrincipalId;
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store);
            return principal;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(AuditPolicy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1416
