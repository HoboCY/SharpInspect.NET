using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Conformance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

[Collection("ConformanceAssemblyLoading")]
public sealed class ConformanceFacilityTests
{
    [Fact]
    public async Task V107_E01_Reserve_precedes_observe_and_pass_binds_raw_evidence_but_never_issues_qualification()
    {
        using var fixture = new ConformanceFixture();
        fixture.Scenario.Observe = context =>
        {
            using var query = new ConformanceQuery(fixture.Options);
            var pending = Assert.Single(query.GetExecutions());
            Assert.Equal(context.TestExecutionId, pending.Reservation.TestExecutionId);
            Assert.Equal(ConformanceOutcome.NotRun, pending.Outcome);
            return Task.FromResult(new ConformanceObservation("expected"));
        };
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Pass, result.Outcome);
        using var reader = new ConformanceQuery(fixture.Options);
        var view = Assert.Single(reader.GetExecutions());
        Assert.Equal(result.TestExecutionId, view.Record!.TestExecutionId);
        Assert.Contains("expected", Encoding.UTF8.GetString(reader.ReadArtifact(result.Outputs.Single().Sha256)));
        var summary = reader.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Framework);
        Assert.True(summary.AllSelectedCasesSatisfied);
        Assert.False(summary.CanIssueQualification);
        Assert.Equal("IncompleteDevelopmentEvidence", summary.QualificationStatus);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetExecutions(limit: 201));
    }

    [Fact]
    public async Task V107_E02_Product_fail_survives_green_rerun_and_context_change_and_new_candidate_is_distinct()
    {
        using var fixture = new ConformanceFixture();
        fixture.Scenario.Observe = _ => Task.FromResult(new ConformanceObservation("product mismatch"));
        var failure = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Fail, failure.Outcome);
        fixture.Scenario.Observe = _ => Task.FromResult(new ConformanceObservation("expected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        fixture.RefreshContext("changed-calculation-context");
        var pass = await fixture.RunAsync(failure.TestExecutionId);
        Assert.Equal(ConformanceOutcome.Pass, pass.Outcome);
        var failed = fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Framework);
        Assert.True(failed.HasProductFailure);
        Assert.False(failed.AllSelectedCasesSatisfied);
        Assert.Equal(2, failed.ExecutionLineage.Count);
        fixture.ChangeCandidate("product correction");
        var corrected = await fixture.RunAsync(pass.TestExecutionId);
        Assert.Equal(ConformanceOutcome.Pass, corrected.Outcome);
        Assert.False(fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Framework).HasProductFailure);
        Assert.Equal(3, fixture.Facility.GetExecutions().Count);
    }

    [Fact]
    public async Task V107_E03_Cancelled_late_success_cannot_overwrite_terminal_and_keeps_actual_slot()
    {
        using var fixture = new ConformanceFixture();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ConformanceObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scenario.Observe = _ => { entered.TrySetResult(true); return late.Task; };
        using var cancellation = new CancellationTokenSource();
        var executing = fixture.RunAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await executing;
        Assert.Equal(ConformanceOutcome.Blocked, cancelled.Outcome);
        Assert.Equal("ConformanceExecutionCancelled", cancelled.ReasonCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync(cancelled.TestExecutionId));
        late.SetResult(new ConformanceObservation("expected"));
        await late.Task;
        var terminal = Assert.Single(fixture.Facility.GetExecutions());
        Assert.Equal(ConformanceOutcome.Blocked, terminal.Outcome);
        Assert.Equal(cancelled.TestExecutionId, terminal.Record!.TestExecutionId);
        Assert.Equal(cancelled.ReasonCode, terminal.Record.ReasonCode);
    }

    [Fact]
    public async Task V107_E04_Changed_fixture_is_objective_invalid_harness_and_preserves_binding_evidence()
    {
        using var fixture = new ConformanceFixture();
        File.WriteAllText(fixture.ThresholdPath, "changed after freeze");
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.InvalidHarness, result.Outcome);
        Assert.Equal("ConformanceContextBytesChanged", result.ReasonCode);
        Assert.False(fixture.Scenario.Invoked);
        var reference = Assert.Single(result.Outputs, o => o.Name == "binding-mismatch");
        var raw = Encoding.UTF8.GetString(fixture.Facility.ReadArtifact(reference.Sha256));
        Assert.Contains("Expected", raw);
        Assert.Contains("Actual", raw);
    }

    [Fact]
    public async Task V107_E05_Product_bytes_changed_during_observation_block_old_candidate()
    {
        using var fixture = new ConformanceFixture();
        fixture.Scenario.Observe = _ =>
        {
            File.WriteAllText(fixture.ProductPath, "changed product bytes");
            return Task.FromResult(new ConformanceObservation("expected"));
        };
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Equal("ConformanceCandidateBytesChanged", result.ReasonCode);
        Assert.False(fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Framework).AllSelectedCasesSatisfied);
    }

    [Fact]
    public async Task V107_E06_Ordinary_exception_is_blocked_not_invalid_and_sensitive_evidence_is_not_retained()
    {
        using var fixture = new ConformanceFixture();
        fixture.Scenario.Observe = _ => throw new InvalidOperationException("secret-password-value");
        var blocked = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Blocked, blocked.Outcome);
        Assert.DoesNotContain("secret-password", Encoding.UTF8.GetString(fixture.Facility.ReadArtifact(blocked.Outputs.Single().Sha256)));
        fixture.Scenario.Observe = _ => Task.FromResult(new ConformanceObservation("expected", new[]
        {
            new ConformanceEvidence("password", Encoding.UTF8.GetBytes("do-not-retain"), ConformanceEvidenceClassification.Sensitive)
        }));
        var sensitive = await fixture.RunAsync(blocked.TestExecutionId);
        Assert.Equal(ConformanceOutcome.Blocked, sensitive.Outcome);
        Assert.DoesNotContain(sensitive.Outputs, o => o.Name == "password");
        Assert.DoesNotContain("do-not-retain", Encoding.UTF8.GetString(fixture.Facility.ReadArtifact(sensitive.Outputs.Single().Sha256)));
    }

    [Fact]
    public async Task V107_E07_Unsupported_method_and_unrun_case_cannot_pass_or_issue_certificate()
    {
        using var fixture = new ConformanceFixture(acceptanceRule: "future-governed-rule");
        var before = fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Framework);
        Assert.Equal(ConformanceOutcome.NotRun, before.Gates.Single().Outcome);
        Assert.False(before.AllSelectedCasesSatisfied);
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Equal("ConformanceVerificationMethodUnavailable", result.ReasonCode);
        Assert.False(fixture.Scenario.Invoked);
        Assert.Throws<ArgumentException>(() => fixture.Facility.Aggregate(fixture.Candidate.Sha256, fixture.Context.Sha256, QualificationLayer.Station));
    }

    [Fact]
    public async Task V107_E08_Same_assembly_scenario_type_cannot_impersonate_frozen_scenario()
    {
        using var fixture = new ConformanceFixture();
        fixture.Facility.Dispose();
        var impostor = new ImpersonatingConformanceScenario();
        using var replacement = new ConformanceFacility(fixture.Options, new[] { impostor });
        var result = await replacement.ExecuteAsync(fixture.Request());
        Assert.Equal(ConformanceOutcome.InvalidHarness, result.Outcome);
        Assert.Equal("ConformanceScenarioChanged", result.ReasonCode);
        Assert.False(impostor.Invoked);
    }

    [Fact]
    public async Task V107_E09_Cancel_before_terminal_decision_wins_but_cancel_after_commit_cannot_rewrite_pass()
    {
        using var fixture = new ConformanceFixture();
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Facility.BeforeTerminalDecision = () => { reached.TrySetResult(true); return release.Task; };
        using var cancelled = new CancellationTokenSource();
        var pending = fixture.RunAsync(cancellationToken: cancelled.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        release.SetResult(true);
        var blocked = await pending;
        Assert.Equal(ConformanceOutcome.Blocked, blocked.Outcome);
        Assert.Equal("ConformanceExecutionCancelled", blocked.ReasonCode);
        fixture.Facility.BeforeTerminalDecision = null;
        using var afterCommit = new CancellationTokenSource();
        var pass = await fixture.RunAsync(blocked.TestExecutionId, afterCommit.Token);
        Assert.Equal(ConformanceOutcome.Pass, pass.Outcome);
        afterCommit.Cancel();
        Assert.Equal(ConformanceOutcome.Pass, fixture.Facility.GetExecutions().Last().Outcome);
    }

    [Fact]
    public async Task V107_E10_Unlisted_candidate_file_addition_invalidates_whole_candidate_manifest()
    {
        using var fixture = new ConformanceFixture();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.ProductPath)!, "unlisted-migration.sql"), "new migration");
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Equal("ConformanceCandidateBytesChanged", result.ReasonCode);
        Assert.False(fixture.Scenario.Invoked);
    }

    [Fact]
    public async Task V107_E11_Read_projection_independently_rejects_wrong_outcome_and_artifact_order()
    {
        using var fixture = new ConformanceFixture();
        var pass = await fixture.RunAsync();
        using var rawReader = new ConformanceLedger(fixture.Options, readOnly: true);
        var entries = rawReader.ReadAll();
        var wrongOutcome = entries.Select(e => e.Kind == "result" ? e with
        {
            Payload = System.Text.Json.JsonSerializer.Serialize(pass with { Outcome = ConformanceOutcome.InvalidHarness })
        } : e).ToArray();
        Assert.Throws<InvalidOperationException>(() => new ConformanceReadModel(wrongOutcome));
        var resultSequence = entries.Single(e => e.Kind == "result").Sequence;
        var lateArtifact = entries.Select(e => e.Kind == "artifact" ? e with { Sequence = resultSequence + 1 } : e).ToArray();
        Assert.Throws<InvalidOperationException>(() => new ConformanceReadModel(lateArtifact));
    }

    [Fact]
    public async Task V107_E12_Candidate_components_cannot_bind_outside_the_frozen_candidate_directory()
    {
        using var fixture = new ConformanceFixture();
        var request = fixture.RequestWithExternalProductCopy();
        var result = await fixture.Facility.ExecuteAsync(request);
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Equal("ConformanceCandidateBindingOutsideDirectory", result.ReasonCode);
        Assert.False(fixture.Scenario.Invoked);
    }

    [Fact]
    public async Task V107_E13_New_dependency_loaded_outside_application_root_cannot_be_ignored()
    {
        using var fixture = new ConformanceFixture();
        var helper = Path.Combine(fixture.Request().Bindings.RootDirectory, "external-helper.dll");
        File.Copy(typeof(ConformanceOutcome).Assembly.Location, helper);
        fixture.Scenario.Observe = context =>
        {
            _ = System.Reflection.Assembly.LoadFile(helper);
            return Task.FromResult(new ConformanceObservation("expected"));
        };
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.InvalidHarness, result.Outcome);
        Assert.Equal("ConformanceHarnessChanged", result.ReasonCode);
        var mismatch = Assert.Single(result.Outputs, a => a.Name == "binding-mismatch");
        Assert.DoesNotContain(helper, Encoding.UTF8.GetString(fixture.Facility.ReadArtifact(mismatch.Sha256)));
    }

    [Fact]
    public async Task V107_E14_Qualification_context_files_cannot_be_part_of_product_candidate()
    {
        using var fixture = new ConformanceFixture();
        var request = fixture.RequestWithContextInsideCandidate();
        var result = await fixture.Facility.ExecuteAsync(request);
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Equal("ConformanceContextBindingInsideCandidateDirectory", result.ReasonCode);
        Assert.False(fixture.Scenario.Invoked);
    }

    [Fact]
    public async Task V107_E15_Scenario_cannot_poison_internal_mismatch_evidence_name()
    {
        using var fixture = new ConformanceFixture();
        fixture.Scenario.Observe = _ =>
        {
            File.WriteAllText(fixture.ThresholdPath, "changed context");
            return Task.FromResult(new ConformanceObservation("expected", new[]
            {
                new ConformanceEvidence("binding-mismatch", Encoding.UTF8.GetBytes("not facility evidence"))
            }));
        };
        var result = await fixture.RunAsync();
        Assert.Equal(ConformanceOutcome.Blocked, result.Outcome);
        Assert.Single(result.Outputs);
        Assert.Equal("facility-observation", result.Outputs[0].Name);
        Assert.Equal(ConformanceOutcome.Blocked, Assert.Single(fixture.Facility.GetExecutions()).Outcome);
    }
}

