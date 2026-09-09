using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runtime-level checks for the schema-17 PLC result-contract authority.  The
/// fixture uses the real SQLite writer, identity session and StationRuntime;
/// no contract revision is inserted by a codec or by direct SQL.
/// </summary>
public sealed class PlcResultContractGovernanceTests
{
    [Fact]
    public async Task V131_G09_MultipleSchemaReasonUnionBindsAndTrueExtrasAreRejected()
    {
        await using var harness = await Harness.CreateAsync(includeAlternateSchema: true);
        await harness.SaveAndReleaseAsync("multi-schema reason union");
        var first = BuildContract(harness.PrimarySchema, "1");
        var second = BuildContract(harness.AlternateSchema!, "1");
        var reasons = first.FrameworkFields.Single(value => value.Field == PlcFrameworkResultField.ResultReasonCode)
            .ReasonCodes.Select(value => value.ReasonCode)
            .Concat(second.FrameworkFields.Single(value => value.Field == PlcFrameworkResultField.ResultReasonCode)
                .ReasonCodes.Select(value => value.ReasonCode)).Distinct(StringComparer.Ordinal).ToArray();
        PlcResultContract Proposal(string version, IEnumerable<string?> values) => new(first.Id, version,
            first.MaximumPayloadBytes, first.MaximumRegisterCount, first.FrameworkFields.Select(field =>
                field.Field == PlcFrameworkResultField.ResultReasonCode
                    ? new PlcFrameworkFieldMapping(field.Field, field.RegisterRange, field.Encoding,
                        reasonCodes: values.Select((reason, index) => new PlcReasonCode(reason, index))) : field),
            first.SchemaMaps.Concat(second.SchemaMaps));
        var contract = Proposal("1", reasons);
        var binder = new PlcResultContractBinder();
        Assert.True(binder.ValidateSchema(contract, harness.PrimarySchema).Valid);
        Assert.True(binder.ValidateSchema(contract, harness.AlternateSchema!).Valid);
        Assert.False(binder.ValidateSchema(Proposal("duplicate", reasons.Append(reasons[0])),
            harness.PrimarySchema).Valid);
        var outcome = await harness.Runtime.SubmitAsync(await harness.AuthorizeChangeAsync(
            harness.Change(contract, null, "two different schema reason catalogs")));
        Assert.True(outcome.Disposition == CommandDisposition.Accepted, outcome.ReasonCode);
        var read = await harness.Contracts.ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(2, read.Revision!.SchemaValidations.Count);
        Assert.Single(read.Revision.Bindings);
        await harness.SubmitRejectedAsync(harness.Change(Proposal("2", reasons.Append("UndeclaredReason")),
            read.Revision.Reference, "extra global reason must fail"), "PlcResultContractGlobalReasonCatalogMismatch");
    }

    [Fact]
    public async Task V131_G01_VersionChangesBindEveryReleasedRecipeAndSurviveColdRead()
    {
        await using var harness = await Harness.CreateAsync();
        var firstRelease = await harness.SaveAndReleaseAsync("first released contract recipe");
        var secondRelease = await harness.SaveAndReleaseAsync("second released contract recipe");
        var before = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, before.ArmState);
        Assert.False(before.Ready);

        var firstContract = BuildContract(harness.PrimarySchema, "1");
        var firstCommand = await harness.AuthorizeChangeAsync(
            harness.Change(firstContract, null, "V131-G01 first contract"));
        var firstOutcome = await harness.Runtime.SubmitAsync(firstCommand);
        Assert.Equal(CommandDisposition.Accepted, firstOutcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, firstOutcome.Audit);

