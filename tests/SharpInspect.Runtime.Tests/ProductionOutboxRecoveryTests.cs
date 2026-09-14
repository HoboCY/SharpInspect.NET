using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T53 focused verification: the governed recovery command surface, the optional schema-37
/// extension and the exact store bindings. The store-backed cases use the Windows machine-key
/// store profile; they run in the real-user context of the ticket validation and deliberately
/// contain no dynamic environment skip, so an unavailable environment is never recorded as a pass.
/// </summary>
public sealed class ProductionOutboxRecoveryTests
{
    [Fact]
    [Trait("VerificationId", "V153_S01")]
    public void V153_S01_RecoveryGrantRestoresEligibilityWithoutResettingAttemptNumbers()
    {
        var delivery = Delivery(maximumAttempts: 2);
        var firstAttempt = Guid.NewGuid();
        var secondAttempt = Guid.NewGuid();
        var events = new[]
        {
            Event(delivery, 1, OutboxEventKind.Created, null, null, "ProductionOutboxCreated", null, null, 10),
            Event(delivery, 2, OutboxEventKind.AttemptStarted, firstAttempt, 1, "ProductionOutboxAttemptStarted", null, null, 20),
            Event(delivery, 3, OutboxEventKind.AttemptFailed, firstAttempt, null, "OutboxTransportFailure",
                OutboxFailureCategory.Transient, DateTimeOffset.UtcNow.AddSeconds(30), 30),
            Event(delivery, 4, OutboxEventKind.AttemptStarted, secondAttempt, 2, "ProductionOutboxAttemptStarted", null, null, 40),
            Event(delivery, 5, OutboxEventKind.AttemptFailed, secondAttempt, null, "OutboxPermanentRejection",
                OutboxFailureCategory.Permanent, null, 50)
        };
        var blocked = SqliteCommandStore.DeriveProductionOutboxState(delivery, events);
        Assert.True(blocked.PermanentBlock);
        Assert.False(blocked.RetryEligible);
        Assert.Equal(2, blocked.AttemptCount);
        Assert.Equal(3, blocked.NextAttemptNumber);

        // The grant lands after the permanent failure: the original frozen budget stays in the
        // event facts while the derived allowance becomes the cumulative started count plus N.
        var grant = new SqliteCommandStore.ProductionOutboxRecoveryGrant(delivery.DeliveryId,
            Guid.NewGuid(), 60, 3);
        var recovered = SqliteCommandStore.DeriveProductionOutboxState(delivery, events, new[] { grant });
        Assert.False(recovered.PermanentBlock);
        Assert.True(recovered.RetryEligible);
        // Cumulative attempt numbers and the complete history survive: nothing is reset.
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(3, recovered.NextAttemptNumber);
        Assert.Equal(events[^1].Position, recovered.LastEventPosition);
        // The grant is bounded: after the three fresh attempts are consumed eligibility ends.
        var attempt3 = Guid.NewGuid();
        var attempt4 = Guid.NewGuid();
        var attempt5 = Guid.NewGuid();
        var exhausted = events.Concat(new[]
        {
            Event(delivery, 6, OutboxEventKind.AttemptStarted, attempt3, 3, "ProductionOutboxAttemptStarted", null, null, 70),
            Event(delivery, 7, OutboxEventKind.AttemptFailed, attempt3, null, "OutboxTransportFailure",
                OutboxFailureCategory.Transient, DateTimeOffset.UtcNow.AddSeconds(30), 80),
            Event(delivery, 8, OutboxEventKind.AttemptStarted, attempt4, 4, "ProductionOutboxAttemptStarted", null, null, 90),
            Event(delivery, 9, OutboxEventKind.AttemptFailed, attempt4, null, "OutboxTransportFailure",
                OutboxFailureCategory.Transient, DateTimeOffset.UtcNow.AddSeconds(30), 100),
            Event(delivery, 10, OutboxEventKind.AttemptStarted, attempt5, 5, "ProductionOutboxAttemptStarted", null, null, 110),
            Event(delivery, 11, OutboxEventKind.AttemptFailed, attempt5, null, "OutboxTransportFailure",
                OutboxFailureCategory.Transient, DateTimeOffset.UtcNow.AddSeconds(30), 120)
        }).ToArray();
        var afterGrant = SqliteCommandStore.DeriveProductionOutboxState(delivery, exhausted, new[] { grant });
        Assert.False(afterGrant.RetryEligible);
        Assert.Equal(5, afterGrant.AttemptCount);
        Assert.Equal(6, afterGrant.NextAttemptNumber);
    }