internal sealed class ConformanceFixture : IDisposable
{
    private readonly List<ConformanceFileBinding> _bindings = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpInspectConformanceTests", Guid.NewGuid().ToString("N"));
    public ConformanceFixture(string acceptanceRule = "exact-text-v1")
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "candidate"));
        Options = new ConformanceLedgerOptions(Path.Combine(_root, "ledger.sqlite"), Path.Combine(_root, "keys"), "conformance-development-test");
        Scenario = new ControlledConformanceScenario();
        Facility = new ConformanceFacility(Options, new[] { Scenario });
        var requirement = new ConformanceRequirement("ADR-0111-E", "ADR-0111:13", "Preserve an immutable execution", true);
        var test = new VerificationCase(Scenario.TestId, QualificationLayer.Framework, VerificationMethod.Executable,
            new[] { requirement.RequirementId }, true, "required development test", "", "observed-environment",
            "isolated development ledger", "observe controlled text", "expected", "public-test-data-v1", acceptanceRule);
        Profile = ConformanceDocuments.FreezeProfile(new ConformanceProfile("V107-development", 1, ConformanceClaim.DevelopmentOnly,
            "Development ledger behavior only", new[] { "current-process" }, new[] { "ledger" }, new[] { QualificationLayer.Framework },
            "retain immutable test data", "no formal authority", new[] { requirement }, new[] { test }));
        ProductPath = Write("candidate/product", "product baseline");
        ThresholdPath = Write("threshold", "exact equality");
        ChangeCandidate("product baseline");
    }
    public ConformanceLedgerOptions Options { get; }
    public ControlledConformanceScenario Scenario { get; }
    public ConformanceFacility Facility { get; }
    public FrozenConformanceDocument Profile { get; }
    public FrozenConformanceDocument Candidate { get; private set; } = null!;
    public FrozenConformanceDocument Context { get; private set; } = null!;
    public string ProductPath { get; }
    public string ThresholdPath { get; }

    public void ChangeCandidate(string product)
    {
        File.WriteAllText(ProductPath, product);
        _bindings.RemoveAll(b => b.Category.StartsWith("candidate/", StringComparison.Ordinal));
        var package = Bind("candidate/Packages", "product", ProductPath);
        var configPath = Write("candidate/build-config", "explicit development configuration");
        var lockPath = Write("candidate/dependency-lock", "isolated test dependency lock fixture");
        var manifest = Bind("candidate/EmbeddedAssets", "candidate-file-manifest",
            Write("candidate-manifest.json", ConformanceBindings.CaptureCandidateManifest(Path.Combine(_root, "candidate"))));
        Candidate = ConformanceDocuments.FreezeCandidate(new ReleaseCandidateDefinition("development-source-fixture",
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), new[] { package },
            Array.Empty<FingerprintComponent>(), new[] { manifest },
            new[] { Bind("candidate/DependencyLocks", "lock", lockPath) },
            new[] { Bind("candidate/BuildConfiguration", "build", configPath) }));
        RefreshContext("initial-calculation-context");
    }

    public void RefreshContext(string calculation)
    {
        _bindings.RemoveAll(b => b.Category.StartsWith("context/", StringComparison.Ordinal));
        var threshold = Bind("context/Thresholds", "threshold", ThresholdPath);
        var rules = Bind("context/CalculationRules", "rule", Write("calculation", calculation));
        Context = ConformanceDocuments.FreezeContext(new QualificationContextDefinition(Profile.Sha256,
            ConformanceBindings.CaptureHarnesses(new[] { Scenario }),
            new[] { ConformanceBindings.DescribeScenario(Scenario) },
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), new[] { threshold }, new[] { rules },
            new[] { new FingerprintComponent("observed-environment", ConformanceBindings.HashText(ConformanceBindings.CaptureEnvironmentJson())) }));
        Facility.Freeze(Profile, Candidate, Context);
    }
    public ConformanceExecutionRequest Request(Guid? predecessor = null) => new(Candidate.Sha256, Context.Sha256, Scenario.TestId,
        new ConformanceBindings(_root, _bindings, Path.Combine(_root, "candidate")), Array.Empty<ConformanceEvidence>(), predecessor);
    public ConformanceExecutionRequest RequestWithExternalProductCopy()
    {
        var external = Path.Combine(_root, "outside-product");
        File.Copy(ProductPath, external);
        var changed = _bindings.Select(b => b.Category == "candidate/Packages" ? b with { Path = external } : b).ToArray();
        return Request() with { Bindings = new ConformanceBindings(_root, changed, Path.Combine(_root, "candidate")) };
    }
    public ConformanceExecutionRequest RequestWithContextInsideCandidate()
    {
        var candidateDirectory = Path.Combine(_root, "candidate");
        var inside = Path.Combine(candidateDirectory, "misplaced-threshold");
        File.Copy(ThresholdPath, inside);
        var manifestPath = Write("candidate-manifest.json", ConformanceBindings.CaptureCandidateManifest(candidateDirectory));
        var manifest = new FingerprintComponent("candidate-file-manifest", ConformanceBindings.HashFile(manifestPath));
        var old = ConformanceDocuments.ReadCandidate(Candidate);
        Candidate = ConformanceDocuments.FreezeCandidate(new ReleaseCandidateDefinition(old.SourceRevision, old.PublicApi,
            old.Schemas, old.Packages, old.Migrations, new[] { manifest }, old.DependencyLocks, old.BuildConfiguration));
        Facility.Freeze(Profile, Candidate, Context);
        return Request() with { Bindings = new ConformanceBindings(_root, _bindings.Select(b =>
            b.Category == "context/Thresholds" ? b with { Path = inside } : b).ToArray(), candidateDirectory) };
    }
    public Task<TestExecutionRecord> RunAsync(Guid? predecessor = null, CancellationToken cancellationToken = default) =>
        Facility.ExecuteAsync(Request(predecessor), cancellationToken);
    private string Write(string name, string text) { var path = Path.Combine(_root, name); File.WriteAllText(path, text); return path; }
    private FingerprintComponent Bind(string category, string name, string path)
    {
        _bindings.Add(new ConformanceFileBinding(category, name, path));
        return new FingerprintComponent(name, ConformanceBindings.HashFile(path));
    }
    public void Dispose() => Facility.Dispose(); // Preserve local immutable development evidence.
}

internal sealed class ControlledConformanceScenario : IConformanceScenario
{
    public string TestId => "V107-controlled";
    public string ScenarioHash => ConformanceBindings.HashText("controlled development observation v1");
    public bool Invoked { get; private set; }
    public Func<ConformanceExecutionContext, Task<ConformanceObservation>> Observe { get; set; } =
        _ => Task.FromResult(new ConformanceObservation("expected"));
    public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context, CancellationToken cancellationToken)
    {
        Invoked = true;
        return Observe(context);
    }
}

internal sealed class ImpersonatingConformanceScenario : IConformanceScenario
{
    public string TestId => "V107-controlled";
    public string ScenarioHash => ConformanceBindings.HashText("controlled development observation v1");
    public bool Invoked { get; private set; }
    public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context, CancellationToken cancellationToken)
    {
        Invoked = true;
        return Task.FromResult(new ConformanceObservation("expected"));
    }
}

[CollectionDefinition("ConformanceAssemblyLoading", DisableParallelization = true)]
public sealed class ConformanceAssemblyLoadingCollection { }