        var firstRead = await harness.Contracts.ReadCurrentAsync();
        Assert.True(firstRead.Available, firstRead.ReasonCode);
        var firstRevision = Assert.IsType<PlcResultContractRevision>(firstRead.Revision);
        Assert.Equal(firstContract.ContentHash, firstRevision.Contract.ContentHash);
        Assert.Equal(2, firstRevision.Bindings.Count);
        Assert.Equal(2, firstRevision.ReleaseHighWatermark);
        Assert.Contains(firstRevision.Bindings,
            value => value.ReleaseId == firstRelease.Record.ReleaseId &&
                value.ReleaseRecordContentHash == firstRelease.Record.ContentHash);
        Assert.Contains(firstRevision.Bindings,
            value => value.ReleaseId == secondRelease.Record.ReleaseId &&
                value.ReleaseRecordContentHash == secondRelease.Record.ContentHash);

        var secondContract = BuildContract(harness.PrimarySchema, "2", 512);
        var secondCommand = await harness.AuthorizeChangeAsync(
            harness.Change(secondContract, firstRevision.Reference, "V131-G01 second contract"));
        var secondOutcome = await harness.Runtime.SubmitAsync(secondCommand);
        Assert.Equal(CommandDisposition.Accepted, secondOutcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, secondOutcome.Audit);

        var secondRead = await harness.Contracts.ReadCurrentAsync();
        Assert.True(secondRead.Available, secondRead.ReasonCode);
        var secondRevision = Assert.IsType<PlcResultContractRevision>(secondRead.Revision);
        Assert.Equal(firstRevision.Reference, secondRevision.PreviousContract);
        Assert.Equal(secondContract.ContentHash, secondRevision.Contract.ContentHash);
        Assert.Equal(2, secondRevision.Bindings.Count);
        Assert.All(secondRevision.Bindings, value =>
            Assert.Equal(secondContract.ContentHash, value.Binding.Contract.ContentHash));

        await harness.StopCapabilitiesAsync();
        await harness.Storage.RestartStoreAsync();

        var coldContractQuery = new SqlitePlcResultContractQuery(harness.Storage.Options);
        var coldCurrent = await coldContractQuery.ReadCurrentAsync();
        Assert.True(coldCurrent.Available, coldCurrent.ReasonCode);
        Assert.Equal(secondRevision.ContentHash, coldCurrent.Revision!.ContentHash);
        var coldExact = await coldContractQuery.ReadAsync(secondRevision.Reference);
        Assert.True(coldExact.Available, coldExact.ReasonCode);
        Assert.Equal(secondRevision.ContentHash, coldExact.Revision!.ContentHash);
        var coldPage = await coldContractQuery.QueryAsync(new(PageSize: 20));
        Assert.True(coldPage.Available, coldPage.ReasonCode);
        Assert.Equal(2, coldPage.Revisions.Count);
        Assert.Null(coldPage.NextAfterPosition);

        var coldReleases = await new SqliteReleasedRecipeQuery(harness.Storage.Options)
            .QueryAsync(new(PageSize: 20));
        Assert.True(coldReleases.Available, coldReleases.ReasonCode);
        Assert.Equal(2, coldReleases.Recipes.Count);
        var integrity = await new SqliteAuditIntegrityQuery(harness.Storage.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        Assert.Equal(AuditIntegrityState.Verified, harness.Storage.Store.Integrity!.State);
    }

