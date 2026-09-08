using System.Collections.ObjectModel;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Conformance;

namespace SharpInspect.Cameras.Conformance;

public sealed record CameraConformanceCaseDefinition(CameraConformanceCase Case, string TestId,
    string RequirementId, string SourceReference, string Invariant, string Stimulus);

/// <summary>Optional extension test code is registered explicitly and frozen with the core profile.</summary>
public sealed class CameraConformanceExtension
{
    public CameraConformanceExtension(string extensionId, string sourceReference, string invariant,
        IConformanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Reuse the profile's identifier and text validation, before retaining any registration.
        Requirement = new ConformanceRequirement("V122-EXT-" + extensionId, sourceReference, invariant, false);
        if (!scenario.TestId.StartsWith("V122-X-", StringComparison.Ordinal))
            throw new ArgumentException("CameraConformanceExtensionTestIdRequired", nameof(scenario));
        _ = new FingerprintComponent(scenario.TestId, scenario.ScenarioHash);
        ExtensionId = extensionId;
        Scenario = scenario;
    }
    public string ExtensionId { get; }
    public ConformanceRequirement Requirement { get; }
    public IConformanceScenario Scenario { get; }
}

/// <summary>
/// Reusable public-contract scenarios. Results are development evidence, never hardware qualification.
/// Callback and native allocation checks remain Blocked until an independent public observer exists.
/// </summary>
public sealed class CameraConformanceSuite
{
    public const string Version = "1.0.0";
    public const string ExpectedObservation = "CameraContractSatisfied";
    public const string FixtureInputName = "camera-fixture";
    public const string CatalogInputName = "camera-case-catalog";
    private readonly ICameraConformanceFixtureFactory _factory;
    private readonly IReadOnlyList<CameraConformanceExtension> _extensions;
    private readonly SemaphoreSlim _physicalExecution = new(1, 1);

    public CameraConformanceSuite(ICameraConformanceFixtureFactory factory,
        IEnumerable<CameraConformanceExtension>? extensions = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Declaration = factory.Declaration ?? throw new ArgumentException("CameraConformanceDeclarationRequired", nameof(factory));
        var registered = (extensions ?? Array.Empty<CameraConformanceExtension>()).Take(17).ToArray();
        if (registered.Length > 16 || registered.Any(value => value is null) ||
            registered.Select(value => value.ExtensionId).Distinct(StringComparer.Ordinal).Count() != registered.Length ||
            registered.Select(value => value.Scenario.TestId).Distinct(StringComparer.Ordinal).Count() != registered.Length)
            throw new ArgumentException("CameraConformanceExtensionRegistrationInvalid", nameof(extensions));
        if (Declaration.OptionalExtensions.Any(id => !registered.Any(value => value.ExtensionId == id)))
            throw new ArgumentException("CameraConformanceDeclaredExtensionTestMissing", nameof(extensions));
        _extensions = Array.AsReadOnly(registered);
        Scenarios = Array.AsReadOnly(Cases.Select(definition => (IConformanceScenario)new CoreScenario(this, definition))
            .Concat(registered.Select(extension => extension.Scenario)).ToArray());
    }

