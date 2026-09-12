using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T52 focused storage tests: option binding, the schema-36 store shape, configuration
/// tamper rejection, fail-closed readers and the pure lifecycle derivation. End-to-end Core
/// atomicity and acceptance-claim integration remain primary-owned runtime gates.
/// </summary>
public sealed class ProductionOutboxStorageTests
{
    [Fact]
    [Trait("VerificationId", "V152_S01")]
    public void V152_S01_RoutesBudgetsAndOptionalPresenceAreExactlyBound()
    {
        var route = Route("route.primary", OutboxRouteCriticality.Required, 4096);
        var options = new ProductionOutboxStoreOptions(new[] { route });
        options.Validate();
        Assert.Single(options.Routes);
        Assert.Equal(route.ContentHash, options.Routes[0].ContentHash);
        Assert.Equal(64, options.RouteSetHash.Length);
        Assert.Equal(64, options.BindingHash.Length);
        Assert.NotEqual(options.BindingHash,
            new ProductionOutboxStoreOptions(new[] { route }) { MaximumAttempts = 6 }.BindingHash);
        Assert.NotEqual(options.BindingHash, new ProductionOutboxStoreOptions(new[]
        { Route("route.primary", OutboxRouteCriticality.BestEffort, 4096) }).BindingHash);
        Assert.Throws<ArgumentException>(() => new ProductionOutboxStoreOptions(
            new[] { route, Route("route.primary", OutboxRouteCriticality.BestEffort, 4096) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProductionOutboxStoreOptions(
            Array.Empty<OutboxRouteDefinition>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => (new ProductionOutboxStoreOptions(new[] { route })
        { MaximumAttempts = 33 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new ProductionOutboxStoreOptions(new[] { route })
        { AttemptTimeout = TimeSpan.FromMinutes(2) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new ProductionOutboxStoreOptions(new[] { route })
        { MaximumRetryDelay = TimeSpan.FromMinutes(61) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new ProductionOutboxStoreOptions(new[] { route })
        { MaximumPageSize = 513 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new ProductionOutboxStoreOptions(new[] { route })
        { MaximumTotalBytes = 1024 }).Validate());
        // A declared legacy feature requires its predecessor, so presence can never be
        // half-bound in the activation payload.
        var lifecycle = new RecipeLifecycleStoreOptions();
        var stage = new ProductionImageStageOptions(TestDirectory("V152-stage"), 512 * 1024,
            32 * 1024 * 1024, 200);
        var evidence = new ProductionImageEvidenceStoreOptions(stage);
        Assert.Throws<ArgumentException>(() => new ProductionOutboxStoreOptions(new[] { route },
            recipeLifecycle: null, imageEvidence: evidence, imageFinalization: null).Validate());
        var bound = new ProductionOutboxStoreOptions(new[] { route }, recipeLifecycle: lifecycle,
            imageEvidence: null, imageFinalization: null);
        bound.Validate();
        Assert.NotNull(bound.RecipeLifecyclePresenceHash);
        Assert.Null(bound.ImageEvidencePresenceHash);
        Assert.True(bound.EncodeActivationPayload().Length > 0);
        Assert.NotEqual(bound.BindingHash, new ProductionOutboxStoreOptions(new[] { route },
            recipeLifecycle: new RecipeLifecycleStoreOptions { MaxEvents = 5 }, imageEvidence: null,
            imageFinalization: null).BindingHash);
    }

    [Fact]
    [Trait("VerificationId", "V152_S02")]
    public async Task V152_S02_FreshSchema36StoreCarriesTheExactOutboxLedger()
    {
        using var fixture = new OutboxStoreFixture();
        await using var store = new SqliteCommandStore(fixture.Options());
        var initialized = await store.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        using var connection = SqliteNative.Open(fixture.DatabasePath, readOnly: true);
        var database = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(5));
        Assert.Equal(36, ProductionOutboxStoreOptions.SchemaVersion);
        Assert.Equal(ProductionOutboxStoreOptions.SchemaVersion,
            AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        Assert.Equal(4, AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name IN ('production_outbox_store_config',
                'production_outbox_deliveries','production_outbox_events','production_outbox_work');",
            deadline));
        Assert.Equal(1, AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM audit_entries
            WHERE Kind='ProductionOutboxStoreActivated';", deadline));
        // The immutable configuration row equals the caller's exact bounded options.
        SqliteCommandStore.RequireConfiguredProductionOutbox(database, fixture.Outbox(), deadline);
        Assert.Empty(SqliteCommandStore.ReadProductionOutboxRows(database, fixture.Outbox(), deadline));
        Assert.Empty(SqliteCommandStore.ReadProductionOutboxDeliveries(database, fixture.Outbox(), deadline));
        Assert.Equal(0, SqliteCommandStore.ReadProductionOutboxAuditReserve(database, deadline));
        // The configuration row is immutable even for the only writer handle.
        Assert.Throws<SqliteNativeException>(() => AuditChainDatabase.Execute(database,
            "UPDATE production_outbox_store_config SET MaximumEvents=MaximumEvents+1;", deadline));
        // A foreign option set can never be opened against this store.
        var foreign = new ProductionOutboxStoreOptions(new[]
        { Route("route.primary", OutboxRouteCriticality.Required, 8192) });
        Assert.Throws<InvalidOperationException>(() => SqliteCommandStore.RequireConfiguredProductionOutbox(
            database, foreign, deadline));
    }

    [Fact]
    [Trait("VerificationId", "V152_S03")]
    public async Task V152_S03_StoreWithoutOutboxOrWithoutProductionPrerequisitesFailsClosed()
    {
        using var fixture = new OutboxStoreFixture();
        await using (var store = new SqliteCommandStore(fixture.Options()))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
        }
        // A schema-36 store opened without the outbox option is refused at the store boundary.
        var withoutOutbox = fixture.Options(withOutbox: false);
        await using var reader = new SqliteCommandStore(withoutOutbox);
        var rejected = await reader.Initialization;
        Assert.False(rejected.Committed);
        Assert.Equal("ProductionOutboxConfigurationRequired", rejected.ReasonCode);
        // The outbox requires the production inspection and trace policy prerequisites.
        Assert.Throws<ArgumentException>(() =>
            new SqliteCommandStore(fixture.Options(withProductionInspections: false)));
        // The read-only query never fabricates an empty backlog for an unconfigured store.
        var query = new SqliteProductionOutboxQuery(withoutOutbox);
        var page = await query.ReadPendingAsync();
        Assert.False(page.Available);
        Assert.Equal("ProductionOutboxConfigurationRequired", page.ReasonCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.ReadBacklogAsync().AsTask());
        // A configured store whose persisted routes no longer match fails closed before any
        // page is produced.
        var mismatched = new SqliteProductionOutboxQuery(fixture.Options(withForeignRoutes: true));
        var unavailable = await mismatched.ReadPendingAsync();
        Assert.False(unavailable.Available);
        Assert.Equal("ProductionOutboxConfigurationMismatch", unavailable.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V152_S04")]
    public void V152_S04_LifecycleDerivationHonorsAttemptsBudgetAndOneSuccess()
    {
        var delivery = Delivery("route.primary", maximumAttempts: 2);
        var started = Event(delivery, 2, OutboxEventKind.AttemptStarted, Guid.NewGuid(), 1, null,
            null, null);
        var active = SqliteCommandStore.DeriveProductionOutboxState(delivery, new[] { started });
        Assert.Equal(OutboxDeliveryState.Pending, active.State);
        Assert.Equal(1, active.AttemptCount);
        Assert.Equal(2, active.NextAttemptNumber);
        Assert.Equal(started.AttemptId, active.ActiveAttemptId);
        Assert.True(active.RetryEligible);
        Assert.False(active.PermanentBlock);
        var transient = Event(delivery, 3, OutboxEventKind.AttemptFailed, started.AttemptId, 1,
            "V152.Transient", OutboxFailureCategory.Transient, DateTimeOffset.UtcNow.AddSeconds(5));
        var failed = SqliteCommandStore.DeriveProductionOutboxState(delivery, new[] { started, transient });
        Assert.Equal(OutboxDeliveryState.Failed, failed.State);
        Assert.Null(failed.ActiveAttemptId);
        Assert.True(failed.RetryEligible);
        Assert.Equal("V152.Transient", failed.LastFailureReasonCode);
        Assert.Equal(OutboxFailureCategory.Transient, failed.LastFailureCategory);
        // The second and final attempt fails: the retained obligation can no longer retry but
        // is not declared permanently blocked.
        var secondStart = Event(delivery, 4, OutboxEventKind.AttemptStarted, Guid.NewGuid(), 2, null,
            null, null);
        var secondFailure = Event(delivery, 5, OutboxEventKind.AttemptFailed, secondStart.AttemptId, 2,
            "V152.Exhausted", OutboxFailureCategory.UnknownOutcome, null);
        var exhausted = SqliteCommandStore.DeriveProductionOutboxState(delivery,
            new[] { started, transient, secondStart, secondFailure });
        Assert.False(exhausted.RetryEligible);
        Assert.False(exhausted.PermanentBlock);
        Assert.Equal(2, exhausted.AttemptCount);
        // A permanent failure blocks the obligation immediately and monotonic success is the
        // only terminal state with an exclusive one-success rule.
        var permanent = Event(delivery, 6, OutboxEventKind.AttemptFailed, secondStart.AttemptId, 2,
            "V152.Permanent", OutboxFailureCategory.Permanent, null);
        var blocked = SqliteCommandStore.DeriveProductionOutboxState(delivery,
            new[] { started, transient, secondStart, permanent });
        Assert.True(blocked.PermanentBlock);
        Assert.False(blocked.RetryEligible);
        Assert.Equal(OutboxFailureCategory.Permanent, blocked.LastFailureCategory);
        var succeeded = Event(delivery, 7, OutboxEventKind.Succeeded, secondStart.AttemptId, 2,
            "ProductionOutboxSucceeded", null, null);
        var completed = SqliteCommandStore.DeriveProductionOutboxState(delivery,
            new[] { started, transient, secondStart, succeeded });
        Assert.Equal(OutboxDeliveryState.Succeeded, completed.State);
        Assert.False(completed.RetryEligible);
        Assert.Null(completed.ActiveAttemptId);
        Assert.Equal(7, completed.LastEventPosition);
    }

    internal static string TestDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static OutboxRouteDefinition Route(string routeId, OutboxRouteCriticality criticality,
        int maximumPayloadBytes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new OutboxRouteDefinition(routeId, "1", criticality, "receiver.station",
            new OutboxContractReference("payload.contract", "1", new string('A', 64)),
            "application/json",
            new OutboxContractReference("receiver.contract", "1", new string('B', 64)),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            new OutboxContractReference("adapter.contract", "1", new string('C', 64)),
            maximumPayloadBytes);
    }

    internal static OutboxDelivery Delivery(string routeId, int maximumAttempts, string? payload = null)
    {
        var route = Route(routeId, OutboxRouteCriticality.Required, 4096);
        var bytes = Encoding.UTF8.GetBytes(payload ?? "{\"kind\":\"core\"}");
        var snapshot = new OutboxPayloadSnapshot(route.PayloadContract, route.ContentType, bytes);
        return new OutboxDelivery(Guid.NewGuid(), Guid.NewGuid(), new string('D', 64), route, snapshot,
            null, DateTimeOffset.UtcNow, maximumAttempts);
    }

    private static ProductionOutboxEvent Event(OutboxDelivery delivery, long position, OutboxEventKind kind,
        Guid? attemptId, int? attemptNumber, string? reasonCode, OutboxFailureCategory? category,
        DateTimeOffset? retryAfter)
    {
        var recorded = DateTimeOffset.UtcNow;
        var succeeded = kind == OutboxEventKind.Succeeded;
        return new ProductionOutboxEvent(position, Guid.NewGuid(), delivery.DeliveryId,
            delivery.InspectionId, delivery.CoreHash, new string('E', 64), delivery.Route.RouteId,
            delivery.Route.Version, delivery.Route.ContentHash, position - 1, kind, attemptId,
            attemptNumber, Guid.Parse("11111111-1111-1111-1111-111111111111"), recorded,
            reasonCode ?? "ProductionOutboxCreated", category, retryAfter,
            kind == OutboxEventKind.AttemptStarted ? new string('F', 64) : null,
            delivery.MaximumAttempts,
            succeeded ? "receipt-1" : null,
            succeeded ? new string('9', 64) : null,
            succeeded ? recorded : null,
            succeeded ? new ReadOnlySpan<byte>(new byte[] { 1, 2, 3 }) : ReadOnlySpan<byte>.Empty,
            delivery.Payload?.ContentHash, new string('1', 64), position, new string('2', 64));
    }

    internal sealed class OutboxStoreFixture : IDisposable
    {
        internal OutboxStoreFixture()
        {
            DirectoryPath = TestDirectory("V152-outbox");
            DatabasePath = Path.Combine(DirectoryPath, "outbox.sqlite");
            var policy = new AuditIntegrityPolicy("V152OutboxStation", "v1",
                "SharpInspect.Test.V152." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(DirectoryPath, "keys")
            };
            Policy = policy;
            Identity = new LocalIdentityOptions(policy.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v152-blocklist", "v1",
                        new[] { "known-compromised" })
                },
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            Inspections = new ProductionInspectionStoreOptions();
            TracePolicies = new TraceStoragePolicyStoreOptions
            {
                DeploymentScope = new TraceStorageDeploymentScope("V152.Deployment", "1",
                    Array.Empty<TraceStorageRouteIdentity>())
            };
            _routes = new[] { Route("route.primary", OutboxRouteCriticality.Required, 4096) };
        }

        private readonly OutboxRouteDefinition[] _routes;
        internal string DirectoryPath { get; }
        internal string DatabasePath { get; }
        internal AuditIntegrityPolicy Policy { get; }
        internal LocalIdentityOptions Identity { get; }
        internal ProductionInspectionStoreOptions Inspections { get; }
        internal TraceStoragePolicyStoreOptions TracePolicies { get; }

        internal ProductionOutboxStoreOptions Outbox() => new(_routes);

        internal ProductionStoreOptions Options(bool withOutbox = true,
            bool withProductionInspections = true, bool withForeignRoutes = false) => new(DatabasePath)
        {
            AuditIntegrityPolicy = Policy,
            LocalIdentity = Identity,
            ProductionInspections = withProductionInspections ? Inspections : null,
            TraceStoragePolicies = TracePolicies,
            Outbox = withOutbox
                ? withForeignRoutes
                    ? new ProductionOutboxStoreOptions(new[]
                    { Route("route.primary", OutboxRouteCriticality.Required, 8192) })
                    : new ProductionOutboxStoreOptions(_routes)
                : null,
            CommitTimeout = TimeSpan.FromSeconds(3),
            QueryTimeout = TimeSpan.FromSeconds(3),
            QueueCapacity = 8
        };

        public void Dispose()
        {
            try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