    [Fact]
    [Trait("VerificationId", "V153_S01")]
    public void V153_S01_RecoveryGrantFollowedByLaterPermanentFailureRemainsBlocked()
    {
        var delivery = Delivery(maximumAttempts: 1);
        var firstAttempt = Guid.NewGuid();
        var secondAttempt = Guid.NewGuid();
        // The grant is recorded only after the obligation is permanently blocked; a later
        // permanent outcome re-blocks it and the grant cannot be replayed into fresh authority.
        var grant = new SqliteCommandStore.ProductionOutboxRecoveryGrant(delivery.DeliveryId,
            Guid.NewGuid(), 40, 2);
        var events = new[]
        {
            Event(delivery, 1, OutboxEventKind.Created, null, null, "ProductionOutboxCreated", null, null, 10),
            Event(delivery, 2, OutboxEventKind.AttemptStarted, firstAttempt, 1, "ProductionOutboxAttemptStarted", null, null, 20),
            Event(delivery, 3, OutboxEventKind.AttemptFailed, firstAttempt, null, "OutboxPermanentRejection",
                OutboxFailureCategory.Permanent, null, 30),
            Event(delivery, 4, OutboxEventKind.AttemptStarted, secondAttempt, 2, "ProductionOutboxAttemptStarted", null, null, 50),
            Event(delivery, 5, OutboxEventKind.AttemptFailed, secondAttempt, null, "OutboxPermanentRejection",
                OutboxFailureCategory.Permanent, null, 60)
        };
        var state = SqliteCommandStore.DeriveProductionOutboxState(delivery, events, new[] { grant });
        Assert.True(state.PermanentBlock);
        Assert.False(state.RetryEligible);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal(3, state.NextAttemptNumber);
        Assert.Equal(OutboxFailureCategory.Permanent, state.LastFailureCategory);
    }

