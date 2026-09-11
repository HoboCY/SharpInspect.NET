using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Storage acceptance for the schema 9 draft ledger. These tests deliberately use the
/// real writer, identity provider, interactive session and authorization boundary; the
/// codec-only and service-only contracts live in their respective focused test files.
/// </summary>
public sealed class RecipeDraftStorageTests
{
    [Fact]
    public async Task V115_D01_RealWriterPersistsCasHistoryAndReadOnlyRestartWithoutCommandFacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = Guid.NewGuid();
        var commandFactsBefore = fixture.Scalar("SELECT COUNT(*) FROM command_facts;");
        var firstDocument = fixture.Document("第一版");
        var first = await fixture.SaveAsync(Guid.NewGuid(), draftId, 0, null, firstDocument, "initial");
        Assert.True(first.Saved, first.ReasonCode);

        await fixture.WaitForVerifiedAsync();
        var secondDocument = fixture.Document("第二版");
        var second = await fixture.SaveAsync(Guid.NewGuid(), draftId, 1,
            first.Revision!.RevisionContentHash, secondDocument, "adjust");
        Assert.True(second.Saved, second.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var query = new SqliteRecipeDraftQuery(fixture.Options);
        var firstPage = await query.QueryAsync(new RecipeDraftFilter(PageSize: 1));
        Assert.True(firstPage.Available, firstPage.ReasonCode);
        Assert.Single(firstPage.Revisions);
        Assert.NotNull(firstPage.NextAfterPosition);
        var secondPage = await query.QueryAsync(new RecipeDraftFilter(
            AfterPosition: firstPage.NextAfterPosition!.Value, PageSize: 1));
        Assert.True(secondPage.Available, secondPage.ReasonCode);
        var persisted = Assert.Single(secondPage.Revisions);
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(firstPage.Revisions[0].RevisionContentHash,
            persisted.PreviousRevisionContentHash);
        Assert.Equal(commandFactsBefore, fixture.Scalar("SELECT COUNT(*) FROM command_facts;"));

        await fixture.RestartStoreAsync();
        var restarted = await new SqliteRecipeDraftQuery(fixture.Options).QueryAsync(
            new RecipeDraftFilter(PageSize: 20));
        Assert.True(restarted.Available, restarted.ReasonCode);
        Assert.Equal(2, restarted.Revisions.Count);
        Assert.Equal(persisted.RevisionContentHash,
            restarted.Revisions[1].RevisionContentHash);
    }

    [Fact]
    public async Task V115_D02_CasComparesFullRevisionHashAcrossAbaContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = Guid.NewGuid();
        var firstDocument = fixture.Document("A");
        var first = await fixture.SaveAsync(Guid.NewGuid(), draftId, 0, null, firstDocument, "A");
        Assert.True(first.Saved, first.ReasonCode);

        var secondDocument = fixture.Document("B");
        var second = await fixture.SaveAsync(Guid.NewGuid(), draftId, 1,
            first.Revision!.RevisionContentHash, secondDocument, "B");
        Assert.True(second.Saved, second.ReasonCode);

        // Return to the same content after B. The revision identity still changes, so
        // an old content hash cannot be used as an ABA substitute for the current head.
        var third = await fixture.SaveAsync(Guid.NewGuid(), draftId, 2,
            second.Revision!.RevisionContentHash, firstDocument, "A again");
        Assert.True(third.Saved, third.ReasonCode);