    [Fact]
    public async Task V131_G02_DuplicateVersionCasAndSchemaCoverageFailuresAreAudited()
    {
        await using var harness = await Harness.CreateAsync(includeAlternateSchema: true);
        await harness.SaveAndReleaseAsync("used schema recipe");

        var first = BuildContract(harness.PrimarySchema, "1");
        var firstCommand = await harness.AuthorizeChangeAsync(
            harness.Change(first, null, "V131-G02 baseline"));
        var firstOutcome = await harness.Runtime.SubmitAsync(firstCommand);
        Assert.Equal(CommandDisposition.Accepted, firstOutcome.Disposition);
        var currentRead = await harness.Contracts.ReadCurrentAsync();
        Assert.True(currentRead.Available, currentRead.ReasonCode);
        var current = Assert.IsType<PlcResultContractRevision>(currentRead.Revision);

        var duplicate = BuildContract(harness.PrimarySchema, "1", 512);
        await harness.SubmitRejectedAsync(
            await harness.AuthorizeChangeAsync(
                harness.Change(duplicate, current.Reference, "V131-G02 duplicate version")),
            "PlcResultContractVersionConflict");

        var stale = harness.Change(BuildContract(harness.PrimarySchema, "2"), null,
            "V131-G02 stale compare and swap");
        await harness.SubmitRejectedAsync(await harness.AuthorizeChangeAsync(stale),
            "PlcResultContractCurrentConflict");

        var omittedUsedSchema = harness.Change(
            BuildContract(harness.AlternateSchema!, "3"), current.Reference,
            "V131-G02 omit used schema");
        await harness.SubmitRejectedAsync(
            await harness.AuthorizeChangeAsync(omittedUsedSchema),
            "PlcResultContractSchemaMapMissingOrDuplicate");

        var unknownSchema = harness.Change(
            BuildContract(UnknownSchema(), "4"), current.Reference,
            "V131-G02 unknown schema");
        await harness.SubmitRejectedAsync(
            await harness.AuthorizeChangeAsync(unknownSchema),
            "PlcResultContractSchemaUnavailable");

        var after = await harness.Contracts.QueryAsync(new(PageSize: 20));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Single(after.Revisions);
        Assert.Equal(current.ContentHash, after.Revisions.Single().ContentHash);
    }

    [Fact]
    public async Task V131_G03_InvalidExpiredWrongIntentAndCancelledChangesNeverAppend()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SaveAndReleaseAsync("grant-bound recipe");
        var first = BuildContract(harness.PrimarySchema, "1");
        var firstOutcome = await harness.Runtime.SubmitAsync(
            await harness.AuthorizeChangeAsync(harness.Change(first, null, "V131-G03 baseline")));
        Assert.Equal(CommandDisposition.Accepted, firstOutcome.Disposition);
        var currentRead = await harness.Contracts.ReadCurrentAsync();
        Assert.True(currentRead.Available, currentRead.ReasonCode);
        var current = Assert.IsType<PlcResultContractRevision>(currentRead.Revision);

        var noGrant = harness.Change(BuildContract(harness.PrimarySchema, "2"), current.Reference,
            "V131-G03 no grant");
        await harness.SubmitRejectedAsync(noGrant, "StepUpRequired");

        var invalidGrant = harness.Change(BuildContract(harness.PrimarySchema, "3"), current.Reference,
            "V131-G03 invalid grant") with
        {
            Invocation = harness.Storage.Invocation(Guid.NewGuid())
        };
        await harness.SubmitRejectedAsync(invalidGrant, "StepUpInvalid");

        var intended = harness.Change(BuildContract(harness.PrimarySchema, "4"), current.Reference,
            "V131-G03 intended reason");
        var intendedGrant = await harness.AuthorizeChangeAsync(intended);
        var changedIntent = new ChangePlcResultContractCommand(intended.CorrelationId,
            harness.Storage.Invocation(intendedGrant.Invocation.StepUpGrantId), intended.Proposal,
            intended.ExpectedCurrent, "V131-G03 changed reason");
        await harness.SubmitRejectedAsync(changedIntent, "StepUpInvalid");

        var expired = harness.Change(BuildContract(harness.PrimarySchema, "5"), current.Reference,
            "V131-G03 expired grant");
        var expiredGrant = await harness.AuthorizeChangeAsync(expired);
        harness.Clock.AdvancePast(harness.Storage.IdentityOptions.AuthenticationPolicy.StepUpFreshness);
        await harness.SubmitRejectedAsync(expiredGrant, "StepUpInvalid");

        var cancelled = harness.Change(BuildContract(harness.PrimarySchema, "6"), current.Reference,
            "V131-G03 cancelled before admission");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await harness.SubmitRejectedAsync(cancelled, "PlcResultContractChangeCancelled", cancellation.Token);