    public CameraConformanceFixtureDeclaration Declaration { get; }
    public IReadOnlyList<IConformanceScenario> Scenarios { get; }
    public static IReadOnlyList<CameraConformanceCaseDefinition> Cases { get; } = Array.AsReadOnly(new[]
    {
        Define(CameraConformanceCase.DiscoveryIdentity, 1, "ADR-0058", "Discovery and open preserve exact provider and device identity.", "Discover and open the frozen target."),
        Define(CameraConformanceCase.ConfigurationReadBack, 2, "ADR-0056", "Applied configuration has complete matching effective readback.", "Apply every frozen configuration and inspect public health/readback."),
        Define(CameraConformanceCase.ConfigurationFailure, 3, "ADR-0056", "Readback failure closes the device, exposes Unknown and cannot arm acquisition.", "Inject configuration readback failure."),
        Define(CameraConformanceCase.SoftwareAcquisition, 4, "ADR-0053;ADR-0057", "Software acquisition returns one correctly correlated canonical lease.", "Run a qualification software acquisition."),
        Define(CameraConformanceCase.HardwareAcquisition, 5, "ADR-0053;ADR-0057", "Hardware acquisition returns one lease for the active correlation.", "Arm qualification acquisition then deliver a hardware pulse."),
        Define(CameraConformanceCase.CanonicalMemory, 6, "ADR-0054", "Rows, layout, valid bits and pixels match the frozen canonical corpus.", "Acquire every frozen canonical format and compare valid row hashes."),
        Define(CameraConformanceCase.Cancellation, 7, "ADR-0053;ADR-0057", "Cancellation yields no lease and late frames cannot cross requests.", "Cancel the pending request, deliver late activity, then acquire again."),
        Define(CameraConformanceCase.ExtraFrame, 8, "ADR-0053;ADR-0057", "Extra frames cannot become a later request's successful lease.", "Deliver a two-frame burst and inspect subsequent acquisition behavior."),
        Define(CameraConformanceCase.Timeout, 9, "ADR-0057", "The acquisition deadline terminates without a lease.", "Advance or wait beyond the deadline without a frame."),
        Define(CameraConformanceCase.DisconnectRecovery, 10, "ADR-0056;ADR-0058", "Recovery reopens only the bound identity, reapplies full configuration and retains complete ordered recovery events.", "Disconnect during acquisition, restore the same device, run bounded recovery."),
        Define(CameraConformanceCase.LeaseLifetime, 11, "ADR-0054;ADR-0055", "Disposed leases reject access and retained leases preserve their pixels.", "Read, retain, release and attempt access after disposal."),
        Define(CameraConformanceCase.PoolExhaustion, 12, "ADR-0055", "Qualification acquisition has bounded lease capacity without a fallback lease.", "Retain poolCapacity leases and request one more; release and acquire again."),
        Define(CameraConformanceCase.CallbackIsolation, 13, "ADR-0053", "SDK callback does not inline downstream continuation or external work.", "Observe callback entry/exit and downstream thread provenance with independent instrumentation."),
        Define(CameraConformanceCase.AdapterCallbackBudget, 14, "ADR-0053", "Callback work respects the frozen adapter budget and classifies overruns.", "Apply callback pressure and observe callback duration and budget fault independently."),
        Define(CameraConformanceCase.NativeAllocationBounds, 15, "ADR-0055", "Native and managed frame allocations remain within the frozen pool budget.", "Measure allocation ownership across exhaustion and retirement using independent instrumentation.")
    });

