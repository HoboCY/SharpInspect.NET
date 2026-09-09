using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// A public-package consumer for the immutable PLC result-contract ledger.
/// This sample creates a real local draft and release, changes one contract
/// through the public Runtime command and Step-Up boundary, then verifies the
/// same revision through the independent read-only query process. It never
/// connects a PLC and never creates a production payload or activation.
/// </summary>
internal static class PlcResultContractGovernanceDemo
{
    private const string RunCaseId = "V131-C01";
    private const string QueryCaseId = "V131-C02";
    private const string ContractId = "Sample.PlcResultContract";
    private const string ContractVersion = "1";
    private const string SchemaId = "Sample.PlcContract.Result";
    private const string SchemaVersion = "1";
    private const string RecipeKey = "PlcContractRecipe";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new PlcResultContractDemoException("PlcResultContractConsumerPasswordRequired");
            RunCoreAsync(options, Path.GetFullPath(directory), userName, expectedPrincipal, password)
                .GetAwaiter().GetResult();
            Console.WriteLine("V131-C01 plc-result-contract PASS released=true changed=true invalidGrantRejected=true changedIntentRejected=true ready=false active=false armed=false");
            return 0;
        }
        catch (PlcResultContractDemoException exception)
        {
            Console.Error.WriteLine($"{RunCaseId} plc-result-contract FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{RunCaseId} plc-result-contract FAIL reason=PlcResultContractConsumerCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory)
    {
        try
        {
            QueryCoreAsync(options, Path.GetFullPath(directory)).GetAwaiter().GetResult();
            Console.WriteLine("V131-C02 plc-result-contract-query PASS readOnly=true databaseUnchanged=true revisions=1 bindings=1");
            return 0;
        }
        catch (PlcResultContractDemoException exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} plc-result-contract-query FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} plc-result-contract-query FAIL reason=PlcResultContractQueryCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    private static async Task RunCoreAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password)
    {
        RequireConfiguredRun(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "PlcResultContractConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) && expectedPrincipalId != Guid.Empty,
            "PlcResultContractConsumerPrincipalRequired");
        Directory.CreateDirectory(directory);
        Require(!File.Exists(Path.Combine(directory, "contract-evidence.json")),
            "PlcResultContractEvidenceAlreadyExists");

        var factory = new ContractFixtureFactory();
        var schema = factory.Descriptor.ResultSchema;
        var contract = BuildContract(schema);
        var services = new ServiceCollection();
        services.AddSingleton<IVisionAlgorithmFactory>(factory);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        await WaitForVerifiedAsync(runtime).ConfigureAwait(true);
        var before = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!before.Ready && before.ArmState == ProductionArmState.Disarmed,
            "PlcResultContractDevelopmentStationNotDisarmed");

        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new PasswordSignInRequest(configuredUserName!, password))
            .ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
            "PlcResultContractConsumerAuthenticationFailed");
        var session = sessions.Current;
        var sessionId = session.SessionId ?? Guid.Empty;
        Require(session.State == InteractiveSessionState.Authenticated &&
            sessionId != Guid.Empty && session.PrincipalId == expectedPrincipalId.ToString("D"),
            "PlcResultContractConsumerSessionUnavailable");
        await WaitForVerifiedAsync(runtime).ConfigureAwait(true);

        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            session.PrincipalId, session.SessionId);
        var editor = provider.GetRequiredService<IRecipeDraftEditor>();
        var releaseService = provider.GetRequiredService<IRecipeReleaseService>();
        var saved = await SaveDraftAsync(options, editor, factory, invocation).ConfigureAwait(true);
        Require(saved.Saved && saved.Revision is not null, "PlcResultContractDraftSaveFailed");
        var source = saved.Revision!;

        var releaseCommand = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, source.DraftId,
            source.Revision, source.RevisionContentHash, options.RecipeReleases!.Policy.Reference,
            "T31 PLC 结果契约依赖发布");
        var releaseOutcome = await SubmitWithStepUpAsync(runtime,
            provider.GetRequiredService<IStepUpAuthentication>(), releaseCommand,
            Permission.ReleaseRecipe, releaseCommand.AuthorizationTarget, password).ConfigureAwait(true);
        Require(releaseOutcome.Disposition == CommandDisposition.Accepted &&
            releaseOutcome.Audit == AuditPersistence.Persisted, "PlcResultContractRecipeReleaseFailed");
        await WaitForVerifiedAsync(runtime).ConfigureAwait(true);
        var releases = await releaseService.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
        Require(releases.Available && releases.Recipes.Count == 1, "PlcResultContractReleaseHistoryMissing");
        var released = releases.Recipes.Single();
        var databaseBefore = HashFile(options.DatabasePath);

        var contractService = provider.GetRequiredService<IPlcResultContractService>();
        var empty = await contractService.ReadCurrentAsync().ConfigureAwait(true);
        Require(empty.Available && empty.Revision is null, "PlcResultContractHistoryNotEmpty");
        var access = await contractService.GetAccessAsync(invocation).ConfigureAwait(true);
        Require(access.CanChange, "PlcResultContractChangeAccessUnavailable");
        var change = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation, contract, null,
            "T31 已发布配方结果契约");
        var changeOutcome = await SubmitWithStepUpAsync(runtime,
            provider.GetRequiredService<IStepUpAuthentication>(), change,
            Permission.ManagePlcResultContract, change.AuthorizationTarget, password).ConfigureAwait(true);
        Require(changeOutcome.Disposition == CommandDisposition.Accepted &&
            changeOutcome.Audit == AuditPersistence.Persisted, "PlcResultContractChangeFailed:" + changeOutcome.ReasonCode);
        var committed = await contractService.ReadCurrentAsync().ConfigureAwait(true);
        Require(committed.Available && committed.Revision is not null,
            "PlcResultContractRevisionMissing:" + committed.ReasonCode);
        var revision = committed.Revision!;
        Require(revision.Contract.ContentHash == contract.ContentHash &&
            revision.SchemaValidations.Count == 1 && revision.Bindings.Count == 1,
            "PlcResultContractRevisionBindingMissing");
        var binding = revision.Bindings.Single().Binding;
        Require(binding.Contract.ContentHash == contract.ContentHash &&
            binding.ResultSchema.ContentHash == schema.ContentHash &&
            binding.Recipe.ContentHash == released.Reference.ContentHash &&
            revision.Bindings.Single().ReleaseRecordContentHash == released.Record.ContentHash,
            "PlcResultContractExactReleaseBindingMismatch");
        var historyBeforeInvalid = await contractService.QueryAsync(new(PageSize: 20))
            .ConfigureAwait(true);
        Require(historyBeforeInvalid.Available && historyBeforeInvalid.Revisions.Count == 1 &&
            historyBeforeInvalid.NextAfterPosition is null,
            "PlcResultContractInitialHistoryInvalid");

        var invalidGrant = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation, BuildContract(schema, "2"),
            revision.Reference, "T31 invalid grant");
        invalidGrant = invalidGrant with
        {
            Invocation = invocation with { StepUpGrantId = Guid.NewGuid() }
        };
        var invalidGrantOutcome = await runtime.SubmitAsync(invalidGrant).ConfigureAwait(true);
        Require(invalidGrantOutcome.Disposition == CommandDisposition.Rejected &&
            invalidGrantOutcome.ReasonCode == "StepUpInvalid" &&
            invalidGrantOutcome.Audit == AuditPersistence.Persisted,
            "PlcResultContractInvalidGrantAccepted");
        var historyAfterInvalidGrant = await contractService.QueryAsync(new(PageSize: 20))
            .ConfigureAwait(true);
        var invalidGrantRevisionCount = historyAfterInvalidGrant.Available
            ? historyAfterInvalidGrant.Revisions.Count - historyBeforeInvalid.Revisions.Count : -1;
        Require(historyAfterInvalidGrant.Available && historyAfterInvalidGrant.Revisions.Count == 1 &&
            historyAfterInvalidGrant.NextAfterPosition is null && invalidGrantRevisionCount == 0,
            "PlcResultContractInvalidGrantChangedHistory");

        var intentCorrelationId = Guid.NewGuid();
        var nextContract = BuildContract(schema, "3");
        var intended = new ChangePlcResultContractCommand(intentCorrelationId, invocation, nextContract,
            revision.Reference, "T31 intended reason");
        var intendedGrant = await provider.GetRequiredService<IStepUpAuthentication>().ReauthenticateAsync(
            new StepUpRequest(Guid.NewGuid(), invocation,
                new StepUpBinding(Permission.ManagePlcResultContract, intended.CorrelationId,
                    intended.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract), password))
            .ConfigureAwait(true);
        Require(intendedGrant.Succeeded && intendedGrant.GrantId is not null,
            "PlcResultContractIntentStepUpFailed");
        var intendedGrantId = intendedGrant.GrantId!.Value;
        var changedIntent = new ChangePlcResultContractCommand(intentCorrelationId, invocation, nextContract,
            revision.Reference, "T31 changed reason") with
        {
            Invocation = invocation with { StepUpGrantId = intendedGrantId }
        };
        var changedIntentOutcome = await runtime.SubmitAsync(changedIntent).ConfigureAwait(true);
        Require(changedIntentOutcome.Disposition == CommandDisposition.Rejected &&
            changedIntentOutcome.ReasonCode == "StepUpInvalid" &&
            changedIntentOutcome.Audit == AuditPersistence.Persisted,
            "PlcResultContractChangedIntentAccepted");
        var historyAfterChangedIntent = await contractService.QueryAsync(new(PageSize: 20))
            .ConfigureAwait(true);
        var changedIntentRevisionCount = historyAfterChangedIntent.Available
            ? historyAfterChangedIntent.Revisions.Count - historyBeforeInvalid.Revisions.Count : -1;
        Require(historyAfterChangedIntent.Available && historyAfterChangedIntent.Revisions.Count == 1 &&
            historyAfterChangedIntent.NextAfterPosition is null && changedIntentRevisionCount == 0,
            "PlcResultContractChangedIntentChangedHistory");

        var afterInvalid = await contractService.ReadCurrentAsync().ConfigureAwait(true);
        Require(afterInvalid.Available && afterInvalid.Revision?.ContentHash == revision.ContentHash,
            "PlcResultContractRejectedMutationChangedHistory");
        var releasedAfter = await releaseService.ReadAsync(released.Reference).ConfigureAwait(true);
        var releasedAfterRecipe = releasedAfter.Recipe;
        Require(releasedAfter.Available && releasedAfterRecipe is not null &&
            releasedAfterRecipe.Record.ContentHash == released.Record.ContentHash &&
            releasedAfterRecipe.Reference.ContentHash == released.Reference.ContentHash,
            "PlcResultContractChangedReleasedRecipe");
        var after = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!after.Ready && after.ArmState == ProductionArmState.Disarmed &&
            after.ActiveRecipe == before.ActiveRecipe && after.CurrentExecution is null,
            "PlcResultContractChangedProductionAuthority");

        var databaseAfter = HashFile(options.DatabasePath);
        var evidence = new ContractEvidence
        {
            Result = "Pass",
            CaseId = RunCaseId,
            ExternalNuGetConsumer = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SHARPINSPECT_PLC_RESULT_CONTRACT_CONSUMER")),
            ConsumerSha256 = HashFile(typeof(PlcResultContractGovernanceDemo).Assembly.Location),
            DatabaseHashBefore = databaseBefore,
            DatabaseHashAfter = databaseAfter,
            Available = committed.Available && releases.Available,
            ReleasedVersions = releases.Recipes.Count,
            ContractRevisionCount = 1,
            ContractRevisionPosition = revision.Position,
            ContractRevisionContentHash = revision.ContentHash,
            Contract = ContractIdentity(contract.Reference),
            Schema = ContractIdentity(new(schema.Id, schema.Version, schema.ContentHash)),
            Release = new ReleaseEvidence(released.Record.ReleaseId, released.Record.ContentHash,
                released.Reference.Id, released.Reference.Version, released.Reference.ContentHash,
                source.DraftId, source.Revision, source.RevisionContentHash),
            Binding = new BindingEvidence(binding.ContentHash, binding.Recipe.Id, binding.Recipe.Version,
                binding.Recipe.ContentHash, binding.Algorithm.Id, binding.Algorithm.Version,
                binding.ResultSchema.Id, binding.ResultSchema.Version, binding.ResultSchema.ContentHash),
            SchemaValidationCount = revision.SchemaValidations.Count,
            BindingCount = revision.Bindings.Count,
            ReleaseHighWatermark = revision.ReleaseHighWatermark,
            InvalidGrantRejected = true,
            InvalidGrantReasonCode = invalidGrantOutcome.ReasonCode,
            InvalidGrantRevisionCount = invalidGrantRevisionCount,
            ChangedIntentRejected = true,
            ChangedIntentReasonCode = changedIntentOutcome.ReasonCode,
            ChangedIntentRevisionCount = changedIntentRevisionCount,
            ReadyBefore = before.Ready,
            ReadyAfter = after.Ready,
            ActiveBefore = before.ActiveRecipe is not null,
            ActiveAfter = after.ActiveRecipe is not null,
            ArmedBefore = before.ArmState == ProductionArmState.Armed,
            ArmedAfter = after.ArmState == ProductionArmState.Armed,
            PrincipalId = expectedPrincipalId,
            SessionId = sessionId,
            Production = "NotRun",
            PlcConnection = "NotRun",
            PayloadExecution = "NotRun",
            Activation = "NotRun",
            IndependentRestart = false,
            DatabaseUnchangedByRestart = false
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "contract-evidence.json"),
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(true);
        await WriteSummaryAsync(directory, evidence).ConfigureAwait(true);
    }

    private static async Task<RecipeDraftSaveResult> SaveDraftAsync(ProductionStoreOptions options,
        IRecipeDraftEditor editor, ContractFixtureFactory factory, CommandInvocation invocation)
    {
        var configuration = AlgorithmConfigurationSnapshot.Create(
            factory.Descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        var content = new RecipeDraftContent(RecipeKey, "T31 PLC 结果契约外部消费者",
            RecipeAlgorithmBinding.FromDescriptor(factory.Descriptor), configuration, "Primary",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 500, 0,
                new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null),
            TimeSpan.FromSeconds(1), Array.Empty<RecipeAssetRequirement>(), new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(options.RecipeDrafts!.ExecutionPolicy.Id,
                        options.RecipeDrafts.ExecutionPolicy.Version,
                        options.RecipeDrafts.ExecutionPolicy.ContentHash)),
                new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance,
                    options.RecipeReleases!.Policy.Reference)
            });
        return await editor.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0,
            null, content, "T31 保存 PLC 结果契约依赖配方", invocation)).ConfigureAwait(true);
    }

    private static async Task<RuntimeCommandOutcome> SubmitWithStepUpAsync<TCommand>(
        IStationRuntime runtime, IStepUpAuthentication stepUp, TCommand command,
        Permission permission, string target, string password)
        where TCommand : RuntimeCommand
    {
        var binding = new StepUpBinding(permission, command.CorrelationId, target,
            command switch
            {
                ReleaseRecipeCommand => AuditedCommandKind.ReleaseRecipe,
                ChangePlcResultContractCommand => AuditedCommandKind.ChangePlcResultContract,
                _ => throw new InvalidOperationException("PlcResultContractStepUpCommandInvalid")
            });
        var grant = await stepUp.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), command.Invocation,
            binding, password)).ConfigureAwait(true);
        Require(grant.Succeeded && grant.GrantId is not null, "PlcResultContractStepUpFailed");
        var grantId = grant.GrantId!.Value;
        RuntimeCommand authorized = command switch
        {
            ReleaseRecipeCommand release => release with
            {
                Invocation = release.Invocation with { StepUpGrantId = grantId }
            },
            ChangePlcResultContractCommand change => change with
            {
                Invocation = change.Invocation with { StepUpGrantId = grantId }
            },
            _ => throw new InvalidOperationException("PlcResultContractStepUpCommandInvalid")
        };
        return await runtime.SubmitAsync(authorized).ConfigureAwait(true);
    }

    private static PlcResultContract BuildContract(AlgorithmResultSchema schema, string version = ContractVersion)
    {
        var u16 = Wire(PlcWireRepresentation.UInt16);
        var u32 = Wire(PlcWireRepresentation.UInt32);
        var reasons = new List<PlcReasonCode> { new(null, 0) };
        reasons.AddRange(PlcResultContract.FrameworkReasonCodes.Select((reason, index) =>
            new PlcReasonCode(reason, index + 1)));
        reasons.Add(new PlcReasonCode("ContractFixtureRejected", 6));
        return new PlcResultContract(ContractId, version, 256, 64, new[]
        {
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ControllerEpoch, new(10, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultSequence, new(12, 2), u32),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ExecutionStatus, new(14, 1), u16,
                executionStatusCodes: new[]
                {
                    new PlcExecutionStatusCode(ExecutionStatus.Success, 10),
                    new PlcExecutionStatusCode(ExecutionStatus.Error, 11),
                    new PlcExecutionStatusCode(ExecutionStatus.Timeout, 12),
                    new PlcExecutionStatusCode(ExecutionStatus.Cancelled, 13)
                }),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.InspectionDecision, new(15, 1), u16,
                inspectionDecisionCodes: new[]
                {
                    new PlcInspectionDecisionCode(InspectionDecision.Pass, 20),
                    new PlcInspectionDecisionCode(InspectionDecision.Fail, 21),
                    new PlcInspectionDecisionCode(InspectionDecision.Unknown, 22)
                }),
            new PlcFrameworkFieldMapping(PlcFrameworkResultField.ResultReasonCode, new(16, 1), u16,
                reasonCodes: reasons)
        }, new[]
        {
            new PlcResultSchemaMap(new(schema.Id, schema.Version, schema.ContentHash), new[]
            {
                new PlcMeasurementMapping("InspectionCount", PlcMeasurementDisposition.Excluded),
                new PlcMeasurementMapping("Disposition", PlcMeasurementDisposition.Excluded)
            })
        });
    }

    private static PlcWireEncoding Wire(PlcWireRepresentation representation) => new(representation,
        PlcByteOrder.BigEndian,
        representation is PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16
            ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst,
        PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);

    private static void RequireConfiguredRun(ProductionStoreOptions options)
    {
        Require(options.LocalIdentity is not null && options.AuditIntegrityPolicy is not null,
            "PlcResultContractIdentityAndAuditRequired");
        Require(options.RecipeDrafts is not null && options.RecipeReleases is not null &&
            options.PlcResultContracts is not null, "PlcResultContractStoreNotConfigured");
    }

    private static async Task QueryCoreAsync(ProductionStoreOptions options, string directory)
    {
        Require(options.LocalIdentity is not null && options.AuditIntegrityPolicy is not null &&
            options.PlcResultContracts is not null && options.RecipeReleases is not null &&
            options.RecipeDrafts is not null, "PlcResultContractQueryStoreNotConfigured");
        var evidencePath = Path.Combine(directory, "contract-evidence.json");
        Require(File.Exists(evidencePath), "PlcResultContractEvidenceMissing");
        var evidence = JsonSerializer.Deserialize<ContractEvidence>(
            await File.ReadAllTextAsync(evidencePath).ConfigureAwait(true), JsonOptions);
        Require(evidence is { Result: "Pass", Available: true, ReleasedVersions: 1,
            ContractRevisionCount: 1 },
            "PlcResultContractEvidenceInvalid");
        var recorded = evidence!;

        var before = HashFile(options.DatabasePath);
        var query = new SqlitePlcResultContractQuery(options);
        var page = await query.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
        Require(page.Available && page.Revisions.Count == 1 && page.NextAfterPosition is null,
            "PlcResultContractColdQueryUnavailable:" + page.ReasonCode);
        var reference = new RecipeContractReference(recorded.Contract.Id, recorded.Contract.Version,
            recorded.Contract.ContentHash);
        var current = await query.ReadCurrentAsync().ConfigureAwait(true);
        var exact = await query.ReadAsync(reference).ConfigureAwait(true);
        var revision = current.Revision ?? throw new PlcResultContractDemoException(
            "PlcResultContractColdQueryRevisionMissing");
        Require(current.Available && exact.Available && exact.Revision?.ContentHash == revision.ContentHash &&
            revision.Reference == reference && revision.ContentHash == recorded.ContractRevisionContentHash,
            "PlcResultContractColdQueryHashMismatch");
        Require(revision.SchemaValidations.Count == recorded.SchemaValidationCount &&
            revision.Bindings.Count == recorded.BindingCount && revision.ReleaseHighWatermark == recorded.ReleaseHighWatermark,
            "PlcResultContractColdQueryEvidenceMismatch");
        var validation = revision.SchemaValidations.Single();
        var binding = revision.Bindings.Single();
        Require(validation.Schema.ContentHash == recorded.Schema.ContentHash &&
            binding.Binding.ContentHash == recorded.Binding.BindingContentHash &&
            binding.ReleaseRecordContentHash == recorded.Release.RecordContentHash &&
            binding.Binding.Recipe.ContentHash == recorded.Release.RecipeContentHash,
            "PlcResultContractColdQueryBindingMismatch");
        var after = HashFile(options.DatabasePath);
        Require(before == after, "PlcResultContractColdQueryChangedDatabase");

        recorded.IndependentRestart = true;
        recorded.DatabaseUnchangedByRestart = true;
        recorded.DatabaseHashBefore = before;
        recorded.DatabaseHashAfter = after;
        await File.WriteAllTextAsync(Path.Combine(directory, "contract-restart.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass",
                CaseId = QueryCaseId,
                ReadOnlyQuery = true,
                DatabaseUnchanged = true,
                RevisionCount = page.Revisions.Count,
                ContractRevisionContentHash = revision.ContentHash,
                ContractReference = revision.Reference,
                SchemaValidationCount = revision.SchemaValidations.Count,
                BindingCount = revision.Bindings.Count,
                BindingContentHash = binding.Binding.ContentHash,
                ReleaseRecordContentHash = binding.ReleaseRecordContentHash,
                OpenedDevices = 0,
                Ready = false,
                Production = "NotRun"
            }, JsonOptions)).ConfigureAwait(true);
        await WriteSummaryAsync(directory, recorded).ConfigureAwait(true);
    }

    private static async Task WriteSummaryAsync(string directory, ContractEvidence evidence)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"),
            JsonSerializer.Serialize(new
            {
                Result = evidence.Result,
                CaseId = evidence.CaseId,
                ExternalNuGetConsumer = evidence.ExternalNuGetConsumer,
                ConsumerSha256 = evidence.ConsumerSha256,
                IndependentRestart = evidence.IndependentRestart,
                DatabaseUnchangedByRestart = evidence.DatabaseUnchangedByRestart,
                ReleasedVersions = evidence.ReleasedVersions,
                Available = evidence.Available,
                Active = evidence.ActiveAfter,
                Armed = evidence.ArmedAfter,
                Ready = evidence.ReadyAfter,
                ContractRevisionCount = evidence.ContractRevisionCount,
                ContractRevisionContentHash = evidence.ContractRevisionContentHash,
                BindingContentHash = evidence.Binding.BindingContentHash,
                ReleaseRecordContentHash = evidence.Release.RecordContentHash,
                Production = evidence.Production,
                PlcConnection = evidence.PlcConnection,
                PayloadExecution = evidence.PayloadExecution,
                Activation = evidence.Activation,
                RunEvidence = "contract-evidence.json",
                RestartEvidence = evidence.IndependentRestart ? "contract-restart.json" : null
            }, JsonOptions)).ConfigureAwait(true);
    }

    private static ContractIdentityEvidence ContractIdentity(RecipeContractReference reference) =>
        new(reference.Id, reference.Version, reference.ContentHash);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static async Task WaitForVerifiedAsync(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) return;
            Require(snapshot.AuditIntegrity?.State != AuditIntegrityState.Faulted &&
                !timeout.IsCancellationRequested, "PlcResultContractAuditVerificationUnavailable:" +
                snapshot.AuditIntegrity?.ReasonCode);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static void WriteFailureLocation(Exception exception)
    {
        var failure = exception.GetBaseException();
        Console.Error.WriteLine("exceptionType=" + failure.GetType().FullName);
        foreach (var frame in (new System.Diagnostics.StackTrace(failure, true).GetFrames() ??
                     Array.Empty<System.Diagnostics.StackFrame>()).Take(12))
        {
            var method = frame.GetMethod();
            Console.Error.WriteLine($"location={method?.DeclaringType?.FullName}.{method?.Name}:{frame.GetFileLineNumber()}");
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new PlcResultContractDemoException(reason);
    }

    private sealed class ContractFixtureFactory : IVisionAlgorithmFactory
    {
        public ContractFixtureFactory()
        {
            var resultSchema = new AlgorithmResultSchema(SchemaId, SchemaVersion, new[]
            {
                new AlgorithmFieldDefinition("InspectionCount", AlgorithmScalarType.Int64, "count", true,
                    new(minInt64: 0, maxInt64: 100)),
                new AlgorithmFieldDefinition("Disposition", AlgorithmScalarType.Enum, "none", false,
                    new(allowedValues: new[] { "pass", "fail" }))
            }, new[] { "ContractFixtureRejected" }, new OverlayContract("Sample.PlcContract.Overlay", "1", 0, 0, 0));
            Descriptor = new AlgorithmDescriptor(new("Sample.PlcContract.Algorithm", "1"),
                new AlgorithmConfigurationSchema("Sample.PlcContract.Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
                resultSchema);
        }

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                configuration.Validate(Descriptor.ConfigurationSchema));
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PlcContractFixtureMustNotExecute");
    }

    private sealed record ContractIdentityEvidence(string Id, string Version, string ContentHash);

    private sealed record ReleaseEvidence(Guid ReleaseId, string RecordContentHash,
        string RecipeId, string RecipeVersion, string RecipeContentHash, Guid DraftId,
        long Revision, string RevisionContentHash);

    private sealed record BindingEvidence(string BindingContentHash, string RecipeId,
        string RecipeVersion, string RecipeContentHash, string AlgorithmId, string AlgorithmVersion,
        string ResultSchemaId, string ResultSchemaVersion, string ResultSchemaContentHash);

    private sealed class ContractEvidence
    {
        public ContractEvidence() { }

        public string Result { get; set; } = string.Empty;
        public string CaseId { get; set; } = string.Empty;
        public bool ExternalNuGetConsumer { get; set; }
        public string ConsumerSha256 { get; set; } = string.Empty;
        public string DatabaseHashBefore { get; set; } = string.Empty;
        public string DatabaseHashAfter { get; set; } = string.Empty;
        public bool Available { get; set; }
        public int ReleasedVersions { get; set; }
        public int ContractRevisionCount { get; set; }
        public long ContractRevisionPosition { get; set; }
        public string ContractRevisionContentHash { get; set; } = string.Empty;
        public ContractIdentityEvidence Contract { get; set; } = new(string.Empty, string.Empty, string.Empty);
        public ContractIdentityEvidence Schema { get; set; } = new(string.Empty, string.Empty, string.Empty);
        public ReleaseEvidence Release { get; set; } = new(Guid.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, Guid.Empty, 0, string.Empty);
        public BindingEvidence Binding { get; set; } = new(string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        public int SchemaValidationCount { get; set; }
        public int BindingCount { get; set; }
        public long ReleaseHighWatermark { get; set; }
        public bool InvalidGrantRejected { get; set; }
        public string InvalidGrantReasonCode { get; set; } = string.Empty;
        public int InvalidGrantRevisionCount { get; set; }
        public bool ChangedIntentRejected { get; set; }
        public string ChangedIntentReasonCode { get; set; } = string.Empty;
        public int ChangedIntentRevisionCount { get; set; }
        public bool ReadyBefore { get; set; }
        public bool ReadyAfter { get; set; }
        public bool ActiveBefore { get; set; }
        public bool ActiveAfter { get; set; }
        public bool ArmedBefore { get; set; }
        public bool ArmedAfter { get; set; }
        public Guid PrincipalId { get; set; }
        public Guid SessionId { get; set; }
        public string Production { get; set; } = "NotRun";
        public string PlcConnection { get; set; } = "NotRun";
        public string PayloadExecution { get; set; } = "NotRun";
        public string Activation { get; set; } = "NotRun";
        public bool IndependentRestart { get; set; }
        public bool DatabaseUnchangedByRestart { get; set; }
    }

    private sealed class PlcResultContractDemoException : Exception
    {
        internal PlcResultContractDemoException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }
}