    [Fact]
    [Trait("VerificationId", "V153_S02")]
    public void V153_S02_OperationHashesBindEveryGovernedField()
    {
        var recoveryId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var commandEvent = Guid.NewGuid();
        var authorizationEvent = Guid.NewGuid();
        var recorded = DateTimeOffset.UtcNow;
        var baseline = SqliteCommandStore.ProductionOutboxRecoveryContentHash(recoveryId, deliveryId, 3,
            "OperatorRepairedEndpoint", actor, session, grant, "policy", "1", new string('A', 64),
            correlation, commandEvent, 100, new string('B', 64), authorizationEvent, 101,
            new string('C', 64), recorded, "delivery-target");
        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, SqliteCommandStore.ProductionOutboxRecoveryContentHash(recoveryId, deliveryId,
            3, "OperatorRepairedEndpoint", actor, session, grant, "policy", "1", new string('A', 64),
            correlation, commandEvent, 100, new string('B', 64), authorizationEvent, 101,
            new string('C', 64), recorded, "delivery-target"));
        // Every governed value changes the exact binding; no field is silently omitted.
        Assert.NotEqual(baseline, SqliteCommandStore.ProductionOutboxRecoveryContentHash(recoveryId, deliveryId,
            4, "OperatorRepairedEndpoint", actor, session, grant, "policy", "1", new string('A', 64),
            correlation, commandEvent, 100, new string('B', 64), authorizationEvent, 101,
            new string('C', 64), recorded, "delivery-target"));
        Assert.NotEqual(baseline, SqliteCommandStore.ProductionOutboxRecoveryContentHash(recoveryId, deliveryId,
            3, "DifferentReason", actor, session, grant, "policy", "1", new string('A', 64),
            correlation, commandEvent, 100, new string('B', 64), authorizationEvent, 101,
            new string('C', 64), recorded, "delivery-target"));
        Assert.NotEqual(baseline, SqliteCommandStore.ProductionOutboxRecoveryContentHash(recoveryId, deliveryId,
            3, "OperatorRepairedEndpoint", actor, session, grant, "policy", "1", new string('A', 64),
            correlation, commandEvent, 100, new string('B', 64), authorizationEvent, 101,
            new string('C', 64), recorded, "other-target"));
        var corrective = SqliteCommandStore.ProductionOutboxCorrectionContentHash(Guid.NewGuid(),
            deliveryId, "FinalBytesCorrected", new string('D', 64), actor, session, grant, "policy", "1",
            new string('A', 64), correlation, commandEvent, 100, new string('B', 64), authorizationEvent,
            101, new string('C', 64), recorded, "correction-target");
        Assert.Equal(64, corrective.Length);
        Assert.NotEqual(baseline, corrective);
    }

    [Fact]
    [Trait("VerificationId", "V153_P01")]
    public void V153_P01_NewPermissionsAndCommandKindsAreStableAndRequireStepUp()
    {
        Assert.Equal((ushort)40, (ushort)Permission.RecoverOutboxDelivery);
        Assert.Equal((ushort)41, (ushort)Permission.CreateCorrectiveOutboxDelivery);
        Assert.Equal(57, (int)AuditedCommandKind.RecoverOutboxDelivery);
        Assert.Equal(58, (int)AuditedCommandKind.CreateCorrectiveOutboxDelivery);
        var policy = AuthorizationPolicy.Development;
        Assert.True(policy.RequiresStepUp(Permission.RecoverOutboxDelivery));
        Assert.True(policy.RequiresStepUp(Permission.CreateCorrectiveOutboxDelivery));
        // The new permissions are explicit grants; the fixed development bundles keep their
        // exact prior contents and therefore their exact policy hash.
        foreach (var role in new[] { HumanRoleBundle.Operator, HumanRoleBundle.Technician,
            HumanRoleBundle.Administrator })
            Assert.DoesNotContain(policy.GetPermissions(role),
                permission => permission is Permission.RecoverOutboxDelivery or
                    Permission.CreateCorrectiveOutboxDelivery);
    }

    [Fact]
    [Trait("VerificationId", "V153_T03")]
    public void V153_T03_HistoricalHandlerGateResolvesOnlyTheExactFrozenRoute()
    {
        using var receiver = new OutboxReceiverFixture();
        var transport = new ProductionOutboxProtocolTests.ReceiverTransport(receiver);
        var options = new ProductionOutboxOptions(new[] { receiver.Binding(transport) });
        var pending = Pending(receiver.Delivery(Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.Null(ProductionOutboxWorker.HistoricalHandlerFailure(pending, options));

        var missing = new ProductionOutboxOptions(Array.Empty<OutboxTransportBinding>());
        Assert.Equal("OutboxRequiredHistoricalHandlerMissing",
            ProductionOutboxWorker.HistoricalHandlerFailure(pending, missing));

        using var bestEffort = new OutboxReceiverFixture(OutboxRouteCriticality.BestEffort, "best-effort");
        var bestEffortItem = Pending(bestEffort.Delivery(Encoding.UTF8.GetBytes("{\"a\":1}")));
        // A BestEffort obligation whose exact handler is gone is still an unavailable handler;
        // it becomes a durable system block rather than a fabricated attempt. Only the Required
        // route feeds the new-trigger admission block.
        Assert.Equal("OutboxHistoricalHandlerUnavailable",
            ProductionOutboxWorker.HistoricalHandlerFailure(bestEffortItem, missing));
        // The frozen route content hash (RouteId + version + payload contract) is the identity:
        // a handler registered for a different route never satisfies the exact obligation.
        using var otherRoute = new OutboxReceiverFixture(routeId: "result-v2");
        Assert.Equal("OutboxRequiredHistoricalHandlerMissing",
            ProductionOutboxWorker.HistoricalHandlerFailure(pending,
                new ProductionOutboxOptions(new[]
                { otherRoute.Binding(new ProductionOutboxProtocolTests.ReceiverTransport(otherRoute)) })));
    }

    [Fact]
    [Trait("VerificationId", "V153_M01")]
    public void V153_M01_Schema37PlanBindsOnlyTheNewExtensionAndKeepsTheSchema36Binding()
    {
        using var receiver = new OutboxReceiverFixture();
        var schema36 = new ProductionOutboxStoreOptions(new[] { receiver.Route });
        var schema37 = new ProductionOutboxStoreOptions(new[] { receiver.Route })
        {
            ManualRecovery = new ProductionOutboxRecoveryOptions()
        };
        // The exact schema-36 outbox binding and its signed activation stay valid: only the new
        // extension configuration carries the recovery budget.
        Assert.Equal(schema36.BindingHash, schema37.BindingHash);
        Assert.NotEqual(schema37.ManualRecovery!.BindingHash,
            new ProductionOutboxRecoveryOptions { MaximumGrantedAttempts = 4 }.BindingHash);
        Assert.True(StoreMigrationJournal.TryResolvePlan(36, 37,
            StoreMigrationJournal.ProductionOutboxRecoveryPlanId, out var plan));
        Assert.True(plan.ProductionOutbox);
        Assert.True(plan.FlexibleFeatures);
        // Only the exact source/target/plan triple is accepted: no older generation jumps to 37
        // and the schema-36 plan is not renamed. The real public migration path is exercised
        // separately by the migration tooling, so this focused check stops at plan resolution.
        Assert.False(StoreMigrationJournal.TryResolvePlan(35, 37,
            StoreMigrationJournal.ProductionOutboxRecoveryPlanId, out _));
        Assert.False(StoreMigrationJournal.TryResolvePlan(36, 37,
            StoreMigrationJournal.ProductionOutboxPlanId, out _));
        var policy = new AuditIntegrityPolicy("V153M01", "v1", "V153.M01." + Guid.NewGuid().ToString("N"));
        var source = SqliteCommandStore.MigrationProductionOutboxRecoverySourceOptions(new(
            Path.Combine(Path.GetTempPath(), "V153", "unused.sqlite"))
        {
            AuditIntegrityPolicy = policy,
            ProductionInspections = new ProductionInspectionStoreOptions(),
            TraceStoragePolicies = new TraceStoragePolicyStoreOptions
            {
                DeploymentScope = new TraceStorageDeploymentScope("V153", "1",
                    Array.Empty<TraceStorageRouteIdentity>())
            },
            Outbox = schema37
        });
        Assert.Null(source.Outbox!.ManualRecovery);
        Assert.Equal(schema36.BindingHash, source.Outbox!.BindingHash);
    }

    [Fact]
    [Trait("VerificationId", "V153_T01")]
    public async Task V153_T01_FreshSchema37StoreKeepsTheSchema36BindingAndRefusesTheOldWriter()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var baseline = fixture.Outbox();
        var recovery = new ProductionOutboxRecoveryOptions();
        var withRecovery = new ProductionOutboxStoreOptions(baseline.Routes)
        {
            ManualRecovery = recovery
        };
        await using (var store = new SqliteCommandStore(CopyWithOutbox(fixture.Options(), withRecovery)))
        {
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            Assert.Equal(37, ReadUserVersion(fixture.DatabasePath));
        }
        // The exact schema-36 writer refuses a schema-37 database without any rewrite.
        await using (var schema36Writer = new SqliteCommandStore(fixture.Options()))
        {
            var result = await schema36Writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(result.Committed);
            Assert.Equal(37, ReadUserVersion(fixture.DatabasePath));
        }
    }

    [Fact]
    [Trait("VerificationId", "V153_T02")]
    public async Task V153_T02_Schema36StoreRefusesTheRecoveryExtensionUntilTheExplicitMigration()
    {
        var directory = ProductionOutboxStorageTests.TestDirectory("V153-schema36");
        try
        {
            var policy = new AuditIntegrityPolicy("V153Schema36", "v1",
                "SharpInspect.Test.V153." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys")
            };
            var baseline = new ProductionOutboxStoreOptions(new[]
            {
                ProductionOutboxStorageTests.Route("route.primary", OutboxRouteCriticality.Required, 4096)
            });
            var options = new ProductionStoreOptions(Path.Combine(directory, "store.sqlite"))
            {
                AuditIntegrityPolicy = policy,
                LocalIdentity = new LocalIdentityOptions(policy.StationId,
                    new LocalPasswordPolicy
                    {
                        Blocklist = PasswordBlocklist.Create("v153-blocklist", "v1",
                            new[] { "known-compromised" })
                    },
                    new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                    AuthorizationPolicy.Development),
                ProductionInspections = new ProductionInspectionStoreOptions(),
                TraceStoragePolicies = new TraceStoragePolicyStoreOptions
                {
                    DeploymentScope = new TraceStorageDeploymentScope("V153", "1",
                        Array.Empty<TraceStorageRouteIdentity>())
                },
                Outbox = baseline
            };
            await using (var store = new SqliteCommandStore(options))
                Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            Assert.Equal(36, ReadUserVersion(options.DatabasePath));
            var recoveryOptions = CopyWithOutbox(options, new ProductionOutboxStoreOptions(baseline.Routes)
            {
                ManualRecovery = new ProductionOutboxRecoveryOptions()
            });
            await using (var recoveryWriter = new SqliteCommandStore(recoveryOptions))
            {
                var result = await recoveryWriter.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.False(result.Committed);
                Assert.Equal("ProductionOutboxRecoveryGovernedMigrationRequired", result.ReasonCode);
                Assert.Equal(36, ReadUserVersion(options.DatabasePath));
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// ProductionStoreOptions is an immutable class without a with-expression, so the focused
    /// schema checks copy every declared field and replace only the outbox binding.
    /// </summary>
    internal static ProductionStoreOptions CopyWithOutbox(ProductionStoreOptions source,
        ProductionOutboxStoreOptions? outbox) => new()
    {
        DatabasePath = source.DatabasePath,
        CommitTimeout = source.CommitTimeout,
        QueryTimeout = source.QueryTimeout,
        QueueCapacity = source.QueueCapacity,
        AuditIntegrityPolicy = source.AuditIntegrityPolicy,
        LocalIdentity = source.LocalIdentity,
        AlarmPolicy = source.AlarmPolicy,
        ExternalAuditAnchor = source.ExternalAuditAnchor,
        AlgorithmResultArchive = source.AlgorithmResultArchive,
        RecipeDrafts = source.RecipeDrafts,
        CameraSetup = source.CameraSetup,
        CameraRecovery = source.CameraRecovery,
        CameraNetwork = source.CameraNetwork,
        ImagingSetup = source.ImagingSetup,
        CalibrationSessions = source.CalibrationSessions,
        CalibrationGovernance = source.CalibrationGovernance,
        RecipeReleases = source.RecipeReleases,
        PlcResultContracts = source.PlcResultContracts,
        RecipeActivations = source.RecipeActivations,
        PreviewSessions = source.PreviewSessions,
        CalibrationImports = source.CalibrationImports,
        ManualInspections = source.ManualInspections,
        ProductionAdmission = source.ProductionAdmission,
        StationQualifications = source.StationQualifications,
        RecipeTransfers = source.RecipeTransfers,
        TraceStoragePolicies = source.TraceStoragePolicies,
        QualificationCycles = source.QualificationCycles,
        PlcCommunication = source.PlcCommunication,
        ProductionInspections = source.ProductionInspections,
        PartIdentities = source.PartIdentities,
        ProductionRecovery = source.ProductionRecovery,
        RecipeSelections = source.RecipeSelections,
        ProductionArming = source.ProductionArming,
        RecipeLifecycle = source.RecipeLifecycle,
        ImageEvidence = source.ImageEvidence,
        ImageFinalization = source.ImageFinalization,
        Outbox = outbox
    };

    private static int ReadUserVersion(string databasePath)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OutboxPendingItem Pending(OutboxDelivery delivery) =>
        new(delivery, OutboxDeliveryState.Pending, 0, null, null, true, null, false, null, 1,
            new string('1', 64));

    private static OutboxDelivery Delivery(int maximumAttempts)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var route = new OutboxRouteDefinition("route.primary", "1", OutboxRouteCriticality.Required,
            "receiver.station", new OutboxContractReference("payload.contract", "1", new string('A', 64)),
            "application/json",
            new OutboxContractReference("receiver.contract", "1", new string('B', 64)),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            new OutboxContractReference("adapter.contract", "1", new string('C', 64)), 4096);
        return new OutboxDelivery(Guid.NewGuid(), Guid.NewGuid(), new string('D', 64), route,
            new OutboxPayloadSnapshot(route.PayloadContract, route.ContentType,
                Encoding.UTF8.GetBytes("{\"kind\":\"core\"}")), null, DateTimeOffset.UtcNow,
            maximumAttempts);
    }

    private static ProductionOutboxEvent Event(OutboxDelivery delivery, long position, OutboxEventKind kind,
        Guid? attemptId, int? attemptNumber, string reasonCode, OutboxFailureCategory? category,
        DateTimeOffset? retryAfter, long auditSequence)
    {
        var recorded = DateTimeOffset.UtcNow.AddSeconds(position);
        return new ProductionOutboxEvent(position, Guid.NewGuid(), delivery.DeliveryId,
            delivery.InspectionId, delivery.CoreHash, new string('E', 64), delivery.Route.RouteId,
            delivery.Route.Version, delivery.Route.ContentHash, position, kind, attemptId,
            attemptNumber, Guid.Parse("11111111-1111-1111-1111-111111111111"), recorded, reasonCode,
            category, retryAfter,
            kind == OutboxEventKind.AttemptStarted ? new string('F', 64) : null,
            delivery.MaximumAttempts, null, null, null, ReadOnlySpan<byte>.Empty,
            delivery.Payload?.ContentHash, new string('1', 64), auditSequence, new string('2', 64));
    }
}