    public byte[] GetCatalogBytes() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "camera-conformance-catalog-v1", suiteVersion = Version, cases = Cases,
        extensions = _extensions.Select(value => new
        { value.ExtensionId, value.Requirement, value.Scenario.TestId, value.Scenario.ScenarioHash })
    });

    public IReadOnlyList<ConformanceEvidence> CreateInputs() => Array.AsReadOnly(new[]
    {
        new ConformanceEvidence(FixtureInputName, Declaration.GetCanonicalBytes()),
        new ConformanceEvidence(CatalogInputName, GetCatalogBytes())
    });

    /// <param name="exclusionEvidence">For each unclaimed optional test, the hash of its candidate-bound
    /// applicability-proof artifact. The facility validates the actual proof at execution.</param>
    public ConformanceProfile CreateProfile(IReadOnlyDictionary<string, string>? exclusionEvidence = null)
    {
        var requirements = Cases.Select(value => new ConformanceRequirement(value.RequirementId,
            value.SourceReference, value.Invariant, true)).Concat(_extensions.Select(value => value.Requirement)).ToArray();
        var cases = Cases.Select(value => Verification(value.TestId, value.RequirementId, true,
            "Required core camera contract; missing observations are Blocked.", "", value.Stimulus)).ToList();
        foreach (var extension in _extensions)
        {
            var applicable = Declaration.OptionalExtensions.Contains(extension.ExtensionId, StringComparer.Ordinal);
            var proof = "";
            if (!applicable && (exclusionEvidence is null ||
                !exclusionEvidence.TryGetValue(extension.Scenario.TestId, out proof)))
                throw new ArgumentException("CameraConformanceFrozenExclusionProofRequired", nameof(exclusionEvidence));
            if (!applicable) _ = new FingerprintComponent("applicability-proof", proof);
            cases.Add(Verification(extension.Scenario.TestId, extension.Requirement.RequirementId, applicable,
                "Optional extension " + extension.ExtensionId + ": frozen declaration membership; exclusion requires candidate-bound unreachable proof.",
                proof, "Execute explicitly registered optional extension public observations."));
        }
        return new ConformanceProfile("SharpInspect-Camera-Provider", 1, ConformanceClaim.DevelopmentOnly,
            "Public camera contracts for the frozen provider/fixture. Real SDK/model/interface/OS/trigger combinations require independent Hardware Qualification. Callback/native instrumentation gaps remain Blocked.",
            new[] { "net6.0", "Windows-NTFS-evidence-ledger" }, new[] { "camera-core", "optional-extensions-separate" },
            new[] { QualificationLayer.Provider }, "Retain immutable attempts, raw artifacts and all frozen hashes.",
            "DevelopmentOnly-no-qualification-authority", requirements, cases);
    }

    private static VerificationCase Verification(string testId, string requirementId, bool applicable,
        string rule, string proof, string stimulus) => new(testId, QualificationLayer.Provider,
        VerificationMethod.Executable, new[] { requirementId }, applicable, rule, proof,
        "observed-environment", "Frozen provider, fixture, input corpus, clock, dependencies and suite.", stimulus,
        ExpectedObservation, "public-test-data-v1", "exact-text-v1");

    private static CameraConformanceCaseDefinition Define(CameraConformanceCase kind, int id,
        string source, string invariant, string stimulus) =>
        new(kind, $"V122-C{id:00}", $"V122-R{id:00}", source, invariant, stimulus);

    private sealed class CoreScenario : IConformanceScenario
    {
        private readonly CameraConformanceSuite _suite;
        private readonly CameraConformanceCaseDefinition _definition;
        internal CoreScenario(CameraConformanceSuite suite, CameraConformanceCaseDefinition definition)
        { _suite = suite; _definition = definition; }
        public string TestId => _definition.TestId;
        public string ScenarioHash => ConformanceBindings.HashText(JsonSerializer.Serialize(new
        { suiteVersion = Version, definition = _definition, declarationHash = _suite._factory.Declaration.ContentHash }));

        public async Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context, CancellationToken cancellationToken)
        {
            if (context.TestId != TestId) throw new ArgumentException("CameraConformanceTestIdentityMismatch", nameof(context));
            foreach (var input in _suite.CreateInputs())
            {
                var actual = context.Inputs.SingleOrDefault(value => value.Name == input.Name);
                if (actual is null || !actual.GetBytes().SequenceEqual(input.GetBytes()))
                    throw new InvalidOperationException("CameraConformanceInputHashMismatch");
            }
            VerifyDeclaration();
            if (!await _suite._physicalExecution.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return Blocked("PhysicalFixtureStillOwned");
            try
            {
                ConformanceObservation observation;
                if (_definition.Case >= CameraConformanceCase.CallbackIsolation)
                    observation = Blocked("IndependentPublicInstrumentationRequired");
                else
                    observation = await CameraConformancePublicObserver.ObserveAsync(_definition.Case,
                        _suite._factory, _suite.Declaration, cancellationToken).ConfigureAwait(false);
                VerifyDeclaration();
                return observation;
            }
            finally { _suite._physicalExecution.Release(); }
        }

        private void VerifyDeclaration()
        {
            if (_suite.Declaration.ContentHash != _suite._factory.Declaration.ContentHash)
                throw new InvalidOperationException("CameraConformanceDeclarationChanged");
        }
        private ConformanceObservation Blocked(string reason) => new("CameraContractUnavailable:" + reason,
            new[] { new ConformanceEvidence("public-observation", JsonSerializer.SerializeToUtf8Bytes(new
            { testId = TestId, reason, declarationHash = _suite.Declaration.ContentHash, scope = "DevelopmentOnly", hardwareQualification = "NotRun" })) },
            ConformanceObservationStatus.Blocked, reason);
    }
}