        var after = await harness.Contracts.QueryAsync(new(PageSize: 20));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Single(after.Revisions);
        Assert.Equal(current.ContentHash, after.Revisions.Single().ContentHash);
    }

    [Fact]
    public async Task V131_G04_OperatorPermissionIsDeniedAndAudited()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SignInOperatorAsync();
        var access = await harness.Contracts.GetAccessAsync(harness.Storage.Invocation());
        Assert.False(access.CanChange);
        Assert.Equal("PermissionDenied", access.ReasonCode);

        var command = harness.Change(BuildContract(harness.PrimarySchema, "1"), null,
            "V131-G04 operator must not change contract");
        await harness.SubmitRejectedAsync(command, "PermissionDenied");
    }

    [Fact]
    public async Task V131_G05_RuntimeStoppedBlocksChangeWithoutCreatingRevision()
    {
        await using var harness = await Harness.CreateAsync();
        var command = harness.Change(BuildContract(harness.PrimarySchema, "1"), null,
            "V131-G05 stopped runtime");
        var authorized = await harness.AuthorizeChangeAsync(command);
        await harness.StopRuntimeAsync();
        var snapshot = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(RuntimeLifecycle.Stopped, snapshot.Lifecycle);
        await harness.SubmitRejectedAsync(authorized, "RuntimeStopped");
    }

    [Fact]
    public async Task V131_G08_ReleaseSnapshotChangeIsRejectedAndAudited()
    {
        var releaseBarrier = new BlockingReleaseQuery();
        await using var harness = await Harness.CreateAsync(contractReleaseQuery: releaseBarrier);
        await harness.SaveAndReleaseAsync("V131-G08 release before contract preparation");

        var command = await harness.AuthorizeChangeAsync(
            harness.Change(BuildContract(harness.PrimarySchema, "1"), null,
                "V131-G08 concurrent release snapshot"));
        var pending = harness.Runtime.SubmitAsync(command).AsTask();
        try
        {
            await releaseBarrier.WaitUntilBlockedAsync(TimeSpan.FromSeconds(2));
            var concurrent = await harness.SaveAndReleaseAsync("V131-G08 concurrent release",
                directReleaseService: true);
            Assert.NotEqual(Guid.Empty, concurrent.Record.ReleaseId);
            releaseBarrier.Release();

            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
            Assert.Equal("PlcResultContractReleaseSnapshotChanged", outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
            await harness.Storage.WaitForVerifiedAsync();

            var history = await harness.Contracts.QueryAsync(new(PageSize: 20));
            Assert.True(history.Available, history.ReasonCode);
            Assert.Empty(history.Revisions);
            var trace = await new SqliteCommandTraceQuery(harness.Storage.Options).QueryAsync(
                new(command.CorrelationId, PageSize: 50));
            Assert.Contains(trace.Records, value =>
                value.CommandKind == AuditedCommandKind.ChangePlcResultContract &&
                value.Disposition == CommandDisposition.Rejected &&
                value.ReasonCode == "PlcResultContractReleaseSnapshotChanged");
        }
        finally
        {
            releaseBarrier.Release();
            if (!pending.IsCompleted)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }
        }
    }

    [Fact]
    public async Task V131_G06_Schema16StepUpRejectsMissingContractConfigurationAndPersistsAudit()
    {
        var releasePolicy = new RecipeGovernancePolicy("V131.G06.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        await using var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(
            recipeReleases: new RecipeReleaseStoreOptions(releasePolicy));
        var contract = BuildContract(PrimarySchema(), "1");
        var command = new ChangePlcResultContractCommand(Guid.NewGuid(), storage.Invocation(),
            contract, null, "V131-G06 unsupported schema17 binding");
        var before = await storage.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var stepUp = await storage.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), storage.Invocation(), new StepUpBinding(
                Permission.ManagePlcResultContract, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract),
            storage.Password));
        Assert.False(stepUp.Succeeded);
        Assert.Equal("PlcResultContractConfigurationRequired", stepUp.ReasonCode);
        await storage.WaitForVerifiedAsync();
        var after = await storage.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        Assert.True(after > before);
        Assert.Equal(AuditIntegrityState.Verified, storage.Store.Integrity!.State);
        var independent = await new SqliteAuditIntegrityQuery(storage.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, independent.State);
    }

    [Fact]
    public async Task V131_G07_PlcContractOptionRequiresDraftsReleasesIdentityAndAudit()
    {
        await using var valid = await RecipeDraftStorageTests.Fixture.CreateAsync();
        var invalid = new ProductionStoreOptions(Path.Combine(Path.GetTempPath(),
            "SharpInspect.Runtime.Tests", "V131-G07-invalid.sqlite"))
        {
            AuditIntegrityPolicy = valid.Options.AuditIntegrityPolicy,
            LocalIdentity = valid.IdentityOptions,
            RecipeDrafts = valid.Options.RecipeDrafts,
            PlcResultContracts = new PlcResultContractStoreOptions()
        };
        var exception = Assert.Throws<ArgumentException>(() => new SqliteCommandStore(invalid));
        Assert.StartsWith("PlcResultContractsRequiresDraftsReleasesIdentityAndAudit", exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("options", exception.ParamName);
    }

    private static PlcResultContract BuildContract(AlgorithmResultSchema schema,
        string version, int maximumPayloadBytes = 1024)
    {
        var u16 = Wire(PlcWireRepresentation.UInt16);
        var u32 = Wire(PlcWireRepresentation.UInt32);
        var reasons = new List<PlcReasonCode> { new(null, 0) };
        reasons.AddRange(PlcResultContract.FrameworkReasonCodes.Select((reason, index) =>
            new PlcReasonCode(reason, index + 1)));
        var next = PlcResultContract.FrameworkReasonCodes.Count + 1;
        foreach (var reason in schema.ReasonCodes)
        {
            if (PlcResultContract.FrameworkReasonCodes.Contains(reason, StringComparer.Ordinal))
                continue;
            reasons.Add(new PlcReasonCode(reason, next++));
        }

        return new PlcResultContract("V131.G.PlcContract", version, maximumPayloadBytes, 64,
            new[]
            {
                new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch,
                    new(10, 2), u32),
                new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultSequence,
                    new(12, 2), u32),
                new PlcFrameworkFieldMapping(PlcFrameworkResultField.ExecutionStatus,
                    new(14, 1), u16, executionStatusCodes: new[]
                    {
                        new PlcExecutionStatusCode(ExecutionStatus.Success, 10),
                        new PlcExecutionStatusCode(ExecutionStatus.Error, 11),
                        new PlcExecutionStatusCode(ExecutionStatus.Timeout, 12),
                        new PlcExecutionStatusCode(ExecutionStatus.Cancelled, 13)
                    }),
                new PlcFrameworkFieldMapping(PlcFrameworkResultField.InspectionDecision,
                    new(15, 1), u16, inspectionDecisionCodes: new[]
                    {
                        new PlcInspectionDecisionCode(InspectionDecision.Pass, 20),
                        new PlcInspectionDecisionCode(InspectionDecision.Fail, 21),
                        new PlcInspectionDecisionCode(InspectionDecision.Unknown, 22)
                    }),
                new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultReasonCode,
                    new(16, 1), u16, reasonCodes: reasons)
            },
            new[] { new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash)) });
    }

    private static AlgorithmResultSchema PrimarySchema() => new("V115.Draft.Result", "1",
        Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" },
        new OverlayContract("V115.Draft.Overlay", "1", 0, 64, 16));

    private static AlgorithmResultSchema UnknownSchema() => new("V131.G.Unknown.Result", "1",
        Array.Empty<AlgorithmFieldDefinition>(), new[] { "UnknownReason" },
        new OverlayContract("V131.G.Unknown.Overlay", "1", 0, 64, 16));

    private static AlgorithmResultSchema AlternateSchema() => new("V131.G.Alternate.Result", "1",
        Array.Empty<AlgorithmFieldDefinition>(), new[] { "AlternateReason" },
        new OverlayContract("V131.G.Alternate.Overlay", "1", 0, 64, 16));

    private static PlcWireEncoding Wire(PlcWireRepresentation representation) =>
        new(representation, PlcByteOrder.BigEndian,
            representation is PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16
                ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst,
            PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);

    internal sealed class Harness : IAsyncDisposable
    {
        private bool _runtimeStopped;
        private bool _draftsStopped;
        private bool _authorizationStopped;

        private Harness(RecipeDraftStorageTests.Fixture storage, AlgorithmResultSchema primarySchema,
            AlgorithmResultSchema? alternateSchema, TestClock clock,
            LocalAuthorizationService authorization, RecipeDraftService drafts,
            StationRuntime runtime, RecipeReleaseService releases,
            IPlcResultContractService contracts)
        {
            Storage = storage;
            PrimarySchema = primarySchema;
            AlternateSchema = alternateSchema;
            Clock = clock;
            Authorization = authorization;
            Drafts = drafts;
            Runtime = runtime;
            Releases = releases;
            Contracts = contracts;
        }

        internal RecipeDraftStorageTests.Fixture Storage { get; }
        internal AlgorithmResultSchema PrimarySchema { get; }
        internal AlgorithmResultSchema? AlternateSchema { get; }
        internal TestClock Clock { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal RecipeDraftService Drafts { get; }
        internal StationRuntime Runtime { get; }
        internal RecipeReleaseService Releases { get; }
        internal IPlcResultContractService Contracts { get; }
        internal string Password => Storage.Password;

        internal static async Task<Harness> CreateAsync(bool includeAlternateSchema = false,
            BlockingReleaseQuery? contractReleaseQuery = null)
        {
            var releasePolicy = new RecipeGovernancePolicy("V131.G.Release", "1",
                RecipeGovernanceMode.SingleApproverRelease);
            var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(
                recipeReleases: new RecipeReleaseStoreOptions(releasePolicy),
                plcResultContracts: new PlcResultContractStoreOptions());
            var seed = storage.Document("V131 governance seed");
            var primarySchema = PrimarySchema();
            var alternateSchema = includeAlternateSchema ? AlternateSchema() : null;
            var factories = new List<IVisionAlgorithmFactory>
            {
                new FixtureFactory(seed.Content.Algorithm.Algorithm!, primarySchema,
                    seed.Content.Algorithm.ConfigurationSchema)
            };
            if (alternateSchema is not null)
                factories.Add(new FixtureFactory(new AlgorithmIdentity("V131.G.Alternate", "1"),
                    alternateSchema, seed.Content.Algorithm.ConfigurationSchema));

            var clock = new TestClock();
            var authorization = new LocalAuthorizationService(storage.Store,
                storage.IdentityOptions, storage.Identity, storage.Sessions, clock.Timestamp);
            var drafts = new RecipeDraftService(factories, storage.Options, authorization,
                new SqliteRecipeDraftQuery(storage.Options));
            var runtime = new StationRuntime(storage.Store, TimeSpan.FromMilliseconds(500),
                storage.Sessions, authorization);
            var releases = new RecipeReleaseService(drafts, authorization,
                new SqliteReleasedRecipeQuery(storage.Options), storage.Options,
                () => runtime.GetSnapshotAsync());
            var releaseQuery = contractReleaseQuery is null
                ? (IReleasedRecipeQuery)releases
                : contractReleaseQuery.Attach(new SqliteReleasedRecipeQuery(storage.Options));
            var contracts = new PlcResultContractService(drafts, releaseQuery,
                new SqlitePlcResultContractQuery(storage.Options), authorization, storage.Options,
                runtime.EnterPlcResultContractChangeAsync, () => runtime.GetSnapshotAsync());
            runtime.ConfigureRecipeReleaseService(releases);
            runtime.ConfigurePlcResultContractService(contracts);
            await storage.WaitForVerifiedAsync();
            return new Harness(storage, primarySchema, alternateSchema, clock,
                authorization, drafts, runtime, releases, contracts);
        }

        internal ChangePlcResultContractCommand Change(PlcResultContract contract,
            RecipeContractReference? expectedCurrent, string reason) =>
            new(Guid.NewGuid(), Storage.Invocation(), contract, expectedCurrent, reason);

        internal async Task<ChangePlcResultContractCommand> AuthorizeChangeAsync(
            ChangePlcResultContractCommand command)
        {
            var result = await Authorization.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), Storage.Invocation(), new StepUpBinding(
                    Permission.ManagePlcResultContract, command.CorrelationId,
                    command.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract), Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.GrantId);
            await Storage.WaitForVerifiedAsync();
            return command with { Invocation = Storage.Invocation(result.GrantId) };
        }

        internal async Task<ReleasedRecipe> SaveAndReleaseAsync(string displayName,
            bool directReleaseService = false)
        {
            var saved = await Storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                Storage.Document(displayName), "V131 governance release draft");
            Assert.True(saved.Saved, saved.ReasonCode);
            var source = Assert.IsType<RecipeDraftRevision>(saved.Revision);
            var command = new ReleaseRecipeCommand(Guid.NewGuid(), Storage.Invocation(),
                source.DraftId, source.Revision, source.RevisionContentHash,
                Storage.Options.RecipeReleases!.Policy.Reference, "V131 governance release");
            var grant = await Authorization.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), Storage.Invocation(), new StepUpBinding(
                    Permission.ReleaseRecipe, command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.ReleaseRecipe), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            var authorizedCommand = command with { Invocation = Storage.Invocation(grant.GrantId) };
            RuntimeCommandOutcome outcome;
            if (directReleaseService)
                outcome = (await Releases.ReleaseAsync(authorizedCommand)).Outcome;
            else
                outcome = await Runtime.SubmitAsync(authorizedCommand);
            Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
            await Storage.WaitForVerifiedAsync();
            var page = await Releases.QueryAsync(new(PageSize: 20));
            Assert.True(page.Available, page.ReasonCode);
            return Assert.Single(page.Recipes,
                value => value.Record.Source.RevisionContentHash == source.RevisionContentHash);
        }

        internal async Task SubmitRejectedAsync(ChangePlcResultContractCommand command,
            string reason, CancellationToken cancellationToken = default)
        {
            var before = await Contracts.QueryAsync(new(PageSize: 100));
            Assert.True(before.Available, before.ReasonCode);
            var result = await Runtime.SubmitAsync(command, cancellationToken);
            Assert.Equal(CommandDisposition.Rejected, result.Disposition);
            Assert.Equal(reason, result.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Audit);
            await Storage.WaitForVerifiedAsync();
            var after = await Contracts.QueryAsync(new(PageSize: 100));
            Assert.True(after.Available, after.ReasonCode);
            Assert.Equal(before.Revisions.Count, after.Revisions.Count);
            var trace = await new SqliteCommandTraceQuery(Storage.Options).QueryAsync(
                new(command.CorrelationId, PageSize: 50));
            Assert.Contains(trace.Records, value =>
                value.CommandKind == AuditedCommandKind.ChangePlcResultContract &&
                value.Disposition == CommandDisposition.Rejected && value.ReasonCode == reason);
        }

        internal async Task SignInOperatorAsync()
        {
            var principal = Guid.NewGuid();
            var correlation = Guid.NewGuid();
            var grant = await Authorization.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), Storage.Invocation(), new StepUpBinding(
                    Permission.ManageAccounts, correlation, principal.ToString("D"),
                    AuditedCommandKind.CreateHumanAccount), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            const string operatorPassword = "V131 operator password 2026!";
            var created = await Runtime.SubmitAsync(new CreateHumanAccountCommand(
                correlation, Storage.Invocation(grant.GrantId), principal,
                "v131-contract-operator", "V131 Contract Operator", operatorPassword,
                HumanRoleBundle.Operator));
            Assert.Equal(CommandDisposition.Accepted, created.Disposition);
            await Storage.WaitForVerifiedAsync();
            await Storage.Sessions.LogoutAsync(Storage.Sessions.Current.SessionId);
            var login = await Storage.Sessions.SignInAsync(
                new PasswordSignInRequest("v131-contract-operator", operatorPassword));
            Assert.True(login.Succeeded, login.ReasonCode);
            await Storage.WaitForVerifiedAsync();
        }

        internal async Task StopRuntimeAsync()
        {
            if (_runtimeStopped) return;
            _runtimeStopped = true;
            await Runtime.DisposeAsync();
        }

        internal async Task StopCapabilitiesAsync()
        {
            await StopRuntimeAsync();
            if (!_draftsStopped)
            {
                _draftsStopped = true;
                await Drafts.DisposeAsync();
            }
            if (!_authorizationStopped)
            {
                _authorizationStopped = true;
                Authorization.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopCapabilitiesAsync();
            await Storage.DisposeAsync();
        }
    }

    private sealed class FixtureFactory : IVisionAlgorithmFactory
    {
        internal FixtureFactory(AlgorithmIdentity identity, AlgorithmResultSchema resultSchema,
            AlgorithmConfigurationSchema configurationSchema)
        {
            Descriptor = new AlgorithmDescriptor(identity, configurationSchema, resultSchema);
        }

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("V131 governance fixture must not create an algorithm");
    }

    /// <summary>
    /// Replays the first real release page after a controlled concurrent release.
    /// The production query pins a page's high-water mark; the continuation is
    /// deliberately reopened without that pin so the service observes the newly
    /// committed release and exercises its snapshot-change rejection path.
    /// </summary>
    internal sealed class BlockingReleaseQuery : IReleasedRecipeQuery
    {
        private readonly TaskCompletionSource<ReleasedRecipePage> _firstPage =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _continue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IReleasedRecipeQuery? _inner;
        private int _blocked;

        internal IReleasedRecipeQuery Attach(IReleasedRecipeQuery inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
            return this;
        }

        internal Task WaitUntilBlockedAsync(TimeSpan timeout) =>
            _firstPage.Task.WaitAsync(timeout);

        internal void Release() => _continue.TrySetResult(true);

        public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
            CancellationToken cancellationToken = default) =>
            (_inner ?? throw new InvalidOperationException("V131-G08 release query not attached"))
                .ReadAsync(reference, cancellationToken);

        public async ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
            CancellationToken cancellationToken = default)
        {
            var inner = _inner ?? throw new InvalidOperationException("V131-G08 release query not attached");
            var isFirst = filter.AfterPosition == 0 &&
                Interlocked.Exchange(ref _blocked, 1) == 0;
            var page = await inner.QueryAsync(filter, cancellationToken).ConfigureAwait(false);
            if (!isFirst) return page;

            _firstPage.TrySetResult(page);
            await _continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Keep the original complete snapshot. The concurrent release must
            // be discovered by the real identity writer's high-watermark CAS.
            Assert.Null(page.NextAfterPosition);
            return page;
        }
    }

    internal sealed class TestClock
    {
        private long _ticks = Stopwatch.GetTimestamp();

        internal long Timestamp() => Volatile.Read(ref _ticks);

        internal void AdvancePast(TimeSpan duration)
        {
            var delta = checked((long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency) +
                Stopwatch.Frequency);
            Interlocked.Add(ref _ticks, delta);
        }
    }
}