        var stale = await fixture.SaveAsync(Guid.NewGuid(), draftId, 3,
            first.Revision!.RevisionContentHash, secondDocument, "stale A hash");
        Assert.False(stale.Saved);
        Assert.Equal("RecipeDraftRevisionContentConflict", stale.ReasonCode);
        Assert.Equal(3, fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V115_D03_OperationReplayIsExactAndDoesNotAppend()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var document = fixture.Document("exact replay");
        var request = fixture.Request(operationId, draftId, 0, null, document, "one");
        var first = await fixture.SaveAsync(request, document);
        Assert.True(first.Saved, first.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var before = fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");

        var replay = await fixture.SaveAsync(request, document);
        Assert.True(replay.Saved, replay.ReasonCode);
        Assert.Equal("RecipeDraftAlreadyPersisted", replay.ReasonCode);
        Assert.Equal(first.Revision!.RevisionContentHash, replay.Revision!.RevisionContentHash);
        Assert.Equal(before, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));

        var changedTarget = fixture.Request(operationId, Guid.NewGuid(), 0, null, document, "one");
        var targetConflict = await fixture.SaveAsync(changedTarget, document);
        Assert.False(targetConflict.Saved);
        Assert.Equal("RecipeDraftOperationConflict", targetConflict.ReasonCode);

        var changedReason = fixture.Request(operationId, draftId, 0, null, document, "changed");
        var reasonConflict = await fixture.SaveAsync(changedReason, document);
        Assert.False(reasonConflict.Saved);
        Assert.Equal("RecipeDraftOperationConflict", reasonConflict.ReasonCode);
        Assert.Equal(before, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));

        var locked = await fixture.Sessions.LockAsync(fixture.Sessions.Current.SessionId,
            SessionLockReason.UserRequested);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        var lockedReplay = await fixture.SaveAsync(request, document);
        Assert.False(lockedReplay.Saved);
        Assert.Contains(lockedReplay.ReasonCode,
            new[] { "SessionLocked", "SessionMismatch", "AuthenticationRequired" });
        Assert.Equal(before, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V115_D04_ConsumedStepUpReplayUsesDurableSignedAuthorization()
    {
        await using var fixture = await Fixture.CreateAsync(requireStepUp: true);
        var operationId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var initialInvocation = fixture.Invocation();
        var binding = new StepUpBinding(Permission.EditRecipeDraft, operationId,
            draftId.ToString("D"), AuditedCommandKind.SaveRecipeDraft);
        var issued = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            operationId, initialInvocation, binding, fixture.Password));
        Assert.True(issued.Succeeded, issued.ReasonCode);

