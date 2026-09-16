using System.Globalization;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V158 contract tests for diagnostic capture/support bundle public contracts: fail-closed identity,
/// explicit UTC and cap validation, immutable bounded collections, and Step-Up authorization targets.
/// </summary>
public sealed class DiagnosticSupportContractTests
{
    private static readonly string PolicyHash = new string('A', 64);
    private static readonly string OtherPolicyHash = new string('B', 64);
    private static readonly DateTimeOffset WindowStart =
        DateTimeOffset.Parse("2026-09-11T00:00:00Z", CultureInfo.InvariantCulture);

    [Fact, Trait("VerificationId", "V158_C01")]
    public void V158_C01_CaptureProfileCanonicalizesComponentsAndBindsImmutableBoundedState()
    {
        var components = new List<string> { "Runtime", "Camera.Front", "Plc" };
        var profile = Profile(components);
        var reordered = Profile(new[] { "Plc", "Runtime", "Camera.Front" });
        var lowerHash = Profile(components, policyHash: PolicyHash.ToLowerInvariant());

        Assert.Equal(new[] { "Camera.Front", "Plc", "Runtime" }, profile.Components);
        Assert.Equal(profile.ContentHash, reordered.ContentHash);
        Assert.Equal(profile.ContentHash, lowerHash.ContentHash);
        Assert.Equal(profile.LoggingPolicyHash, PolicyHash);
        Assert.Matches("^[0-9A-F]{64}$", profile.ContentHash);

        components.Clear();
        Assert.Equal(3, profile.Components.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)profile.Components).Add("Intruder"));
        Assert.DoesNotContain("Intruder", profile.Components);
    }

    [Fact, Trait("VerificationId", "V158_C02")]
    public void V158_C02_CaptureProfileRejectsInvalidComponentsHashesLevelsAndCaps()
    {
        Assert.Throws<ArgumentException>(() => Profile(components: Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => Profile(components: new[] { "Camera", "Camera" }));
        Assert.Throws<ArgumentException>(() => Profile(components: new[] { "bad component" }));
        Assert.Throws<ArgumentException>(() => Profile(components: new[] { new string('a', 129) }));
        Assert.Throws<ArgumentException>(() => Profile(components: new[] { "Camera", null! }));
        Assert.Throws<ArgumentException>(() => Profile(components: Enumerable.Range(0, 33)
            .Select(index => "component-" + index)));
        Assert.Throws<ArgumentException>(() => new DiagnosticCaptureProfile(
            PolicyHash, null!, DiagnosticLevel.Debug, TimeSpan.FromMinutes(1), 10));

        Assert.Throws<ArgumentNullException>(() => new DiagnosticCaptureProfile(
            null!, new[] { "Camera" }, DiagnosticLevel.Debug, TimeSpan.FromMinutes(1), 10));
        Assert.Throws<ArgumentException>(() => Profile(policyHash: new string('A', 63)));
        Assert.Throws<ArgumentException>(() => Profile(policyHash: new string('G', 64)));

        Assert.Throws<ArgumentException>(() => Profile(level: DiagnosticLevel.Information));
        Assert.Throws<ArgumentException>(() => Profile(level: DiagnosticLevel.Critical));
        Assert.Throws<ArgumentException>(() => Profile(level: (DiagnosticLevel)99));
        Assert.Throws<ArgumentException>(() => Profile(duration: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Profile(duration: TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => Profile(duration: TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentException>(() => Profile(maximumEvents: 0));
        Assert.Throws<ArgumentException>(() => Profile(maximumEvents: -1));
        Assert.Throws<ArgumentException>(() => Profile(maximumEvents: 1_000_001));
        Assert.Throws<ArgumentException>(() => Profile(maximumEvents: int.MaxValue));

        var exactBounds = Profile(components: Enumerable.Range(0, 32).Select(index => "component-" + index),
            level: DiagnosticLevel.Trace, duration: TimeSpan.FromHours(1), maximumEvents: 1_000_000);
        Assert.Equal(32, exactBounds.Components.Count);
        Assert.Matches("^[0-9A-F]{64}$", exactBounds.ContentHash);
    }

    [Fact, Trait("VerificationId", "V158_C03")]
    public void V158_C03_CaptureProfileEnumerationStopsAtBoundedNPlusOne()
    {
        var pulled = 0;
        var disposed = false;
        IEnumerable<string> Hostile()
        {
            try
            {
                while (true)
                {
                    pulled++;
                    yield return "component-" + pulled;
                }
            }
            finally { disposed = true; }
        }

        Assert.Throws<ArgumentException>(() => Profile(components: Hostile()));
        Assert.Equal(33, pulled);
        Assert.True(disposed);
    }

    [Fact, Trait("VerificationId", "V158_C04")]
    public void V158_C04_ScopeRequiresZeroOffsetAndBoundsUtcWindowAndRuntimeEpoch()
    {
        var epoch = Guid.NewGuid();
        var offsetStart = DateTimeOffset.Parse("2026-09-11T02:00:00+02:00", CultureInfo.InvariantCulture);
        var offsetThrough = DateTimeOffset.Parse("2026-09-12T02:00:00+02:00", CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentException>(() => Scope(epoch, offsetStart, WindowStart.AddDays(1)));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, offsetThrough));
        Assert.Throws<ArgumentException>(() => Scope(Guid.Empty, WindowStart, WindowStart.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart,
            WindowStart.AddDays(31) + TimeSpan.FromTicks(1)));

        var exactUpper = Scope(epoch, WindowStart, WindowStart.AddDays(31));
        Assert.Equal(WindowStart.AddDays(31), exactUpper.ThroughUtc);
        var minimalScope = Scope(epoch, WindowStart, WindowStart.AddHours(1));
        Assert.Empty(minimalScope.Executions);
        Assert.Empty(minimalScope.CommandCorrelations);
        Assert.Empty(minimalScope.CaptureSessions);
        Assert.Matches("^[0-9A-F]{64}$", minimalScope.ContentHash);
    }

    [Fact, Trait("VerificationId", "V158_C05")]
    public void V158_C05_ScopeRejectsDuplicateUnknownAndEmptyIdentities()
    {
        var epoch = Guid.NewGuid();
        var execution = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var command = Guid.NewGuid();
        var capture = Guid.NewGuid();

        var valid = Scope(epoch, WindowStart, WindowStart.AddHours(1),
            new[] { execution }, new[] { command }, new[] { capture });
        Assert.Single(valid.Executions);
        Assert.Single(valid.CommandCorrelations);
        Assert.Single(valid.CaptureSessions);

        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            new[] { execution, execution }, null, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            new[] { new ExecutionCorrelationId((ExecutionKind)(-1), Guid.NewGuid()) }, null, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            new[] { new ExecutionCorrelationId((ExecutionKind)99, Guid.NewGuid()) }, null, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            new[] { new ExecutionCorrelationId(ExecutionKind.Production, Guid.Empty) }, null, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            null, new[] { Guid.Empty }, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            null, null, new[] { Guid.Empty }));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            null, new[] { command, command }, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            null, null, new[] { capture, capture }));
    }

    [Fact, Trait("VerificationId", "V158_C06")]
    public void V158_C06_ScopeCapsAggregateIdentityCountAtSixtyFour()
    {
        var epoch = Guid.NewGuid();
        var executions = Enumerable.Range(0, 65)
            .Select(_ => new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid())).ToArray();
        var commands = Enumerable.Range(0, 33).Select(_ => Guid.NewGuid()).ToArray();
        var captures = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();

        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            executions, null, null));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            null, commands, captures));
        Assert.Throws<ArgumentException>(() => Scope(epoch, WindowStart, WindowStart.AddHours(1),
            executions.Take(64), new[] { Guid.NewGuid() }, null));

        var maximum = Scope(epoch, WindowStart, WindowStart.AddHours(1), executions.Take(64), null, null);
        Assert.Equal(64, maximum.Executions.Count);
        var mixed = Scope(epoch, WindowStart, WindowStart.AddHours(1), null,
            commands.Take(32), captures.Take(32));
        Assert.Equal(32, mixed.CommandCorrelations.Count);
        Assert.Equal(32, mixed.CaptureSessions.Count);
    }

    [Fact, Trait("VerificationId", "V158_C07")]
    public void V158_C07_ScopeIdentityEnumerationStopsAtBoundedNPlusOne()
    {
        var executionsPulled = 0;
        var executionsDisposed = 0;
        IEnumerable<ExecutionCorrelationId> HostileExecutions()
        {
            try
            {
                while (true)
                {
                    executionsPulled++;
                    yield return new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
                }
            }
            finally { executionsDisposed++; }
        }

        var commandsPulled = 0;
        var commandsDisposed = 0;
        IEnumerable<Guid> HostileCommands()
        {
            try
            {
                while (true)
                {
                    commandsPulled++;
                    yield return Guid.NewGuid();
                }
            }
            finally { commandsDisposed++; }
        }

        Assert.Throws<ArgumentException>(() => Scope(Guid.NewGuid(), WindowStart, WindowStart.AddHours(1),
            HostileExecutions(), null, null));
        Assert.Equal(65, executionsPulled);
        Assert.Equal(1, executionsDisposed);

        Assert.Throws<ArgumentException>(() => Scope(Guid.NewGuid(), WindowStart, WindowStart.AddHours(1),
            null, HostileCommands(), null));
        Assert.Equal(65, commandsPulled);
        Assert.Equal(1, commandsDisposed);
    }

    [Fact, Trait("VerificationId", "V158_C08")]
    public void V158_C08_ScopeCanonicalOrderAndHashAreDeterministicForEquivalentInputs()
    {
        var epoch = Guid.NewGuid();
        var executions = new[]
        {
            new ExecutionCorrelationId(ExecutionKind.Qualification,
                Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new ExecutionCorrelationId(ExecutionKind.Production,
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new ExecutionCorrelationId(ExecutionKind.Production,
                Guid.Parse("00000000-0000-0000-0000-000000000001"))
        };
        var commands = new[]
        {
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        var captures = new[] { Guid.Parse("44444444-4444-4444-4444-444444444444") };

        var first = Scope(epoch, WindowStart, WindowStart.AddHours(6), executions, commands, captures);
        var permuted = Scope(epoch, WindowStart, WindowStart.AddHours(6),
            executions.Reverse(), commands.Reverse(), captures.Reverse());
        var changedWindow = Scope(epoch, WindowStart, WindowStart.AddHours(7), executions, commands, captures);

        Assert.Equal(first.ContentHash, permuted.ContentHash);
        Assert.NotEqual(first.ContentHash, changedWindow.ContentHash);
        Assert.Equal(new[]
        {
            new ExecutionCorrelationId(ExecutionKind.Production,
                Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ExecutionCorrelationId(ExecutionKind.Production,
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new ExecutionCorrelationId(ExecutionKind.Qualification,
                Guid.Parse("11111111-1111-1111-1111-111111111111"))
        }, first.Executions);
        Assert.Equal(commands.OrderBy(value => value), first.CommandCorrelations);
        Assert.Equal(captures.OrderBy(value => value), first.CaptureSessions);
    }

    [Fact, Trait("VerificationId", "V158_C09")]
    public void V158_C09_ScopeCollectionsAreDefensivelyCopiedAndReadOnly()
    {
        var executions = new List<ExecutionCorrelationId>
        {
            new(ExecutionKind.Production, Guid.NewGuid())
        };
        var commands = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var captures = new List<Guid> { Guid.NewGuid() };
        var scope = Scope(Guid.NewGuid(), WindowStart, WindowStart.AddHours(1), executions, commands, captures);
        var hash = scope.ContentHash;

        executions.Clear();
        commands.Clear();
        captures.Clear();

        Assert.Single(scope.Executions);
        Assert.Equal(2, scope.CommandCorrelations.Count);
        Assert.Single(scope.CaptureSessions);
        Assert.Equal(hash, scope.ContentHash);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)scope.CommandCorrelations).Add(Guid.NewGuid()));
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionCorrelationId>)scope.Executions).Add(
            new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid())));
    }

    [Fact, Trait("VerificationId", "V158_C10")]
    public void V158_C10_StartCaptureTargetChangesWithComponentScopeLevelBudgetPolicyAndReason()
    {
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var baseline = Start(sessionId, Profile(), correlationId: correlationId);
        var equivalent = Start(sessionId, Profile(), correlationId: correlationId);

        Assert.Equal(baseline.AuthorizationTarget, equivalent.AuthorizationTarget);
        Assert.Matches("^[0-9A-F]{64}$", baseline.AuthorizationTarget);
        Assert.Equal(baseline.AuthorizationTarget, Start(sessionId,
            Profile(components: new[] { "Runtime", "Camera" }), correlationId: correlationId).AuthorizationTarget);

        Assert.NotEqual(baseline.AuthorizationTarget,
            Start(Guid.NewGuid(), Profile(), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId,
            Profile(components: new[] { "Camera" }), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId,
            Profile(level: DiagnosticLevel.Trace), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId,
            Profile(duration: TimeSpan.FromMinutes(6)), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId,
            Profile(maximumEvents: 1001), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId,
            Profile(policyHash: OtherPolicyHash), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Start(sessionId, Profile(),
            DiagnosticSupportReason.FaultInvestigation, correlationId).AuthorizationTarget);
    }

    [Fact, Trait("VerificationId", "V158_C11")]
    public void V158_C11_StopCaptureTargetIsDistinctDeterministicAndBindsSessionAndReason()
    {
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var stop = new StopDiagnosticCaptureCommand(correlationId, Invocation(), sessionId,
            DiagnosticSupportReason.MaintenanceInvestigation);
        var equivalent = new StopDiagnosticCaptureCommand(correlationId, Invocation(), sessionId,
            DiagnosticSupportReason.MaintenanceInvestigation);

        Assert.Equal(stop.AuthorizationTarget, equivalent.AuthorizationTarget);
        Assert.NotEqual(stop.AuthorizationTarget,
            Start(sessionId, Profile(), correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(stop.AuthorizationTarget, new StopDiagnosticCaptureCommand(correlationId, Invocation(),
            Guid.NewGuid(), DiagnosticSupportReason.MaintenanceInvestigation).AuthorizationTarget);
        Assert.NotEqual(stop.AuthorizationTarget, new StopDiagnosticCaptureCommand(correlationId, Invocation(),
            sessionId, DiagnosticSupportReason.FaultInvestigation).AuthorizationTarget);
    }

    [Fact, Trait("VerificationId", "V158_C12")]
    public void V158_C12_BundleTargetChangesWithScopeBudgetPolicyAndReason()
    {
        var epoch = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var execution = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var commandCorrelation = Guid.NewGuid();
        var captureSession = Guid.NewGuid();
        var scope = Scope(epoch, WindowStart, WindowStart.AddHours(4),
            new[] { execution }, new[] { commandCorrelation }, new[] { captureSession });

        var baseline = Bundle(operationId, scope, PolicyHash, correlationId: correlationId);
        var equivalent = Bundle(operationId, scope, PolicyHash, correlationId: correlationId);
        var lowerPolicyHash = Bundle(operationId, scope, PolicyHash.ToLowerInvariant(),
            correlationId: correlationId);

        Assert.Equal(baseline.AuthorizationTarget, equivalent.AuthorizationTarget);
        Assert.Equal(baseline.AuthorizationTarget, lowerPolicyHash.AuthorizationTarget);

        Assert.NotEqual(baseline.AuthorizationTarget, Bundle(operationId,
            Scope(epoch, WindowStart, WindowStart.AddHours(5),
                new[] { execution }, new[] { commandCorrelation }, new[] { captureSession }),
            PolicyHash, correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Bundle(operationId,
            Scope(Guid.NewGuid(), WindowStart, WindowStart.AddHours(4),
                new[] { execution }, new[] { commandCorrelation }, new[] { captureSession }),
            PolicyHash, correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Bundle(operationId,
            Scope(epoch, WindowStart, WindowStart.AddHours(4),
                new[] { new ExecutionCorrelationId(ExecutionKind.Manual, execution.Value) },
                new[] { commandCorrelation }, new[] { captureSession }),
            PolicyHash, correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget,
            Bundle(operationId, scope, OtherPolicyHash, correlationId: correlationId).AuthorizationTarget);
        Assert.NotEqual(baseline.AuthorizationTarget, Bundle(operationId, scope, PolicyHash,
            DiagnosticSupportReason.FaultInvestigation, correlationId).AuthorizationTarget);
    }

    [Fact, Trait("VerificationId", "V158_C13")]
    public void V158_C13_AllDiagnosticSupportCommandsRejectEmptyIdentityInvalidReasonAndNullContext()
    {
        var correlationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var profile = Profile();
        var scope = Scope(Guid.NewGuid(), WindowStart, WindowStart.AddHours(1));

        Assert.Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(Guid.Empty, Invocation(),
            sessionId, profile, DiagnosticSupportReason.MaintenanceInvestigation));
        Assert.Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, Invocation(),
            Guid.Empty, profile, DiagnosticSupportReason.MaintenanceInvestigation));
        Assert.Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, Invocation(),
            sessionId, profile, (DiagnosticSupportReason)0));
        Assert.Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, Invocation(),
            sessionId, profile, (DiagnosticSupportReason)99));
        Assert.Throws<ArgumentNullException>(() => new StartDiagnosticCaptureCommand(correlationId, Invocation(),
            sessionId, null!, DiagnosticSupportReason.MaintenanceInvestigation));
        Assert.Throws<ArgumentNullException>(() => new StartDiagnosticCaptureCommand(correlationId, null!,
            sessionId, profile, DiagnosticSupportReason.MaintenanceInvestigation));

        Assert.Throws<ArgumentException>(() => new StopDiagnosticCaptureCommand(Guid.Empty, Invocation(),
            sessionId, DiagnosticSupportReason.FaultInvestigation));
        Assert.Throws<ArgumentException>(() => new StopDiagnosticCaptureCommand(correlationId, Invocation(),
            Guid.Empty, DiagnosticSupportReason.FaultInvestigation));
        Assert.Throws<ArgumentException>(() => new StopDiagnosticCaptureCommand(correlationId, Invocation(),
            sessionId, (DiagnosticSupportReason)200));
        Assert.Throws<ArgumentNullException>(() => new StopDiagnosticCaptureCommand(correlationId, null!,
            sessionId, DiagnosticSupportReason.FaultInvestigation));

        Assert.Throws<ArgumentException>(() => new CreateSupportBundleCommand(Guid.Empty, Invocation(),
            operationId, scope, PolicyHash, DiagnosticSupportReason.AcceptanceSupport));
        Assert.Throws<ArgumentException>(() => new CreateSupportBundleCommand(correlationId, Invocation(),
            Guid.Empty, scope, PolicyHash, DiagnosticSupportReason.AcceptanceSupport));
        Assert.Throws<ArgumentException>(() => new CreateSupportBundleCommand(correlationId, Invocation(),
            operationId, scope, PolicyHash, (DiagnosticSupportReason)255));
        Assert.Throws<ArgumentNullException>(() => new CreateSupportBundleCommand(correlationId, Invocation(),
            operationId, null!, PolicyHash, DiagnosticSupportReason.AcceptanceSupport));
        Assert.Throws<ArgumentNullException>(() => new CreateSupportBundleCommand(correlationId, Invocation(),
            operationId, scope, null!, DiagnosticSupportReason.AcceptanceSupport));
        Assert.Throws<ArgumentException>(() => new CreateSupportBundleCommand(correlationId, Invocation(),
            operationId, scope, new string('z', 64), DiagnosticSupportReason.AcceptanceSupport));
        Assert.Throws<ArgumentNullException>(() => new CreateSupportBundleCommand(correlationId, null!,
            operationId, scope, PolicyHash, DiagnosticSupportReason.AcceptanceSupport));
    }

    private static DiagnosticCaptureProfile Profile(IEnumerable<string>? components = null,
        DiagnosticLevel level = DiagnosticLevel.Debug, TimeSpan? duration = null, int maximumEvents = 1000,
        string? policyHash = null) =>
        new(policyHash ?? PolicyHash, components ?? new[] { "Camera", "Runtime" }, level,
            duration ?? TimeSpan.FromMinutes(5), maximumEvents);

    private static SupportBundleScope Scope(Guid runtimeEpoch, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IEnumerable<ExecutionCorrelationId>? executions = null, IEnumerable<Guid>? commandCorrelations = null,
        IEnumerable<Guid>? captureSessions = null) =>
        new(runtimeEpoch, fromUtc, throughUtc, executions ?? Array.Empty<ExecutionCorrelationId>(),
            commandCorrelations ?? Array.Empty<Guid>(), captureSessions ?? Array.Empty<Guid>());

    private static CommandInvocation Invocation() =>
        new(CommandSource.PhysicalConsole, "operator-1", Guid.NewGuid(), Guid.NewGuid());

    private static StartDiagnosticCaptureCommand Start(Guid sessionId, DiagnosticCaptureProfile profile,
        DiagnosticSupportReason reason = DiagnosticSupportReason.MaintenanceInvestigation,
        Guid? correlationId = null) =>
        new(correlationId ?? Guid.NewGuid(), Invocation(), sessionId, profile, reason);

    private static CreateSupportBundleCommand Bundle(Guid operationId, SupportBundleScope scope,
        string policyHash, DiagnosticSupportReason reason = DiagnosticSupportReason.AcceptanceSupport,
        Guid? correlationId = null) =>
        new(correlationId ?? Guid.NewGuid(), Invocation(), operationId, scope, policyHash, reason);
}
