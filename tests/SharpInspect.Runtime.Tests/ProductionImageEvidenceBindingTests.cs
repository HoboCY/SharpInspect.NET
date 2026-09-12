using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V150 P08-P15 deployment-evidence refusal semantics. The exact Evidence Capture Policy
/// reference of a Released Recipe is bound to the current local deployment: the deployment
/// must declare a qualified image stage, the store must be bound to that same stage, the
/// current catalog must still resolve the exact identity, and a Recipe without a capture
/// declaration is only accepted by a deployment without a stage. Every refusal fails
/// V132.A16 and V132.A18 closed, so activation can never reach Passed or Armed. The
/// no-image deployment keeps its legacy binding, which resolves no reference and stays
/// available, as the positive control.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V150_P08")]
    public async Task V150_P08_DeclaredCaptureWithoutALocalImageStageRefusesRealActivationAndArm()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var capture = new EvidenceCapturePolicySnapshot("V150.Capture", "1", EvidenceCaptureMode.All);
        // The Released Recipe declares the exact capture policy and the release catalog can
        // resolve it, but this deployment never qualified a local image stage.
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            capturePolicy: capture);
        using var issuer = new ProductionTestIssuer();

        var activation = await V150BindingActivateAsync(harness, issuer);

        Assert.Equal(CommandDisposition.Rejected, activation.Outcome.Disposition);
        Assert.Equal("ProductionEvidenceCapturePolicyUnavailable", activation.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, activation.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(activation.Record);
        Assert.NotEqual(RecipeActivationOutcomeState.Succeeded, record.Outcome.State);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Null(record.ResultingRecipe);
        var admission = Assert.Single(record.Checks, value => value.CheckId == "V132.A16");
        Assert.Equal(RecipeActivationCheckStatus.Failed, admission.Status);
        Assert.Equal("ProductionEvidenceCapturePolicyUnavailable", admission.ReasonCode);
        var cycle = Assert.Single(record.Checks, value => value.CheckId == "V132.A18");
        Assert.Equal(RecipeActivationCheckStatus.Failed, cycle.Status);
        Assert.Equal("ProductionInspectionEvidenceRequirementMismatch", cycle.ReasonCode);
        Assert.All(new[] { admission, cycle }, value =>
        {
            Assert.True(value.RequiredForActivation);
            Assert.False(value.Satisfied);
        });
        // The refused activation leaves the station disarmed with no active production recipe.
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.NotEqual(true, current.Record?.Outcome.Succeeded);
    }

    [Theory]
    [InlineData((int)V150BindingCatalogCase.OtherIdentity)]
    [InlineData((int)V150BindingCatalogCase.OtherVersion)]
    [InlineData((int)V150BindingCatalogCase.OtherContentHash)]
    [Trait("VerificationId", "V150_P09")]
    public void V150_P09_TheDeclaredCaptureReferenceMustResolveExactlyInTheCurrentCatalog(int caseValue)
    {
        var catalogCase = (V150BindingCatalogCase)caseValue;
        using var root = new ProductionImageTestRoot();
        var policy = V150BindingPolicy();
        var declared = V150BindingRecipe(policy);
        var deployed = V150BindingStage(root, 512 * 1024);
        var options = V150BindingOptions(deployed, V150BindingProfile(), V150BindingTrace());
        // The stage binding matches; only the exact catalog identity is unavailable.
        var store = V150BindingStore(deployed, V150BindingCatalog(catalogCase, policy));

        var rejection = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ProductionImageEvidenceBinding.Resolve(options, store, declared);
        });

        Assert.Equal("ProductionEvidenceCapturePolicyUnavailable", rejection.Message);
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, store, declared));
    }

    [Fact]
    [Trait("VerificationId", "V150_P10")]
    public void V150_P10_ARecipeDeclaringCaptureIsRefusedWithoutALocalImageStage()
    {
        using var root = new ProductionImageTestRoot();
        var policy = V150BindingPolicy();
        var declared = V150BindingRecipe(policy);
        var deployed = V150BindingStage(root, 512 * 1024);
        var options = V150BindingOptions(stage: null, V150BindingProfile(), V150BindingTrace());
        var store = V150BindingStore(deployed, new EvidenceCapturePolicyCatalog(new[] { policy }));

        var rejection = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ProductionImageEvidenceBinding.Resolve(options, store, declared);
        });

        Assert.Equal("ProductionImageStageConfigurationRequired", rejection.Message);
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, store, declared));
    }

    [Fact]
    [Trait("VerificationId", "V150_P11")]
    public void V150_P11_AStoreBoundToAnotherStageIsRefused()
    {
        using var root = new ProductionImageTestRoot();
        var policy = V150BindingPolicy();
        var declared = V150BindingRecipe(policy);
        var bound = V150BindingStage(root, 512 * 1024);
        // Same local root, different qualified limits: the exact stage binding differs.
        var relinked = V150BindingStage(root, 256 * 1024);
        Assert.NotEqual(bound.ContentHash, relinked.ContentHash);
        var options = V150BindingOptions(relinked, V150BindingProfile(), V150BindingTrace());
        var store = V150BindingStore(bound, new EvidenceCapturePolicyCatalog(new[] { policy }));

        var rejection = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ProductionImageEvidenceBinding.Resolve(options, store, declared);
        });

        Assert.Equal("ProductionImageEvidenceConfigurationMismatch", rejection.Message);
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, store, declared));
    }

    [Fact]
    [Trait("VerificationId", "V150_P12")]
    public void V150_P12_ARecipeWithoutAnyCaptureDeclarationIsRefusedByAnImageDeployment()
    {
        using var root = new ProductionImageTestRoot();
        var deployed = V150BindingStage(root, 512 * 1024);
        var undeclared = V150BindingRecipe(capturePolicy: null);
        var options = V150BindingOptions(deployed, V150BindingProfile(), V150BindingTrace());
        var store = V150BindingStore(deployed, new EvidenceCapturePolicyCatalog(new[] { V150BindingPolicy() }));

        var rejection = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ProductionImageEvidenceBinding.Resolve(options, store, undeclared);
        });

        Assert.Equal("ProductionEvidenceCaptureDeclarationRequired", rejection.Message);
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, store, undeclared));
    }

    [Fact]
    [Trait("VerificationId", "V150_P13")]
    public void V150_P13_TheLegacyNoImageDeploymentStaysAvailableWithoutACaptureDeclaration()
    {
        var legacy = V150BindingRecipe(capturePolicy: null);
        var legacyStore = V150BindingStore(stage: null, catalog: null);
        var options = V150BindingOptions(stage: null, V150BindingProfile(), V150BindingTrace());

        // The old deployment keeps its exact behavior: no local stage and no declaration is
        // no image-retention binding, never a rejection.
        Assert.Null(ProductionImageEvidenceBinding.Resolve(options, legacyStore, legacy));
        Assert.True(ProductionImageEvidenceBinding.IsAvailable(options, legacyStore, legacy));
        // Missing inputs are still refused rather than assumed available.
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(null, legacyStore, legacy));
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, null, legacy));
        Assert.False(ProductionImageEvidenceBinding.IsAvailable(options, legacyStore, null));
    }

    [Fact]
    [Trait("VerificationId", "V150_P14")]
    public void V150_P14_TheExactDeclaredIdentityResolvesTheCurrentCatalogSnapshot()
    {
        using var root = new ProductionImageTestRoot();
        var declared = V150BindingPolicy();
        var current = new EvidenceCapturePolicySnapshot(declared.Id, declared.Version, declared.Mode);
        Assert.Equal(declared.Reference, current.Reference);
        Assert.NotSame(declared, current);
        var deployed = V150BindingStage(root, 512 * 1024);
        var recipe = V150BindingRecipe(declared);
        var options = V150BindingOptions(deployed, V150BindingProfile(), V150BindingTrace());
        var store = V150BindingStore(deployed, new EvidenceCapturePolicyCatalog(new[] { current }));

        var resolved = ProductionImageEvidenceBinding.Resolve(options, store, recipe);

        // Resolution returns the current deployment catalog identity, not the Recipe's copy.
        Assert.Same(current, resolved);
        Assert.Equal(EvidenceCaptureMode.All, resolved!.Mode);
        Assert.Equal(current.ContentHash, recipe.PolicyRequirements
            .Single(value => value.Kind == RecipePolicyKind.EvidenceCapture).Contract.ContentHash);
        Assert.True(ProductionImageEvidenceBinding.IsAvailable(options, store, recipe));
    }

    [Theory]
    [InlineData((int)V150BindingImageCase.DeclaredWithoutLocalStage)]
    [InlineData((int)V150BindingImageCase.DeclarationMissingWithLocalStage)]
    [InlineData((int)V150BindingImageCase.StageBindingMismatch)]
    [InlineData((int)V150BindingImageCase.CatalogMissingTheExactPolicy)]
    [Trait("VerificationId", "V150_P15")]
    public void V150_P15_DeploymentEvidenceCannotPassItsAdmissionAndCycleChecks(int caseValue)
    {
        var imageCase = (V150BindingImageCase)caseValue;
        using var root = new ProductionImageTestRoot();
        var policy = V150BindingPolicy();
        var deployed = V150BindingStage(root, 512 * 1024);
        var relinked = V150BindingStage(root, 256 * 1024);
        var declarationMissing = imageCase == V150BindingImageCase.DeclarationMissingWithLocalStage;
        var content = V150BindingRecipe(declarationMissing ? null : policy);
        var profile = V150BindingProfile();
        var trace = V150BindingTrace();
        var options = V150BindingOptions(imageCase switch
        {
            V150BindingImageCase.DeclaredWithoutLocalStage => null,
            V150BindingImageCase.StageBindingMismatch => relinked,
            _ => deployed
        }, profile, trace);
        var store = V150BindingStore(imageCase == V150BindingImageCase.DeclaredWithoutLocalStage ? null : deployed,
            imageCase == V150BindingImageCase.CatalogMissingTheExactPolicy
                ? V150BindingCatalog(V150BindingCatalogCase.OtherVersion, policy)
                : new EvidenceCapturePolicyCatalog(new[] { policy }),
            new TraceStorageDeploymentScope("V150.Binding.Deployment", "1",
                Array.Empty<TraceStorageRouteIdentity>()));
        var checks = new RecipeActivationChecks();

        checks.VerifyDeploymentEvidence(V150BindingSnapshot(content), new RecipeActivationDeploymentEvidence(
            new ProductionConfiguration(new Dictionary<ProductionConfigurationBinding, string>
            {
                [ProductionConfigurationBinding.PlcEndpoint] = profile.EndpointBindingHash
            }), ProductionQualificationInputs.Unconfigured, options, store, trace,
            productionWriterAvailable: true, startupReconciled: true));

        var admission = Assert.Single(checks.Snapshot(), value => value.CheckId == "V132.A16");
        var cycle = Assert.Single(checks.Snapshot(), value => value.CheckId == "V132.A18");
        Assert.Equal(RecipeActivationCheckStatus.Failed, admission.Status);
        Assert.Equal("ProductionEvidenceCapturePolicyUnavailable", admission.ReasonCode);
        Assert.Equal(RecipeActivationCheckStatus.Failed, cycle.Status);
        Assert.Equal("ProductionInspectionEvidenceRequirementMismatch", cycle.ReasonCode);
        Assert.All(new[] { admission, cycle }, value =>
        {
            Assert.True(value.RequiredForActivation);
            Assert.False(value.Satisfied);
        });
    }

    /// <summary>
    /// Releases and activates the prepared production candidate exactly like the acceptance
    /// harness, but returns the activation result instead of asserting success. The
    /// deployment-evidence refusal is the observation under test.
    /// </summary>
    private static async Task<RecipeActivationResult> V150BindingActivateAsync(ManualHarness harness,
        ProductionTestIssuer issuer)
    {
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
        var fixture = harness.Fixture;
        var release = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(), harness.Draft.DraftId,
            harness.Draft.Revision, harness.Draft.RevisionContentHash,
            fixture.Options.RecipeReleases!.Policy.Reference, "V150 release declared capture candidate");
        var releaseGrant = await ProductionGrantAsync(harness, Permission.ReleaseRecipe, release.CorrelationId,
            release.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var released = await harness.Service<IRecipeReleaseService>().ReleaseAsync(release with
        {
            Invocation = harness.Invocation() with { StepUpGrantId = releaseGrant }
        });
        AssertAccepted(released.Outcome, "Release declared capture candidate");
        var recipe = Assert.IsType<ReleasedRecipe>(released.Recipe);
        var contract = PlcResultContractTestSupport.Contract(harness.Factory.Descriptor.ResultSchema,
            frameworkReasons: PlcResultContract.ProductionFailureReasonCatalogV2);
        var change = new ChangePlcResultContractCommand(Guid.NewGuid(), harness.Invocation(), contract, null,
            "V150 bind complete production failure catalog");
        var contractGrant = await ProductionGrantAsync(harness, Permission.ManagePlcResultContract,
            change.CorrelationId, change.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
        AssertAccepted(await runtime.SubmitAsync(change with
        {
            Invocation = harness.Invocation() with { StepUpGrantId = contractGrant }
        }), "V150 PLC contract");
        await WaitProductionAsync(harness, state => state.Recovery == RecoveryState.None &&
            state.PlcCommunication is { Healthy: true }, "V150 startup and communication synchronization");
        var activate = new ActivateRecipeCommand(Guid.NewGuid(), harness.Invocation(), recipe.Reference,
            recipe.Record.ReleaseId, recipe.Record.ContentHash, null, null, "V150 declared capture activation");
        var activationGrant = await ProductionGrantAsync(harness, Permission.ActivateRecipe,
            activate.CorrelationId, activate.AuthorizationTarget, AuditedCommandKind.ActivateRecipe);
        return await harness.Service<IRecipeActivationService>().ActivateAsync(activate with
        {
            Invocation = harness.Invocation() with { StepUpGrantId = activationGrant }
        });
    }

    private static EvidenceCapturePolicySnapshot V150BindingPolicy() =>
        new("V150.Capture", "1", EvidenceCaptureMode.All);

    private static EvidenceCapturePolicyCatalog V150BindingCatalog(V150BindingCatalogCase catalogCase,
        EvidenceCapturePolicySnapshot policy) => catalogCase switch
        {
            V150BindingCatalogCase.OtherIdentity => new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot("V150.Other", policy.Version, policy.Mode)
            }),
            V150BindingCatalogCase.OtherVersion => new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, "2", policy.Mode)
            }),
            _ => new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, policy.Version, EvidenceCaptureMode.None)
            })
        };

    private static RecipeDraftContent V150BindingRecipe(EvidenceCapturePolicySnapshot? capturePolicy)
    {
        var configuration = new AlgorithmConfigurationSchema("V150.Binding.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var result = new AlgorithmResultSchema("V150.Binding.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), new[] { "V150BindingUnknown" },
            new OverlayContract("V150.Binding.Overlay", "1", 1, 4, 4, 0));
        var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("V150.Binding.Algorithm", "1"),
            configuration, result);
        var requirements = capturePolicy is null ? Array.Empty<RecipePolicyRequirement>() : new[]
        {
            new RecipePolicyRequirement(RecipePolicyKind.EvidenceCapture, capturePolicy.Reference)
        };
        return new RecipeDraftContent("V150.Binding.Recipe", "Capture binding test",
            RecipeAlgorithmBinding.FromDescriptor(descriptor),
            AlgorithmConfigurationSnapshot.Create(configuration, Array.Empty<AlgorithmConfigurationEntry>()),
            "TopCamera", new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 250, 0, null),
            TimeSpan.FromSeconds(2), Array.Empty<RecipeAssetRequirement>(), requirements,
            partIdentityRequirement: PartIdentityRequirement.None);
    }

    private static ProductionImageStageOptions V150BindingStage(ProductionImageTestRoot root,
        long maximumStageBytes) => new(root.Path, maximumStageBytes, 32 * 1024 * 1024, 200);

    private static ProductionStoreOptions V150BindingStore(ProductionImageStageOptions? stage,
        EvidenceCapturePolicyCatalog? catalog, TraceStorageDeploymentScope? scope = null) => new()
        {
            ImageEvidence = stage is null ? null : new ProductionImageEvidenceStoreOptions(stage),
            RecipeReleases = catalog is null ? null : new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
                "V150.Binding.Release", "1", RecipeGovernanceMode.SingleApproverRelease))
                { EvidenceCapturePolicies = catalog },
            TraceStoragePolicies = scope is null ? null :
                new TraceStoragePolicyStoreOptions { DeploymentScope = scope },
            ProductionInspections = new ProductionInspectionStoreOptions()
        };

    private static ProductionInspectionOptions V150BindingOptions(ProductionImageStageOptions? stage,
        ModbusProductionProfile profile, TraceStoragePolicySnapshot trace) => stage is null
        ? new ProductionInspectionOptions("V150.Binding.Station", ProductionEvidenceRequirement.None, profile,
            trace.Version, trace.ContentHash, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2))
        : new ProductionInspectionOptions(stage, "V150.Binding.Station", profile, trace.Version,
            trace.ContentHash, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    private static TraceStoragePolicySnapshot V150BindingTrace() => new(new TraceStoragePolicyPublication(1,
        TraceStoragePolicyRuntimeTests.Policy(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        Guid.NewGuid(), DateTimeOffset.UtcNow, previousContentHash: null));

    private static ModbusProductionProfile V150BindingProfile()
    {
        var policy = new PlcCommunicationPolicy("V150.Binding.Plc", "1", TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), 3,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        return new ModbusProductionProfile("V150.Binding.Profile", "1", "127.0.0.1", 502, 1, 0, 10,
            new ModbusCommunicationBinding(policy, 100, 200), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Fully typed local-authority activation snapshot: no device, database or activation
    /// service is involved, and no read-only field is bypassed.
    /// </summary>
    private static RecipeActivationSnapshot V150BindingSnapshot(RecipeDraftContent content)
    {
        var revision = new RecipeDraftRevision(1, Guid.NewGuid(), 1, Guid.NewGuid(), null,
            content.ContentHash, content, Guid.NewGuid(), Guid.NewGuid(), 1, "V150 binding source revision",
            DateTimeOffset.UtcNow);
        var release = new RecipeReleaseRecord(1, Guid.NewGuid(), Guid.NewGuid(), 1, revision,
            new RecipeGovernancePolicy("V150.Binding.Release", "1", RecipeGovernanceMode.SingleApproverRelease),
            new[] { new RecipeReleaseValidationCheck("Structure", "recipe", true, "Passed") },
            new[]
            {
                new RecipeReleaseChange("Camera/Configuration", "requested", "applied",
                    revision.AuthorPrincipalId, revision.DraftId, revision.Revision)
            },
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            new RecipeContractReference("V150.Binding.Authorization", "1", V150BindingHash('A')),
            "V150 deployment evidence binding", V150BindingHash('B'), DateTimeOffset.UtcNow);
        var camera = new CameraSetupSnapshot(content.CameraRole, null, new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Closed,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, 1)));
        var schema = new AlgorithmResultSchema("V150.Binding.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), new[] { "V150BindingUnknown" },
            new OverlayContract("V150.Binding.Overlay", "1", 1, 4, 4, 0));
        var validation = new PlcResultSchemaValidation(PlcResultContractTestSupport.Contract(schema), schema,
            new[] { new PlcResultValidationCheck("V150.Binding.Proof", "schema", true, "Passed") });
        return new RecipeActivationSnapshot(RecipeActivationEvidenceKind.LocalAuthority, release, Guid.NewGuid(),
            new AlgorithmExecutionPolicy("V150.Binding.Execution", "1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)),
            camera, new PlcResultContractBinding(release.Recipe, content.Algorithm.Algorithm, validation),
            calibrationBindings: null, framePoolCapacity: 1, framePoolMaximumBytes: 1);
    }

    private static string V150BindingHash(char fill) => new(fill, 64);

    private enum V150BindingCatalogCase
    {
        OtherIdentity = 1,
        OtherVersion = 2,
        OtherContentHash = 3
    }

    private enum V150BindingImageCase
    {
        DeclaredWithoutLocalStage = 1,
        DeclarationMissingWithLocalStage = 2,
        StageBindingMismatch = 3,
        CatalogMissingTheExactPolicy = 4
    }
}
