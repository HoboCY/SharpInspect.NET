using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// A public-package consumer for the schema-18 Recipe Activation boundary.
/// The consumer creates a real explicit-None released recipe and invokes the
/// public activation service. Missing production authorities remain a durable
/// rejection; this sample never installs a fixture or a production bypass.
/// </summary>
internal static class RecipeActivationDemo
{
    private const string RunCaseId = "V132_N01";
    private const string QueryCaseId = "V132_N02";
    private const string RecipeKey = "RecipeActivationConsumerRecipe";
    private const string AlgorithmId = "Sample.RecipeActivation.Algorithm";
    private const string AlgorithmVersion = "1";
    private const string ConfigurationSchemaId = "Sample.RecipeActivation.Config";
    private const string ResultSchemaId = "Sample.RecipeActivation.Result";
    private const string ResultSchemaVersion = "1";
    private const string ContractId = "Sample.RecipeActivation.PlcContract";
    private const string ContractVersion = "1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new RecipeActivationDemoException("RecipeActivationConsumerPasswordRequired");
            RunCoreAsync(options, Path.GetFullPath(directory), userName, expectedPrincipal, password)
                .GetAwaiter().GetResult();
            Console.WriteLine("V132_N01 recipe-activation PASS access=true requiresStepUp=false admitted=true terminalRejected=true reason=FrameworkQualificationAuthorityUnavailable providerDiscovery=0 providerOpen=0 ready=false active=false armed=false");
            return 0;
        }
        catch (RecipeActivationDemoException exception)
        {
            Console.Error.WriteLine($"{RunCaseId} recipe-activation FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{RunCaseId} recipe-activation FAIL reason=RecipeActivationConsumerCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory)
    {
        try
        {
            QueryCoreAsync(options, Path.GetFullPath(directory)).GetAwaiter().GetResult();
            Console.WriteLine("V132_N02 recipe-activation-query PASS readOnly=true databaseUnchanged=true records=2 active=false pending=false providerDiscovery=0 providerOpen=0");
            return 0;
        }
        catch (RecipeActivationDemoException exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} recipe-activation-query FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} recipe-activation-query FAIL reason=RecipeActivationQueryCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    private static async Task RunCoreAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password)
    {
        RequireConfiguredRun(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "RecipeActivationConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) && expectedPrincipalId != Guid.Empty,
            "RecipeActivationConsumerPrincipalRequired");
        Directory.CreateDirectory(directory);
        Require(!File.Exists(Path.Combine(directory, "activation-evidence.json")),
            "RecipeActivationEvidenceAlreadyExists");

        var factory = new ActivationConsumerFactory();
        var cameraProvider = new CountingCameraProvider();
        var services = new ServiceCollection();
        // These are real public preparation and frame ownership services. Their
        // zero-use counters prove that A12 is reached before physical I/O.
        services.AddSingleton<IVisionAlgorithmFactory>(factory);
        services.AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
        services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(2, 4096,
            TimeSpan.FromMilliseconds(100)));
        services.AddSharpInspectCameraProvider(cameraProvider);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IStationRuntime>();
        var startup = await WaitForStartupFenceAsync(runtime).ConfigureAwait(true);
        var startupFenceCleared = !startup.AdmissionBlockers.Contains(
            "RecipeActivationStartupRecoveryPending", StringComparer.Ordinal);
        Require(startupFenceCleared && !startup.AdmissionBlockers.Contains(
            "RecipeActivationStartupRecoveryRequired", StringComparer.Ordinal),
            "RecipeActivationStartupRecoveryUnavailable");
        var initial = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!initial.Ready && initial.ArmState == ProductionArmState.Disarmed,
            "RecipeActivationConsumerInitialStationNotDisarmed");

        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new PasswordSignInRequest(configuredUserName!, password))
            .ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
            "RecipeActivationConsumerAuthenticationFailed");
        var session = sessions.Current;
        var sessionId = session.SessionId ?? Guid.Empty;
        Require(session.State == InteractiveSessionState.Authenticated && sessionId != Guid.Empty &&
            session.PrincipalId == expectedPrincipalId.ToString("D"),
            "RecipeActivationConsumerSessionUnavailable");
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            session.PrincipalId, session.SessionId);

        var editor = provider.GetRequiredService<IRecipeDraftEditor>();
        var releaseService = provider.GetRequiredService<IRecipeReleaseService>();
        var activationService = provider.GetRequiredService<IRecipeActivationService>();
        var factoryPreparation = provider.GetRequiredService<AlgorithmPreparationService>();
        var framePool = provider.GetRequiredService<FrameBufferPool>();

        var saved = await SaveDraftAsync(options, editor, factory, invocation).ConfigureAwait(true);
        Require(saved.Saved && saved.Revision is not null, "RecipeActivationDraftSaveFailed");
        var source = saved.Revision!;
        Require(source.Content.PartIdentityRequirement?.Mode == PartIdentityRequirementMode.None,
            "RecipeActivationPartIdentityNoneMissingBeforeRelease");

        var releaseCommand = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, source.DraftId,
            source.Revision, source.RevisionContentHash, options.RecipeReleases!.Policy.Reference,
            "T32 public consumer released activation candidate");
        var releaseOutcome = await SubmitWithStepUpAsync(runtime,
            provider.GetRequiredService<IStepUpAuthentication>(), releaseCommand,
            Permission.ReleaseRecipe, releaseCommand.AuthorizationTarget, password).ConfigureAwait(true);
        Require(releaseOutcome.Disposition == CommandDisposition.Accepted &&
            releaseOutcome.Audit == AuditPersistence.Persisted,
            "RecipeActivationReleaseFailed:" + releaseOutcome.ReasonCode);
        await WaitForAuditVerifiedAsync(runtime).ConfigureAwait(true);
        var releases = await releaseService.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
        Require(releases.Available && releases.Recipes.Count == 1,
            "RecipeActivationReleaseHistoryMissing");
        var released = releases.Recipes.Single();
        Require(released.Content.PartIdentityRequirement?.Mode == PartIdentityRequirementMode.None,
            "RecipeActivationReleasedPartIdentityNoneMissing");

        var contractService = provider.GetRequiredService<IPlcResultContractService>();
        var emptyContract = await contractService.ReadCurrentAsync().ConfigureAwait(true);
        Require(emptyContract.Available && emptyContract.Revision is null,
            "RecipeActivationPlcContractHistoryNotEmpty");
        var contract = BuildContract(factory.Descriptor.ResultSchema);
        var contractCommand = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation,
            contract, null, "T32 public consumer activation dependency");
        var contractOutcome = await SubmitWithStepUpAsync(runtime,
            provider.GetRequiredService<IStepUpAuthentication>(), contractCommand,
            Permission.ManagePlcResultContract, contractCommand.AuthorizationTarget, password)
            .ConfigureAwait(true);
        Require(contractOutcome.Disposition == CommandDisposition.Accepted &&
            contractOutcome.Audit == AuditPersistence.Persisted,
            "RecipeActivationPlcContractFailed:" + contractOutcome.ReasonCode);
        await WaitForAuditVerifiedAsync(runtime).ConfigureAwait(true);
        var contractRevision = await contractService.ReadCurrentAsync().ConfigureAwait(true);
        Require(contractRevision.Available && contractRevision.Revision is not null &&
            contractRevision.Revision.Bindings.Count == 1 &&
            contractRevision.Revision.Bindings[0].Binding.Recipe == released.Reference,
            "RecipeActivationPlcContractBindingMissing");

        var activationBefore = await activationService.ReadCurrentAsync().ConfigureAwait(true);
        Require(activationBefore.Available && activationBefore.Record is null &&
            !activationBefore.RecoveryRequired, "RecipeActivationHistoryNotEmptyBeforeAttempt");
        var access = await activationService.GetAccessAsync(invocation).ConfigureAwait(true);
        Require(access.CanActivate && !access.RequiresStepUp &&
            access.ReasonCode == "RecipeActivationAccessAvailable",
            "RecipeActivationAccessUnavailable");

        var databaseBefore = HashFile(options.DatabasePath);
        var command = new ActivateRecipeCommand(Guid.NewGuid(), invocation, released.Reference,
            released.Record.ReleaseId, released.Record.ContentHash, null, null,
            "T32 public consumer governed activation rejection");
        var activation = await activationService.ActivateAsync(command).ConfigureAwait(true);
        Require(activation.Outcome.Disposition == CommandDisposition.Rejected &&
            activation.Outcome.Audit == AuditPersistence.Persisted && activation.Record is not null,
            "RecipeActivationTerminalRejectionNotPersisted");
        var terminal = activation.Record!;
        Require(terminal.Outcome.State == RecipeActivationOutcomeState.Failed &&
            terminal.Outcome.ReasonCode == "FrameworkQualificationAuthorityUnavailable" &&
            terminal.AdmissionReference is not null && terminal.Restoration.State ==
            RecipeActivationRestorationState.NotRequired && terminal.EvidenceKind ==
            RecipeActivationEvidenceKind.LocalAuthority && terminal.SuccessfulSnapshot is null &&
            terminal.ResultingRecipe is null && !terminal.CanBeActive,
            "RecipeActivationTerminalEvidenceInvalid");

        var page = await activationService.QueryAsync(new RecipeActivationFilter(PageSize: 20))
            .ConfigureAwait(true);
        Require(page.Available && page.Records.Count == 2 &&
            page.PendingAdmissions is { Count: 0 }, "RecipeActivationHistoryNotDurable");
        var admission = page.Records.Single(value =>
            value.Outcome.State == RecipeActivationOutcomeState.Admitted);
        var recordedTerminal = page.Records.Single(value => value.IsTerminal);
        Require(admission.EvidenceKind == RecipeActivationEvidenceKind.LocalAuthority &&
            admission.Admission is not null && recordedTerminal.AdmissionReference == admission.Reference &&
            recordedTerminal.AttemptId == admission.AttemptId &&
            recordedTerminal.ContentHash == terminal.ContentHash,
            "RecipeActivationAdmissionTerminalLinkInvalid");
        RequireRejectedAuthorityChecks(recordedTerminal);

        var after = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!after.Ready && after.ArmState == ProductionArmState.Disarmed &&
            after.ActiveRecipe == initial.ActiveRecipe && after.CurrentExecution is null,
            "RecipeActivationChangedProductionState");
        var poolSnapshot = framePool.GetSnapshot();
        // SQLite can keep durable commits in WAL while the writer is open.
        // Retire all writers before hashing the stable database for cold reads.
        await provider.DisposeAsync().ConfigureAwait(true);
        var databaseAfter = HashFile(options.DatabasePath);
        Require(databaseAfter != databaseBefore, "RecipeActivationEvidenceWasNotWritten");

        var evidence = new ActivationEvidence
        {
            Result = "Pass",
            CaseId = RunCaseId,
            ExternalNuGetConsumer = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                "SHARPINSPECT_RECIPE_ACTIVATION_CONSUMER")),
            ConsumerSha256 = HashFile(typeof(RecipeActivationDemo).Assembly.Location),
            DatabaseHashBeforeActivation = databaseBefore,
            DatabaseHashAfterActivation = databaseAfter,
            DatabaseChangedByActivation = true,
            StartupFenceCleared = startupFenceCleared,
            ActivationAccessAvailable = access.CanActivate,
            ActivationRequiresStepUp = access.RequiresStepUp,
            PrincipalId = expectedPrincipalId,
            SessionId = sessionId,
            ReleasedVersions = releases.Recipes.Count,
            ReleaseId = released.Record.ReleaseId,
            ReleaseRecordContentHash = released.Record.ContentHash,
            RecipeContentHash = released.Reference.ContentHash,
            PartIdentityMode = released.Content.PartIdentityRequirement!.Mode.ToString(),
            AdmissionPosition = admission.Position,
            AdmissionActivationId = admission.ActivationId,
            AdmissionContentHash = admission.ContentHash,
            TerminalPosition = recordedTerminal.Position,
            TerminalActivationId = recordedTerminal.ActivationId,
            TerminalContentHash = recordedTerminal.ContentHash,
            TerminalAdmissionContentHash = recordedTerminal.AdmissionReference!.ContentHash,
            TerminalOutcome = recordedTerminal.Outcome.State.ToString(),
            TerminalReasonCode = recordedTerminal.Outcome.ReasonCode,
            EvidenceKind = recordedTerminal.EvidenceKind.ToString(),
            RestorationState = recordedTerminal.Restoration.State.ToString(),
            ReadyBefore = initial.Ready,
            ReadyAfter = after.Ready,
            ActiveBefore = initial.ActiveRecipe is not null,
            ActiveAfter = after.ActiveRecipe is not null,
            ArmedBefore = initial.ArmState == ProductionArmState.Armed,
            ArmedAfter = after.ArmState == ProductionArmState.Armed,
            AlgorithmPreparationRegistered = factoryPreparation is not null,
            FramePoolRegistered = framePool is not null,
            CameraProviderRegistered = true,
            AlgorithmFactoryValidateCalls = factory.ValidateCalls,
            AlgorithmFactoryCreateCalls = factory.CreateCalls,
            AlgorithmWarmUpCalls = factory.WarmUpCalls,
            AlgorithmDisposeCalls = factory.DisposeCalls,
            ProviderDiscoveryCalls = cameraProvider.DiscoveryCalls,
            ProviderOpenCalls = cameraProvider.OpenCalls,
            ProviderApplyCalls = cameraProvider.ApplyCalls,
            ProviderStartCalls = cameraProvider.StartCalls,
            ProviderStopCalls = cameraProvider.StopCalls,
            FramePoolOutstandingLeases = poolSnapshot.OutstandingLeases,
            Checks = recordedTerminal.Checks.Select(value => new CheckEvidence
            {
                CheckId = value.CheckId,
                Subject = value.Subject,
                Status = value.Status.ToString(),
                ReasonCode = value.ReasonCode,
                RequestedEvidenceHash = value.RequestedEvidenceHash,
                EffectiveEvidenceHash = value.EffectiveEvidenceHash
            }).ToArray(),
            Production = "NotRun",
            PlcConnection = "NotRun",
            PayloadExecution = "NotRun",
            Activation = "RejectedBeforePhysicalIo",
            IndependentColdRead = false,
            DatabaseUnchangedByColdRead = false
        };
        await WriteEvidenceAsync(directory, evidence).ConfigureAwait(true);
    }

    private static async Task QueryCoreAsync(ProductionStoreOptions options, string directory)
    {
        RequireConfiguredRun(options);
        var evidencePath = Path.Combine(directory, "activation-evidence.json");
        Require(File.Exists(evidencePath), "RecipeActivationEvidenceMissing");
        var evidence = JsonSerializer.Deserialize<ActivationEvidence>(
            await File.ReadAllTextAsync(evidencePath).ConfigureAwait(true), JsonOptions);
        Require(evidence is { Result: "Pass", CaseId: RunCaseId, ReleasedVersions: 1,
            AdmissionPosition: 1, TerminalPosition: 2 }, "RecipeActivationEvidenceInvalid");

        // Query-only capability construction is intentionally direct. There is no
        // ServiceCollection, SqliteCommandStore, algorithm factory or provider here.
        var before = HashFile(options.DatabasePath);
        IRecipeActivationQuery query = new SqliteRecipeActivationQuery(options);
        var page = await query.QueryAsync(new RecipeActivationFilter(PageSize: 20)).ConfigureAwait(true);
        Require(page.Available && page.Records.Count == 2 && page.ThroughPosition == 2 &&
            page.NextAfterPosition is null && page.PendingAdmissions is { Count: 0 },
            "RecipeActivationColdQueryUnavailable:" + page.ReasonCode);
        var current = await query.ReadCurrentAsync().ConfigureAwait(true);
        Require(current.Available && current.Record is null && !current.RecoveryRequired &&
            current.ReasonCode == "RecipeActivationNoActiveRecord",
            "RecipeActivationColdCurrentProjectionInvalid");
        var admission = page.Records.Single(value =>
            value.Outcome.State == RecipeActivationOutcomeState.Admitted);
        var terminal = page.Records.Single(value => value.IsTerminal);
        var exactAdmission = await query.ReadAsync(admission.Reference).ConfigureAwait(true);
        var exactTerminal = await query.ReadAsync(terminal.Reference).ConfigureAwait(true);
        var exactTerminalRecord = exactTerminal.Record;
        Require(exactAdmission.Available && exactAdmission.Record?.ContentHash == admission.ContentHash &&
            exactTerminal.Available && exactTerminalRecord is not null &&
            exactTerminalRecord.ContentHash == terminal.ContentHash &&
            exactTerminalRecord.AdmissionReference == admission.Reference &&
            exactTerminalRecord.Outcome.ReasonCode == evidence.TerminalReasonCode,
            "RecipeActivationColdExactRecordMismatch");
        RequireRejectedAuthorityChecks(exactTerminalRecord!);
        var after = HashFile(options.DatabasePath);
        Require(before == after && evidence.DatabaseHashAfterActivation == before,
            "RecipeActivationColdQueryChangedDatabase");

        await File.WriteAllTextAsync(Path.Combine(directory, "activation-restart.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass",
                CaseId = QueryCaseId,
                ReadOnlyQuery = true,
                WriterStarted = false,
                AlgorithmFactoryCreated = false,
                ProviderFactoryCreated = false,
                DatabaseHashBefore = before,
                DatabaseHashAfter = after,
                DatabaseUnchanged = true,
                RecordCount = page.Records.Count,
                AdmissionPosition = admission.Position,
                TerminalPosition = terminal.Position,
                AdmissionContentHash = admission.ContentHash,
                TerminalContentHash = terminal.ContentHash,
                TerminalAdmissionContentHash = terminal.AdmissionReference!.ContentHash,
                CurrentReasonCode = current.ReasonCode,
                RecoveryRequired = current.RecoveryRequired,
                Ready = false,
                Active = false,
                ArmState = "Disarmed",
                ProviderDiscoveryCalls = 0,
                ProviderOpenCalls = 0,
                AlgorithmFactoryCreateCalls = 0,
                FramePoolOutstandingLeases = 0,
                Checks = exactTerminalRecord!.Checks.Select(value => new CheckEvidence
                {
                    CheckId = value.CheckId,
                    Subject = value.Subject,
                    Status = value.Status.ToString(),
                    ReasonCode = value.ReasonCode,
                    RequestedEvidenceHash = value.RequestedEvidenceHash,
                    EffectiveEvidenceHash = value.EffectiveEvidenceHash
                }).ToArray()
            }, JsonOptions)).ConfigureAwait(true);

        evidence.IndependentColdRead = true;
        evidence.DatabaseUnchangedByColdRead = true;
        evidence.DatabaseHashBeforeColdRead = before;
        evidence.DatabaseHashAfterColdRead = after;
        await WriteEvidenceAsync(directory, evidence).ConfigureAwait(true);
    }

    private static async Task<RecipeDraftSaveResult> SaveDraftAsync(ProductionStoreOptions options,
        IRecipeDraftEditor editor, ActivationConsumerFactory factory, CommandInvocation invocation)
    {
        var configuration = AlgorithmConfigurationSnapshot.Create(
            factory.Descriptor.ConfigurationSchema, Array.Empty<AlgorithmConfigurationEntry>());
        var content = new RecipeDraftContent(RecipeKey, "T32 public activation consumer recipe",
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
            }, partIdentityRequirement: PartIdentityRequirement.None);
        return await editor.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0,
            null, content, "T32 save explicit None activation candidate", invocation)).ConfigureAwait(true);
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
                _ => throw new InvalidOperationException("RecipeActivationStepUpCommandInvalid")
            });
        var grant = await stepUp.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), command.Invocation,
            binding, password)).ConfigureAwait(true);
        Require(grant.Succeeded && grant.GrantId is not null, "RecipeActivationStepUpFailed");
        RuntimeCommand authorized = command switch
        {
            ReleaseRecipeCommand release => release with
            {
                Invocation = release.Invocation with { StepUpGrantId = grant.GrantId.Value }
            },
            ChangePlcResultContractCommand change => change with
            {
                Invocation = change.Invocation with { StepUpGrantId = grant.GrantId.Value }
            },
            _ => throw new InvalidOperationException("RecipeActivationStepUpCommandInvalid")
        };
        return await runtime.SubmitAsync(authorized).ConfigureAwait(true);
    }

    private static PlcResultContract BuildContract(AlgorithmResultSchema schema)
    {
        var u16 = Wire(PlcWireRepresentation.UInt16);
        var u32 = Wire(PlcWireRepresentation.UInt32);
        var reasons = new List<PlcReasonCode> { new(null, 0) };
        reasons.AddRange(PlcResultContract.FrameworkReasonCodes.Concat(schema.ReasonCodes)
            .Distinct(StringComparer.Ordinal).Select((reason, index) =>
            new PlcReasonCode(reason, index + 1)));
        return new PlcResultContract(ContractId, ContractVersion, 256, 64, new[]
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
                new PlcMeasurementMapping("InspectionCount", PlcMeasurementDisposition.Excluded)
            })
        });
    }

    private static PlcWireEncoding Wire(PlcWireRepresentation representation) => new(representation,
        PlcByteOrder.BigEndian,
        representation is PlcWireRepresentation.UInt16 or PlcWireRepresentation.Int16
            ? PlcWordOrder.NotApplicable : PlcWordOrder.HighWordFirst,
        PlcRoundingMode.Exact, PlcOverflowBehavior.EncodingFault);

    private static RecipeActivationCheck Check(RecipeActivationRecord record, string checkId) =>
        record.Checks.Single(value => value.CheckId == checkId);

    private static void RequireRejectedAuthorityChecks(RecipeActivationRecord record)
    {
        var expectedAuthorityFailures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V132.A12"] = "FrameworkQualificationAuthorityUnavailable",
            ["V132.A13"] = "ProviderQualificationAuthorityUnavailable",
            ["V132.A14"] = "ProductionAcquisitionAuthorityUnavailable",
            ["V132.A15"] = "PlcDeploymentAuthorityUnavailable",
            ["V132.A16"] = "EvidenceAdmissionAuthorityUnavailable",
            ["V132.A17"] = "DeploymentPolicyAuthorityUnavailable",
            ["V132.A18"] = "ProductionCycleUnavailable"
        };
        foreach (var expected in expectedAuthorityFailures)
        {
            var check = record.Checks.SingleOrDefault(value => value.CheckId == expected.Key);
            Require(check is { Status: RecipeActivationCheckStatus.Failed } &&
                check.ReasonCode == expected.Value, "RecipeActivationAuthorityCheckMissing:" + expected.Key);
        }
        Require(Check(record, "V132.A04").Status == RecipeActivationCheckStatus.NotApplicable &&
            Check(record, "V132.A06").Status == RecipeActivationCheckStatus.NotRun &&
            Check(record, "V132.A11").Status == RecipeActivationCheckStatus.NotRun,
            "RecipeActivationPreparationOrFramePoolGateWasWrong");
    }

    private static void RequireConfiguredRun(ProductionStoreOptions options)
    {
        Require(options.LocalIdentity is not null && options.AuditIntegrityPolicy is not null,
            "RecipeActivationIdentityAndAuditRequired");
        Require(options.CameraSetup is not null && options.RecipeDrafts is not null &&
            options.RecipeReleases is not null && options.PlcResultContracts is not null &&
            options.RecipeActivations is not null,
            "RecipeActivationSchema18OptionsIncomplete");
    }

    private static async Task<StationStateSnapshot> WaitForStartupFenceAsync(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(true);
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                throw new RecipeActivationDemoException("RecipeActivationAuditVerificationFaulted");
            if (!snapshot.AdmissionBlockers.Contains("RecipeActivationStartupRecoveryPending",
                    StringComparer.Ordinal) && snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified)
                return snapshot;
            if (timeout.IsCancellationRequested)
                throw new RecipeActivationDemoException("RecipeActivationStartupFenceTimeout");
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task WaitForAuditVerifiedAsync(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(true);
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                throw new RecipeActivationDemoException("RecipeActivationAuditVerificationFaulted");
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) return;
            if (timeout.IsCancellationRequested)
                throw new RecipeActivationDemoException("RecipeActivationAuditVerificationTimeout");
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task WriteEvidenceAsync(string directory, ActivationEvidence evidence)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "activation-evidence.json"),
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"),
            JsonSerializer.Serialize(new
            {
                Result = evidence.Result,
                CaseId = evidence.CaseId,
                ExternalNuGetConsumer = evidence.ExternalNuGetConsumer,
                ConsumerSha256 = evidence.ConsumerSha256,
                StartupFenceCleared = evidence.StartupFenceCleared,
                ActivationAccessAvailable = evidence.ActivationAccessAvailable,
                ActivationRequiresStepUp = evidence.ActivationRequiresStepUp,
                IndependentColdRead = evidence.IndependentColdRead,
                DatabaseUnchangedByColdRead = evidence.DatabaseUnchangedByColdRead,
                ReleasedVersions = evidence.ReleasedVersions,
                PartIdentityMode = evidence.PartIdentityMode,
                AdmissionPosition = evidence.AdmissionPosition,
                TerminalPosition = evidence.TerminalPosition,
                TerminalOutcome = evidence.TerminalOutcome,
                TerminalReasonCode = evidence.TerminalReasonCode,
                EvidenceKind = evidence.EvidenceKind,
                RestorationState = evidence.RestorationState,
                AlgorithmPreparationRegistered = evidence.AlgorithmPreparationRegistered,
                FramePoolRegistered = evidence.FramePoolRegistered,
                CameraProviderRegistered = evidence.CameraProviderRegistered,
                ProviderDiscoveryCalls = evidence.ProviderDiscoveryCalls,
                ProviderOpenCalls = evidence.ProviderOpenCalls,
                ProviderApplyCalls = evidence.ProviderApplyCalls,
                FramePoolOutstandingLeases = evidence.FramePoolOutstandingLeases,
                Ready = evidence.ReadyAfter,
                Active = evidence.ActiveAfter,
                Armed = evidence.ArmedAfter,
                Production = evidence.Production,
                PlcConnection = evidence.PlcConnection,
                PayloadExecution = evidence.PayloadExecution,
                Activation = evidence.Activation,
                RunEvidence = "activation-evidence.json",
                RestartEvidence = evidence.IndependentColdRead ? "activation-restart.json" : null
            }, JsonOptions)).ConfigureAwait(true);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
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

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new RecipeActivationDemoException(reason);
    }

    private sealed class ActivationConsumerFactory : IVisionAlgorithmFactory
    {
        public ActivationConsumerFactory()
        {
            var resultSchema = new AlgorithmResultSchema(ResultSchemaId, ResultSchemaVersion, new[]
            {
                new AlgorithmFieldDefinition("InspectionCount", AlgorithmScalarType.Int64, "count", true,
                    new(minInt64: 0, maxInt64: 100))
            }, new[] { "RecipeActivationConsumerNotRun" },
                new OverlayContract("Sample.RecipeActivation.Overlay", "1"));
            Descriptor = new AlgorithmDescriptor(new(AlgorithmId, AlgorithmVersion),
                new AlgorithmConfigurationSchema(ConfigurationSchemaId, "1",
                    Array.Empty<AlgorithmFieldDefinition>()), resultSchema);
        }

        public AlgorithmDescriptor Descriptor { get; }
        public int ValidateCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int WarmUpCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCalls++;
            return ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                configuration.Validate(Descriptor.ConfigurationSchema));
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            return ValueTask.FromResult<IVisionAlgorithm>(new ConsumerAlgorithm(
                Descriptor.ResultSchema.OverlayContract, () => WarmUpCalls++, () => DisposeCalls++));
        }
    }

    private sealed class ConsumerAlgorithm : IVisionAlgorithm
    {
        private readonly OverlayContract _overlay;
        private readonly Action _warmed;
        private readonly Action _disposed;
        private bool _prepared;
        private bool _disposedState;

        internal ConsumerAlgorithm(OverlayContract overlay, Action warmed, Action disposed)
        { _overlay = overlay; _warmed = warmed; _disposed = disposed; }

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prepared = true;
            _warmed();
            return ValueTask.CompletedTask;
        }

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (!_prepared || _disposedState)
                throw new InvalidOperationException("RecipeActivationConsumerAlgorithmNotPrepared");
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Unknown,
                "RecipeActivationConsumerNotRun", new[]
                {
                    new AlgorithmMeasurement("InspectionCount", "count",
                        AlgorithmScalarValue.FromInt64(0))
                }, new OutputOverlaySet(_overlay)));
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposedState)
            {
                _disposedState = true;
                _disposed();
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A passive public provider probe. It supplies no device and is expected to
    /// observe zero calls because qualification is rejected before camera I/O.
    /// </summary>
    private sealed class CountingCameraProvider : ICameraProvider
    {
        private int _discoveryCalls;
        private int _openCalls;
        private int _applyCalls;
        private int _startCalls;
        private int _stopCalls;

        internal CountingCameraProvider()
        {
            Identity = new CameraProviderIdentity("Sample.RecipeActivation.Provider", "1",
                "Sample.RecipeActivation.Adapter", "1");
        }

        public CameraProviderIdentity Identity { get; }
        public int DiscoveryCalls => Volatile.Read(ref _discoveryCalls);
        public int OpenCalls => Volatile.Read(ref _openCalls);
        public int ApplyCalls => Volatile.Read(ref _applyCalls);
        public int StartCalls => Volatile.Read(ref _startCalls);
        public int StopCalls => Volatile.Read(ref _stopCalls);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _discoveryCalls);
            return ValueTask.FromResult(CameraDiscoveryResult.Failure("RecipeActivationConsumerProviderProbe"));
        }

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            return ValueTask.FromResult(CameraOpenResult.Failure("RecipeActivationConsumerProviderProbe"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ActivationEvidence
    {
        public string Result { get; set; } = string.Empty;
        public string CaseId { get; set; } = string.Empty;
        public bool ExternalNuGetConsumer { get; set; }
        public string ConsumerSha256 { get; set; } = string.Empty;
        public string DatabaseHashBeforeActivation { get; set; } = string.Empty;
        public string DatabaseHashAfterActivation { get; set; } = string.Empty;
        public bool DatabaseChangedByActivation { get; set; }
        public bool StartupFenceCleared { get; set; }
        public bool ActivationAccessAvailable { get; set; }
        public bool ActivationRequiresStepUp { get; set; }
        public Guid PrincipalId { get; set; }
        public Guid SessionId { get; set; }
        public int ReleasedVersions { get; set; }
        public Guid ReleaseId { get; set; }
        public string ReleaseRecordContentHash { get; set; } = string.Empty;
        public string RecipeContentHash { get; set; } = string.Empty;
        public string PartIdentityMode { get; set; } = string.Empty;
        public long AdmissionPosition { get; set; }
        public Guid AdmissionActivationId { get; set; }
        public string AdmissionContentHash { get; set; } = string.Empty;
        public long TerminalPosition { get; set; }
        public Guid TerminalActivationId { get; set; }
        public string TerminalContentHash { get; set; } = string.Empty;
        public string TerminalAdmissionContentHash { get; set; } = string.Empty;
        public string TerminalOutcome { get; set; } = string.Empty;
        public string TerminalReasonCode { get; set; } = string.Empty;
        public string EvidenceKind { get; set; } = string.Empty;
        public string RestorationState { get; set; } = string.Empty;
        public bool ReadyBefore { get; set; }
        public bool ReadyAfter { get; set; }
        public bool ActiveBefore { get; set; }
        public bool ActiveAfter { get; set; }
        public bool ArmedBefore { get; set; }
        public bool ArmedAfter { get; set; }
        public bool AlgorithmPreparationRegistered { get; set; }
        public bool FramePoolRegistered { get; set; }
        public bool CameraProviderRegistered { get; set; }
        public int AlgorithmFactoryValidateCalls { get; set; }
        public int AlgorithmFactoryCreateCalls { get; set; }
        public int AlgorithmWarmUpCalls { get; set; }
        public int AlgorithmDisposeCalls { get; set; }
        public int ProviderDiscoveryCalls { get; set; }
        public int ProviderOpenCalls { get; set; }
        public int ProviderApplyCalls { get; set; }
        public int ProviderStartCalls { get; set; }
        public int ProviderStopCalls { get; set; }
        public int FramePoolOutstandingLeases { get; set; }
        public CheckEvidence[] Checks { get; set; } = Array.Empty<CheckEvidence>();
        public string Production { get; set; } = "NotRun";
        public string PlcConnection { get; set; } = "NotRun";
        public string PayloadExecution { get; set; } = "NotRun";
        public string Activation { get; set; } = "RejectedBeforePhysicalIo";
        public bool IndependentColdRead { get; set; }
        public bool DatabaseUnchangedByColdRead { get; set; }
        public string? DatabaseHashBeforeColdRead { get; set; }
        public string? DatabaseHashAfterColdRead { get; set; }
    }

    private sealed class CheckEvidence
    {
        public string CheckId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ReasonCode { get; set; } = string.Empty;
        public string? RequestedEvidenceHash { get; set; }
        public string? EffectiveEvidenceHash { get; set; }
    }

    private sealed class RecipeActivationDemoException : Exception
    {
        internal RecipeActivationDemoException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }
}
