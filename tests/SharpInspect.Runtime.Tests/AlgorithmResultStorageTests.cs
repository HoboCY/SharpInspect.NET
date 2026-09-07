using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmResultStorageTests
{
    [Fact]
    public async Task V114_S01_Schema8RoundTripsAndSurvivesRestart()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        var document = TestDocument() with { RecordedAtUtc = DateTimeOffset.UnixEpoch };

        var written = await fixture.AppendAsync(document);
        Assert.True(written.Committed, written.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        using (var connection = fixture.Open(readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, Convert.ToInt64(command.ExecuteScalar()));
            command.CommandText = "SELECT COUNT(*) FROM development_algorithm_results;";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }

        var query = new SqliteAlgorithmResultQuery(fixture.Options);
        var page = await query.QueryAsync(new AlgorithmResultFilter(PageSize: 10));
        Assert.True(page.Available, page.ReasonCode);
        var record = Assert.Single(page.Records);
        Assert.Equal(document.RecordId, record.RecordId);
        Assert.Equal(document.PayloadHash, record.ContentHash);
        Assert.Equal(document.Algorithm.Id, record.Algorithm.Id);
        Assert.Equal(document.FrameMetadata.Width, record.FrameMetadata.Width);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, record.RecordedAtUtc);
        Assert.Null(page.NextAfterPosition);

        await fixture.DisposeStoreAsync();
        await fixture.ReopenAsync();
        var restarted = await new SqliteAlgorithmResultQuery(fixture.Options)
            .QueryAsync(new AlgorithmResultFilter(PageSize: 10));
        Assert.True(restarted.Available, restarted.ReasonCode);
        Assert.Equal(document.RecordId, Assert.Single(restarted.Records).RecordId);
    }

    [Fact]
    public async Task V114_S02_DuplicateIsIdempotentAndConflictingIdentityIsRejected()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        var document = TestDocument();

        var first = await fixture.AppendAsync(document);
        Assert.True(first.Committed, first.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var duplicate = await fixture.AppendAsync(document);
        Assert.True(duplicate.Committed, duplicate.ReasonCode);
        Assert.Equal("AlgorithmResultAlreadyPersisted", duplicate.ReasonCode);

        var sameCorrelation = TestDocument(recordId: Guid.NewGuid(), correlation: document.Correlation);
        var correlationConflict = await fixture.AppendAsync(sameCorrelation);
        Assert.False(correlationConflict.Committed);
        Assert.Equal("AlgorithmResultCorrelationConflict", correlationConflict.ReasonCode);

        var differentPayload = TestDocument(recordId: document.RecordId,
            correlation: new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()),
            decision: InspectionDecision.Fail);
        var recordConflict = await fixture.AppendAsync(differentPayload);
        Assert.False(recordConflict.Committed);
        Assert.Equal("AlgorithmResultRecordIdConflict", recordConflict.ReasonCode);
    }

    [Fact]
    public async Task V114_S03_ProductionAndMalformedArchivesNeverCreatePartialRows()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();

        var production = TestOutcome(new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid()));
        Assert.False(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), production,
            out _, out var productionReason));
        Assert.Equal("AlgorithmResultProductionForbidden", productionReason);

        var invalid = TestDocument();
        var malformed = invalid with { PayloadHash = new string('0', 64) };
        var write = await fixture.AppendAsync(malformed);
        Assert.False(write.Committed);
        Assert.Equal("AlgorithmResultPayloadHashMismatch", write.ReasonCode);

        var nonCanonical = invalid with
        {
            PayloadJson = "{}",
            PayloadHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{}")))
        };
        var rejectedPayload = await fixture.AppendAsync(nonCanonical);
        Assert.False(rejectedPayload.Committed);
        Assert.Contains("AlgorithmResultPayload", rejectedPayload.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM development_algorithm_results;"));
    }

    [Fact]
    public async Task V114_S04_PayloadTamperIsRejectedEvenWhenRowTriggerIsRestored()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        var document = TestDocument();
        Assert.True((await fixture.AppendAsync(document)).Committed);
        await fixture.WaitForVerifiedAsync();
        await fixture.DisposeStoreAsync();

        using (var connection = fixture.Open(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                DROP TRIGGER development_algorithm_results_immutable_update;
                UPDATE development_algorithm_results
                SET PayloadJson = PayloadJson || 'tampered'
                WHERE RecordId = $recordId;
                CREATE TRIGGER development_algorithm_results_immutable_update
                BEFORE UPDATE ON development_algorithm_results BEGIN
                    SELECT RAISE(ABORT, 'ImmutableAlgorithmResult');
                END;";
            command.Parameters.AddWithValue("$recordId", document.RecordId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var page = await new SqliteAlgorithmResultQuery(fixture.Options)
            .QueryAsync(new AlgorithmResultFilter(PageSize: 10));
        Assert.False(page.Available);
        Assert.Contains("AlgorithmResult", page.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V114_S05_IndexTamperIsRejectedBeforeAFilteredPageCanHideIt()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        var document = TestDocument();
        Assert.True((await fixture.AppendAsync(document)).Committed);
        await fixture.WaitForVerifiedAsync();
        await fixture.DisposeStoreAsync();

        using (var connection = fixture.Open(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                DROP TRIGGER development_algorithm_results_immutable_update;
                UPDATE development_algorithm_results
                SET CorrelationId = $otherCorrelation
                WHERE RecordId = $recordId;
                CREATE TRIGGER development_algorithm_results_immutable_update
                BEFORE UPDATE ON development_algorithm_results BEGIN
                    SELECT RAISE(ABORT, 'ImmutableAlgorithmResult');
                END;";
            command.Parameters.AddWithValue("$otherCorrelation", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$recordId", document.RecordId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var page = await new SqliteAlgorithmResultQuery(fixture.Options)
            .QueryAsync(new AlgorithmResultFilter(Correlation: document.Correlation, PageSize: 1));
        Assert.False(page.Available);
        Assert.Contains("AlgorithmResult", page.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V114_S06_EnablingArchiveOnSchema7RequiresGovernedMigration()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync(archive: null, enableArchive: false);
        await fixture.DisposeStoreAsync();

        var archiveOptions = new AlgorithmResultArchiveOptions();
        await fixture.ReopenAsync(archiveOptions);
        var initialization = await fixture.Store!.Initialization;
        Assert.False(initialization.Committed);
        Assert.Equal("AlgorithmResultArchiveGovernedMigrationRequired", initialization.ReasonCode);

        var report = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Equal("AlgorithmResultArchiveGovernedMigrationRequired", report.ReasonCode);
    }

    [Fact]
    public async Task V114_S07_RecordCapacityIsIndependentFromMaximumRecordSize()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync(new AlgorithmResultArchiveOptions
        {
            MaximumRecords = 1
        });
        var first = TestDocument();
        Assert.True((await fixture.AppendAsync(first)).Committed);
        await fixture.WaitForVerifiedAsync();

        var second = TestDocument(correlation: new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid()));
        var result = await fixture.AppendAsync(second);
        Assert.False(result.Committed);
        Assert.Equal("AlgorithmResultArchiveCapacityExceeded", result.ReasonCode);
    }

    [Fact]
    public async Task V114_S08_ReadOnlyQueryAndPreAdmissionCancellationDoNotCreateRows()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        var before = fixture.Scalar("SELECT COUNT(*) FROM development_algorithm_results;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store!
            .AppendAlgorithmResultAsync(TestDocument(), new StoreDeadline(TimeSpan.FromSeconds(2)),
                cancellation.Token).AsTask());
        var page = await new SqliteAlgorithmResultQuery(fixture.Options)
            .QueryAsync(new AlgorithmResultFilter(PageSize: 1));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Records);
        Assert.Equal(before, fixture.Scalar("SELECT COUNT(*) FROM development_algorithm_results;"));
    }

    [Fact]
    public async Task V114_S09_FacadeRejectsFifthOutstandingRequestBeforeEncodingAndReleasesSlot()
    {
        RequireWindows();
        await using var fixture = await ArchiveFixture.CreateAsync();
        Assert.NotNull(fixture.Store);
        var archive = new AlgorithmResultArchive(fixture.Store!);

        using var blocker = fixture.Open(readOnly: false);
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var pending = new Task<AlgorithmResultArchiveResult>[AlgorithmResultArchive.MaximumOutstandingRequests];
        for (var i = 0; i < pending.Length; i++)
        {
            pending[i] = archive.AppendAsync(Guid.NewGuid(), TestOutcome(
                new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()))).AsTask();
            Assert.False(pending[i].IsCompleted,
                $"request {i} completed before the SQLite writer was released");
        }

        var fifth = await archive.AppendAsync(Guid.NewGuid(), null!);
        Assert.False(fifth.Recorded);
        Assert.Equal("AlgorithmResultArchiveCapacityExceeded", fifth.ReasonCode);

        using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        var completed = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(completed, result => Assert.True(result.Recorded, result.ReasonCode));

        var afterRelease = await archive.AppendAsync(Guid.NewGuid(), TestOutcome(
            new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid())));
        Assert.True(afterRelease.Recorded, afterRelease.ReasonCode);
    }

    [Fact]
    public async Task V114_S10_ArchiveBudgetLeavesControlReserveAndQueriesRemainUsable()
    {
        RequireWindows();
        var alarmPolicy = new AlarmPolicy("V114AlarmPolicy", "1", new[]
        {
            new AlarmPolicyRule("V114-CODE", "V114-SOURCE", AlarmSeverity.Warning,
                ProductionImpact.None, false, AlarmNotification.None, null)
        }, TimeSpan.FromMinutes(1), maximumActiveInstances: 4, maximumPlcEntries: 1);
        await using var fixture = await ArchiveFixture.CreateAsync(
            new AlgorithmResultArchiveOptions { MaximumRecords = 200 },
            maximumVerificationEntries: 202, alarmPolicy: alarmPolicy);
        var store = fixture.Store!;
        var policyBudget = fixture.Options.AuditIntegrityPolicy!.MaximumVerificationEntries;
        var archiveLimit = policyBudget - AlgorithmResultArchiveOptions.ControlVerificationReserve;

        while (fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;") < archiveLimit)
        {
            var tail = fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;");
            var batchSize = checked((int)Math.Min(8, archiveLimit - tail));
            var events = Enumerable.Range(0, batchSize)
                .Select(_ => new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.SessionLocked,
                    DateTimeOffset.UtcNow, fixture.Options.LocalIdentity!.StationId, Guid.NewGuid(),
                    null, null, null, "V114BudgetPadding") { SessionId = Guid.NewGuid() })
                .ToArray();
            var padding = await store.UpdateIdentityAsync(
                _ => new IdentityUpdate("V114BudgetPadding", events), CancellationToken.None);
            Assert.True(padding.Committed, padding.ReasonCode);
            await fixture.WaitForVerifiedAsync();
        }

        var beforeRows = fixture.Scalar("SELECT COUNT(*) FROM development_algorithm_results;");
        var beforeAudit = fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;");
        var rejected = await fixture.AppendAsync(TestDocument());
        Assert.False(rejected.Committed);
        Assert.Equal("AlgorithmResultArchiveCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(beforeRows, fixture.Scalar("SELECT COUNT(*) FROM development_algorithm_results;"));
        Assert.Equal(beforeAudit, fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"));
        Assert.Equal(AuditIntegrityState.Verified, store.Integrity?.State);

        var control = await store.AppendAsync(new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, AuditedCommandKind.ArmProduction,
            CommandSource.PhysicalConsole, "V114-budget", null, null, CommandAuditPhase.Outcome,
            CommandDisposition.Rejected, "V114BudgetControl"), new StoreDeadline(TimeSpan.FromSeconds(3)));
        Assert.True(control.Committed, control.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var identity = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(fixture.Options.LocalIdentity!.StationId, identity.StationId);
        var alarmState = await store.ReadAlarmStateAsync(fixture.RuntimeEpoch);
        Assert.True(alarmState.Available, alarmState.ReasonCode);
        var alarmPage = await new SqliteAlarmHistoryQuery(fixture.Options)
            .QueryAsync(new AlarmHistoryFilter(pageSize: 1));
        Assert.True(alarmPage.Available, alarmPage.ReasonCode);
        Assert.Single(alarmPage.Records);
    }

    [Fact]
    public async Task V114_S11_Schema7ReadOnlyQueriesRejectArchiveConfigurationWithoutMutation()
    {
        RequireWindows();
        var alarmPolicy = new AlarmPolicy("V114Schema7Alarm", "1", new[]
        {
            new AlarmPolicyRule("V114-CODE", "V114-SOURCE", AlarmSeverity.Warning,
                ProductionImpact.None, false, AlarmNotification.None, null)
        }, TimeSpan.FromMinutes(1), maximumActiveInstances: 4, maximumPlcEntries: 1);
        await using var fixture = await ArchiveFixture.CreateAsync(
            archive: null, enableArchive: false, alarmPolicy: alarmPolicy);
        await fixture.DisposeStoreAsync();

        var archiveOptions = new AlgorithmResultArchiveOptions();
        var queryOptions = new ProductionStoreOptions(fixture.DatabasePath)
        {
            AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy,
            LocalIdentity = fixture.Options.LocalIdentity,
            AlarmPolicy = fixture.Options.AlarmPolicy,
            AlgorithmResultArchive = archiveOptions,
            CommitTimeout = fixture.Options.CommitTimeout,
            QueryTimeout = fixture.Options.QueryTimeout,
            QueueCapacity = fixture.Options.QueueCapacity
        };
        var before = SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath));

        var alarmQuery = new SqliteAlarmHistoryQuery(queryOptions);
        var alarmFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            alarmQuery.QueryAsync(new AlarmHistoryFilter()).AsTask());
        Assert.Equal("AlgorithmResultArchiveGovernedMigrationRequired", alarmFailure.Message);

        var traceQuery = new SqliteCommandTraceQuery(queryOptions);
        var traceFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            traceQuery.QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("AlgorithmResultArchiveGovernedMigrationRequired", traceFailure.Message);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath)));
        using var connection = fixture.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, Convert.ToInt64(command.ExecuteScalar()));
    }

    private static AlgorithmResultArchiveDocument TestDocument(Guid? recordId = null,
        ExecutionCorrelationId? correlation = null, InspectionDecision decision = InspectionDecision.Pass)
    {
        var actualCorrelation = correlation ?? new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var outcome = TestOutcome(actualCorrelation, decision);
        Assert.True(AlgorithmResultStorageCodec.TryEncode(recordId ?? Guid.NewGuid(), outcome,
            out var document, out var reason), reason);
        return document!;
    }

    private static AlgorithmExecutionOutcome TestOutcome(ExecutionCorrelationId correlation,
        InspectionDecision decision = InspectionDecision.Pass)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("V114Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var overlayContract = new OverlayContract("V114Overlay", "1");
        var resultSchema = new AlgorithmResultSchema("V114Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlayContract);
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("V114Algorithm", "1"),
            configurationSchema, resultSchema);
        var prepared = new PreparedAlgorithm(descriptor, configuration, new NoopAlgorithm(),
            (_, _) => Task.CompletedTask);
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 2, 1), VisionPixelFormat.Mono8,
            null, 1000, 0, null);
        var frame = new FrameMetadata(correlation, "TopCamera", 2, 1, 2,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
        var result = new AlgorithmResult(decision, decision == InspectionDecision.Unknown ? "Unknown" : null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlayContract));
        var timing = new AlgorithmExecutionTimingSnapshot(
            new RecipeReference("V114Recipe", "1", new string('A', 64)),
            "V114Policy", "1", new string('B', 64), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return new AlgorithmExecutionOutcome(prepared, frame, ExecutionStatus.Success, null,
            result, timing, 1);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema 8 audit storage uses Windows machine protection.");
    }

    private sealed class NoopAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ArchiveFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _policy;
        private readonly AlgorithmResultArchiveOptions? _archive;

        private ArchiveFixture(string directory, ProductionStoreOptions options,
            AuditIntegrityPolicy policy, AlgorithmResultArchiveOptions? archive)
        {
            _directory = directory; Options = options; _policy = policy; _archive = archive;
        }

        public ProductionStoreOptions Options { get; private set; }
        public SqliteCommandStore? Store { get; private set; }
        public string DatabasePath => Options.DatabasePath;
        public Guid RuntimeEpoch { get; } = Guid.NewGuid();

        public static async Task<ArchiveFixture> CreateAsync(AlgorithmResultArchiveOptions? archive = null,
            bool enableArchive = true, int? maximumVerificationEntries = null,
            AlarmPolicy? alarmPolicy = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V114-Archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var verificationBudget = maximumVerificationEntries ?? 10_000;
            var compactBudget = maximumVerificationEntries is not null;
            var policy = new AuditIntegrityPolicy("V114ArchiveStation", "v1",
                "SharpInspect.Test.V114." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = compactBudget ? 1 : 2,
                MaximumVerificationEntries = verificationBudget,
                BackgroundVerificationEntries = compactBudget ? 1 : 200,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(policy.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v114-blocklist", "v1",
                        new[] { "known-compromised" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "archive.sqlite"))
            {
                AuditIntegrityPolicy = policy,
                LocalIdentity = identity,
                AlarmPolicy = alarmPolicy,
                AlgorithmResultArchive = enableArchive ? archive ?? new AlgorithmResultArchiveOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(3),
                QueryTimeout = TimeSpan.FromSeconds(3),
                QueueCapacity = 8
            };
            var fixture = new ArchiveFixture(directory, options, policy,
                options.AlgorithmResultArchive);
            try
            {
                fixture.Store = new SqliteCommandStore(options);
                var initialized = await fixture.Store.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await fixture.WaitForVerifiedAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task ReopenAsync(AlgorithmResultArchiveOptions? archive = null)
        {
            var effective = archive ?? _archive;
            Options = new ProductionStoreOptions(Options.DatabasePath)
            {
                AuditIntegrityPolicy = Options.AuditIntegrityPolicy,
                LocalIdentity = Options.LocalIdentity,
                AlarmPolicy = Options.AlarmPolicy,
                ExternalAuditAnchor = Options.ExternalAuditAnchor,
                AlgorithmResultArchive = effective,
                CommitTimeout = Options.CommitTimeout,
                QueryTimeout = Options.QueryTimeout,
                QueueCapacity = Options.QueueCapacity
            };
            Store = new SqliteCommandStore(Options);
            await Task.Yield();
        }

        public async Task DisposeStoreAsync()
        {
            if (Store is not null)
            {
                await Store.DisposeAsync();
                Store = null;
            }
        }

        public ValueTask<StoreWriteResult> AppendAsync(AlgorithmResultArchiveDocument document)
        {
            Assert.NotNull(Store);
            return Store!.AppendAlgorithmResultAsync(document,
                new StoreDeadline(TimeSpan.FromSeconds(3)));
        }

        public async Task WaitForVerifiedAsync()
        {
            Assert.NotNull(Store);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (Store!.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (Store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException($"Audit integrity did not become Verified: {Store!.Integrity?.ReasonCode}");
        }

        public long Scalar(string sql)
        {
            using var connection = Open(readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public SqliteConnection Open(bool readOnly)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeStoreAsync();
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(_policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

#pragma warning restore CA1416