        var document = fixture.Document("step-up draft");
        var request = fixture.Request(operationId, draftId, 0, null, document, "step-up",
            issued.GrantId);
        var first = await fixture.SaveAsync(request, document);
        Assert.True(first.Saved, first.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        // Issuing another grant purges the consumed in-memory grant. The durable
        // RecipeDraftSaved event must still authorize an exact replay.
        var purgeOperation = Guid.NewGuid();
        var purgeDraft = Guid.NewGuid();
        var purge = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            purgeOperation, fixture.Invocation(), new StepUpBinding(Permission.EditRecipeDraft,
                purgeOperation, purgeDraft.ToString("D"), AuditedCommandKind.SaveRecipeDraft), fixture.Password));
        Assert.True(purge.Succeeded, purge.ReasonCode);

        var replay = await fixture.SaveAsync(request, document);
        Assert.True(replay.Saved, replay.ReasonCode);
        Assert.Equal("RecipeDraftAlreadyPersisted", replay.ReasonCode);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V115_D05_CancelledSaveDoesNotReachTheWriter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cancelledDocument = fixture.Document("cancelled");
        var cancelledRequest = fixture.Request(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            cancelledDocument, "cancelled");
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        var pending = fixture.SaveAsync(cancelledRequest, cancelledDocument, cancellation.Token).AsTask();
        await Task.Yield();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await using (var commit = connection.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }

        var cancelled = await pending;
        Assert.False(cancelled.Saved);
        Assert.Equal("RecipeDraftSaveCancelled", cancelled.ReasonCode);

        var normalDocument = fixture.Document("after cancel");
        var normal = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            normalDocument, "normal");
        Assert.True(normal.Saved, normal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM recipe_draft_revisions WHERE OperationId='" +
            cancelledRequest.OperationId.ToString("D") + "';"));
    }

    [Fact]
    public async Task V115_D06_Schema9DraftAndArchiveOptionsShareOneSignedStore()
    {
        await using var fixture = await Fixture.CreateAsync(enableArchive: true);
        var initialized = fixture.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        Assert.Equal(9, await fixture.ScalarAsync("PRAGMA user_version;"));

        var draftPage = await new SqliteRecipeDraftQuery(fixture.Options).QueryAsync(
            new RecipeDraftFilter(PageSize: 20));
        Assert.True(draftPage.Available, draftPage.ReasonCode);
        var saved = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("archive coexist"), "archive coexist");
        Assert.True(saved.Saved, saved.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        draftPage = await new SqliteRecipeDraftQuery(fixture.Options).QueryAsync(
            new RecipeDraftFilter(PageSize: 20));
        Assert.True(draftPage.Available, draftPage.ReasonCode);
        Assert.Single(draftPage.Revisions);
        var archivePage = await new SqliteAlgorithmResultQuery(fixture.Options).QueryAsync(
            new AlgorithmResultFilter(PageSize: 20));
        Assert.True(archivePage.Available, archivePage.ReasonCode);
        Assert.Empty(archivePage.Records);

        await fixture.RestartStoreAsync();
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
        var restartedArchivePage = await new SqliteAlgorithmResultQuery(fixture.Options).QueryAsync(
            new AlgorithmResultFilter(PageSize: 20));
        Assert.True(restartedArchivePage.Available, restartedArchivePage.ReasonCode);
        Assert.Empty(restartedArchivePage.Records);
    }

    [Fact]
    public async Task V115_D07_EnablingDraftsOverSchema7Or8RequiresGovernedMigration()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Recipe Draft storage requires Windows machine key protection.");

        foreach (var enableArchive in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V115-DraftMigration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V115DraftMigrationStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V115.Migration." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v115-migration-blocklist", "v1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var path = Path.Combine(directory, "legacy.sqlite");
            var legacyOptions = new ProductionStoreOptions(path)
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identity,
                AlgorithmResultArchive = enableArchive ? new AlgorithmResultArchiveOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4), QueryTimeout = TimeSpan.FromSeconds(4)
            };
            try
            {
                await using (var legacy = new SqliteCommandStore(legacyOptions))
                {
                    var initialized = await legacy.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.True(initialized.Committed, initialized.ReasonCode);
                    await Fixture.WaitForVerifiedAsync(legacy);
                }
                var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                var execution = new AlgorithmExecutionPolicy("V115.Draft.Execution", "1",
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
                var governed = new ProductionStoreOptions(path)
                {
                    AuditIntegrityPolicy = audit,
                    LocalIdentity = identity,
                    RecipeDrafts = new RecipeDraftStoreOptions(execution),
                    CommitTimeout = TimeSpan.FromSeconds(4), QueryTimeout = TimeSpan.FromSeconds(4)
                };
                await using (var rejected = new SqliteCommandStore(governed))
                {
                    var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.False(result.Committed);
                    Assert.Equal("RecipeDraftGovernedMigrationRequired", result.ReasonCode);
                    var after = await File.ReadAllBytesAsync(path);
                    Assert.Equal(before, Convert.ToHexString(SHA256.HashData(after)));
                }
            }
            finally
            {
                Fixture.DeleteDirectoryForTest(directory, audit);
            }
        }
    }

    [Fact]
    public async Task V115_D08_PreDraftDevelopmentPolicyReopensSchema7And8()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Recipe Draft storage requires Windows machine key protection.");

        // This is the hash of the pre-draft Development policy.  Adding a new
        // Permission enum value must not silently retarget existing identity
        // stores that still use the same policy Id and Version.
        Assert.Equal("D7202FE18DE53788D5CC9A8E898F796E345CB5B2EBB265E5615C669282825C50",
            AuthorizationPolicy.Development.ContentHash);
        Assert.DoesNotContain(Permission.EditRecipeDraft,
            AuthorizationPolicy.Development.GetPermissions(HumanRoleBundle.Technician));
        Assert.DoesNotContain(Permission.EditRecipeDraft,
            AuthorizationPolicy.Development.GetPermissions(HumanRoleBundle.Administrator));

        foreach (var enableArchive in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V115-DevelopmentPolicyReopen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V115DevelopmentReopenStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V115.DevelopmentReopen." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v115-development-reopen-blocklist", "v1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "legacy.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identity,
                AlgorithmResultArchive = enableArchive ? new AlgorithmResultArchiveOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4),
                QueryTimeout = TimeSpan.FromSeconds(4)
            };

            try
            {
                await using (var first = new SqliteCommandStore(options))
                {
                    var initialized = await first.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.True(initialized.Committed, initialized.ReasonCode);
                    await Fixture.WaitForVerifiedAsync(first);
                    var state = await first.ReadIdentityAsync(CancellationToken.None);
                    Assert.Equal(identity.PolicyContentHash, state.PolicyContentHash);
                    Assert.Equal(enableArchive ? 8 : 7,
                        await ScalarAsync(options.DatabasePath, "PRAGMA user_version;"));

                    // Exercise a real identity mutation before the restart so
                    // the reopen path verifies protected state plus its audit
                    // event, rather than only an untouched bootstrap state.
                    var identityService = new LocalIdentityService(first, identity,
                        new Fixture.FixtureConsole());
                    var tokenResult = await identityService.ProvisionBootstrapTokenAsync();
                    Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
                    var bootstrapToken = tokenResult.Token!.TakeForDisplay();
                    tokenResult.Token.Dispose();
                    const string userName = "v115.development.reopen";
                    const string password = "V115 development reopen password 2026!";
                    var created = await identityService.CreateFirstAdministratorAsync(
                        new BootstrapAdministratorRequest(station, bootstrapToken, userName,
                            "V115 Development Reopen Administrator", password));
                    Assert.True(created.Succeeded, created.ReasonCode);
                    created.RecoveryKit?.Dispose();
                    await Fixture.WaitForVerifiedAsync(first);
                }

                await using (var reopened = new SqliteCommandStore(options))
                {
                    var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.True(initialized.Committed, initialized.ReasonCode);
                    await Fixture.WaitForVerifiedAsync(reopened);
                    var identityService = new LocalIdentityService(reopened, identity,
                        new Fixture.FixtureConsole());
                    await using (var sessions = new InteractiveSessionService(identityService,
                        identity.AuthenticationPolicy, identityService.PersistSessionEventAsync))
                    {
                        var login = await sessions.SignInAsync(new PasswordSignInRequest(
                            "v115.development.reopen", "V115 development reopen password 2026!"));
                        Assert.True(login.Succeeded, login.ReasonCode);
                        Assert.NotNull(login.Identity);
                        var locked = await sessions.LockAsync(login.Session.SessionId,
                            SessionLockReason.UserRequested);
                        Assert.True(locked.Succeeded, locked.ReasonCode);
                        await Fixture.WaitForVerifiedAsync(reopened);
                    }

                    var state = await reopened.ReadIdentityAsync(CancellationToken.None);
                    Assert.Equal(identity.PolicyContentHash, state.PolicyContentHash);
                    Assert.NotNull(state.Administrator);
                }
            }
            finally
            {
                Fixture.DeleteDirectoryForTest(directory, audit);
            }
        }
    }

    [Fact]
    public async Task V115_D09_TamperedHistoryFaultsWriterButMalformedCallerDoesNotLatch()
    {
        await using (var fixture = await Fixture.CreateAsync(
            verificationInterval: TimeSpan.FromSeconds(30)))
        {
            var first = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                fixture.Document("before tamper"), "initial");
            Assert.True(first.Saved, first.ReasonCode);
            await fixture.WaitForVerifiedAsync();
            var revisionsBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;");
            var auditBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");

            // Keep the replacement payload internally valid, but leave the
            // signed RecipeDraftRevision binding unchanged. This exercises the
            // writer's persisted-history verification phase rather than caller
            // document admission.
            await TamperFirstDraftPayloadAndBindingAsync(fixture.Options.DatabasePath,
                fixture.Document("tampered persisted payload"));

            var rejected = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                fixture.Document("after tamper"), "must reject tampered history");
            Assert.False(rejected.Saved);
            Assert.Equal("RecipeDraftBindingMismatch", rejected.ReasonCode);
            Assert.Equal(revisionsBefore,
                await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
            Assert.Equal(auditBefore,
                await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
            Assert.Equal(AuditIntegrityState.Faulted, fixture.Store.Integrity?.State);
            Assert.Equal("RecipeDraftBindingMismatch", fixture.Store.Integrity?.ReasonCode);
        }

        await using (var fixture = await Fixture.CreateAsync(
            verificationInterval: TimeSpan.FromSeconds(30)))
        {
            var valid = fixture.Document("valid caller content");
            var malformed = new RecipeDraftDocument(valid.Content, valid.PayloadJson + " ", valid.PayloadHash);
            var rejected = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                malformed, "malformed caller payload");
            Assert.False(rejected.Saved);
            Assert.Equal("RecipeDraftPayloadHashMismatch", rejected.ReasonCode);
            Assert.NotEqual(AuditIntegrityState.Faulted, fixture.Store.Integrity?.State);
            Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        }
    }

    internal static async Task TamperFirstDraftPayloadAndBindingAsync(string databasePath,
        RecipeDraftDocument replacement)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();

        await using var triggerLookup = connection.CreateCommand();
        triggerLookup.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='recipe_draft_revision_immutable_update';";
        var trigger = (string?)await triggerLookup.ExecuteScalarAsync();
        Assert.False(string.IsNullOrWhiteSpace(trigger));

        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER recipe_draft_revision_immutable_update; " +
            "UPDATE recipe_draft_revisions SET PayloadJson=$payload,PayloadHash=$hash WHERE Position=1; " +
            trigger;
        command.Parameters.AddWithValue("$payload", replacement.PayloadJson);
        command.Parameters.AddWithValue("$hash", replacement.PayloadHash);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _auditPolicy;
        private bool _disposed;

        private Fixture(string directory, ProductionStoreOptions options,
            AuditIntegrityPolicy auditPolicy, LocalIdentityOptions identityOptions,
            SqliteCommandStore store, LocalIdentityService identity,
            InteractiveSessionService sessions, LocalAuthorizationService authorization,
            string userName, string password)
        {
            _directory = directory; Options = options; _auditPolicy = auditPolicy;
            IdentityOptions = identityOptions; Store = store; Identity = identity;
            Sessions = sessions; Authorization = authorization; UserName = userName;
            Password = password;
        }

        internal ProductionStoreOptions Options { get; private set; }
        internal LocalIdentityOptions IdentityOptions { get; }
        internal SqliteCommandStore Store { get; private set; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal string UserName { get; }
        internal string Password { get; }
        internal StoreWriteResult Initialization => Store.Initialization.GetAwaiter().GetResult();

        internal static async Task<Fixture> CreateAsync(bool requireStepUp = false,
            bool enableArchive = false, int? maximumRevisionCount = null,
            TimeSpan? verificationInterval = null, RecipeReleaseStoreOptions? recipeReleases = null,
            AlarmPolicy? alarmPolicy = null, PlcResultContractStoreOptions? plcResultContracts = null,
            IExternalAuditAnchor? externalAuditAnchor = null, bool requireExternalAnchor = false,
            AuthorizationPolicy? authorizationPolicy = null, CameraSetupStoreOptions? cameraSetup = null,
            RecipeActivationStoreOptions? recipeActivations = null, PreviewSessionStoreOptions? previewSessions = null,
            ManualInspectionStoreOptions? manualInspections = null, int? maximumAuditEntries = null,
            ProductionAdmissionStoreOptions? productionAdmission = null,
            RecipeTransferStoreOptions? recipeTransfers = null,
             TraceStoragePolicyStoreOptions? traceStoragePolicies = null,
             PlcCommunicationStoreOptions? plcCommunication = null,
             ProductionInspectionStoreOptions? productionInspections = null,
             PartIdentityStoreOptions? partIdentities = null,
             ProductionRecoveryStoreOptions? productionRecovery = null,
             RecipeSelectionStoreOptions? recipeSelections = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Recipe Draft storage requires Windows machine key protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V115-DraftStorage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V115DraftStorageStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V115.Draft." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                MaximumVerificationEntries = maximumAuditEntries ?? 10_000,
                VerificationInterval = verificationInterval ?? TimeSpan.FromSeconds(1),
                RequireExternalAnchor = requireExternalAnchor,
                ExternalAnchorRouteId = requireExternalAnchor ? "v131-query-anchor" : null,
                AnchorTimeout = TimeSpan.FromSeconds(2)
            };
            var passwordPolicy = new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v115-draft-blocklist", "v1",
                    new[] { "known-compromised-value" })
            };
            var identityOptions = new LocalIdentityOptions(station, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                authorizationPolicy ?? RecipeDraftTestPolicies.Authoring);
            var execution = new AlgorithmExecutionPolicy("V115.Draft.Execution", "1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
            var draftOptions = new RecipeDraftStoreOptions(execution)
            {
                RequireStepUp = requireStepUp,
                MaximumRevisionCount = maximumRevisionCount ?? 10_000
            };
            var options = new ProductionStoreOptions(Path.Combine(directory, "drafts.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                AlarmPolicy = alarmPolicy,
                ExternalAuditAnchor = externalAuditAnchor,
                RecipeDrafts = draftOptions,
                RecipeReleases = recipeReleases,
                RecipeTransfers = recipeTransfers,
                TraceStoragePolicies = traceStoragePolicies,
                PlcResultContracts = plcResultContracts,
                CameraSetup = cameraSetup,
                RecipeActivations = recipeActivations,
                PreviewSessions = previewSessions,
                ManualInspections = manualInspections,
                ProductionAdmission = productionAdmission,
                PlcCommunication = plcCommunication,
                ProductionInspections = productionInspections,
                PartIdentities = partIdentities,
                ProductionRecovery = productionRecovery,
                RecipeSelections = recipeSelections,
                AlgorithmResultArchive = enableArchive ? new AlgorithmResultArchiveOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4), QueryTimeout = TimeSpan.FromSeconds(4), QueueCapacity = 8
            };

            SqliteCommandStore? store = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store, allowTransientAnchorPending: requireExternalAnchor);

                var identity = new LocalIdentityService(store, identityOptions, new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var bootstrap = token.Token!.TakeForDisplay();
                token.Token.Dispose();
                await WaitForVerifiedAsync(store, allowTransientAnchorPending: requireExternalAnchor);
                const string userName = "draft.storage.admin";
                const string password = "V115 draft storage password 2026!";
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(station, bootstrap, userName,
                        "Draft Storage Administrator", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store, allowTransientAnchorPending: requireExternalAnchor);

                var sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                var login = await sessions.SignInAsync(new PasswordSignInRequest(userName, password));
                Assert.True(login.Succeeded, login.ReasonCode);
                var authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);
                await WaitForVerifiedAsync(store, allowTransientAnchorPending: requireExternalAnchor);
                return new Fixture(directory, options, audit, identityOptions, store,
                    identity, sessions, authorization, userName, password);
            }
            catch
            {
                if (store is not null) await store.DisposeAsync();
                DeleteDirectory(directory, audit);
                throw;
            }
        }

        internal RecipeDraftDocument Document(string displayName)
        {
            var schema = new AlgorithmConfigurationSchema("V115.Draft.Config", "1", new[]
            {
                new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "items", true,
                    new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                    AlgorithmScalarValue.FromInt64(5))
            });
            var configuration = AlgorithmConfigurationSnapshot.Create(schema, new[]
            {
                new AlgorithmConfigurationEntry("Threshold", "items", AlgorithmScalarValue.FromInt64(5))
            });
            var overlay = new OverlayContract("V115.Draft.Overlay", "1", 0, 64, 16);
            var result = new AlgorithmResultSchema("V115.Draft.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" }, overlay);
            var binding = new RecipeAlgorithmBinding(new AlgorithmIdentity("V115.Draft.Algorithm", "1"),
                schema, new RecipeContractReference(result.Id, result.Version, result.ContentHash),
                new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
            var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                100, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);
            var requirement = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference("V115.Draft.Execution", "1",
                    Options.RecipeDrafts!.ExecutionPolicy.ContentHash));
            var content = new RecipeDraftContent("V115.Draft.Recipe", displayName, binding, configuration,
                "TopCamera", camera, TimeSpan.FromMilliseconds(100), null, new[] { requirement });
            Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
            return document!;
        }

        internal CommandInvocation Invocation(Guid? stepUpGrantId = null) =>
            new(CommandSource.Integration, GetPrincipalId().ToString("D"), Sessions.Current.SessionId,
                stepUpGrantId);

        internal RecipeDraftSaveRequest Request(Guid operationId, Guid draftId, long expectedRevision,
            string? expectedHash, RecipeDraftDocument document, string reason, Guid? grant = null) =>
            new(operationId, draftId, expectedRevision, expectedHash, document.Content, reason,
                Invocation(grant), grant);

        internal ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request,
            RecipeDraftDocument document, CancellationToken cancellationToken = default) =>
            Authorization.SaveRecipeDraftAsync(request, document,
                new StoreDeadline(TimeSpan.FromSeconds(8)), cancellationToken);

        internal ValueTask<RecipeDraftSaveResult> SaveAsync(Guid operationId, Guid draftId,
            long expectedRevision, string? expectedHash, RecipeDraftDocument document, string reason,
            Guid? grant = null, CancellationToken cancellationToken = default) =>
            SaveAsync(Request(operationId, draftId, expectedRevision, expectedHash, document, reason, grant),
                document, cancellationToken);

        private Guid GetPrincipalId() => Guid.Parse(Sessions.Current.PrincipalId!);

        internal async Task RestartStoreAsync()
        {
            await Store.DisposeAsync();
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(Store);
        }

        internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

        internal long Scalar(string sql) => ScalarAsync(sql).GetAwaiter().GetResult();

        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Authorization.Dispose();
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
            DeleteDirectory(_directory, _auditPolicy);
        }

        internal static async Task WaitForVerifiedAsync(SqliteCommandStore store,
            bool allowTransientAnchorPending = false)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                {
                    // A required-anchor store briefly publishes this advisory state after a
                    // signed append and before the background delivery/receipt commit.  Keep
                    // waiting only for that known transient; every other fault remains fatal.
                    if (!allowTransientAnchorPending || fault.ReasonCode != "AuditRequiredAnchorPending")
                        throw new XunitException(fault.ReasonCode);
                }
                await Task.Delay(25);
            }
            throw new XunitException("Audit integrity did not become Verified: " + store.Integrity?.ReasonCode);
        }

        private static void DeleteDirectory(string directory, AuditIntegrityPolicy policy)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void DeleteDirectoryForTest(string directory, AuditIntegrityPolicy policy) =>
            DeleteDirectory(directory, policy);

        internal sealed class FixtureConsole : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V115-DRAFT");
        }

    }
}
