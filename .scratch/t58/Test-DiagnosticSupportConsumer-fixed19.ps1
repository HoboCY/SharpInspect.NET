param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed,
    [string]$DependencyFeed = 'https://api.nuget.org/v3/index.json'
)
# Isolated package-based public contract consumer for the T58 diagnostic capture and support
# bundle family. It references only the packed Runtime/Abstractions/Wpf packages, exercises
# canonical immutable targets, the distinct permissions, option/binding fail-closed gates, the
# absent-configuration runtime boundary and the optional WPF surface by reflection. Real
# runtime capture/export acceptance and physical station qualification stay NotRun.
$ErrorActionPreference = 'Stop'
$consumerRun = [IO.Path]::GetFullPath($Run)
$consumerRoot = Join-Path $consumerRun 'diagnostic-support-consumer'
$consumerFeed = [IO.Path]::GetFullPath($PackageFeed)
if (Test-Path -LiteralPath $consumerRoot) { throw 'Use a fresh diagnostic support consumer directory.' }
if (-not (Test-Path -LiteralPath $consumerFeed -PathType Container)) { throw 'The current package feed is missing.' }
[void][IO.Directory]::CreateDirectory($consumerRoot)

$consumerProjectXml = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <AssemblyName>DiagnosticSupport.Consumer</AssemblyName><RootNamespace>SharpInspect.Consumers</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpInspect.NET.Abstractions" Version="0.1.0-dev.1" />
    <PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" />
    <PackageReference Include="SharpInspect.NET.Wpf" Version="0.1.0-dev.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="6.0.1" />
  </ItemGroup>
</Project>
'@

$consumerProgram = @'
// Isolated package consumer for the T58 diagnostic-support public contracts. It references only
// the packed Runtime/Abstractions/Wpf packages and exercises contract construction, canonical
// immutable targets, the distinct permissions, option/binding fail-closed gates, the
// absent-configuration runtime boundary and the optional WPF presentation surface by reflection.
// No project reference, no private fixture import and no production qualification is used; real
// capture/export runtime acceptance and physical station qualification stay NotRun.
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.Consumers;

internal static class Program
{
    private const string PolicyHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly Dictionary<string, bool> Checks = new(StringComparer.Ordinal);
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main(string[] args)
    {
        string? work = null;
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == "--work") work = args[index + 1];
        if (work is null || !Path.IsPathFullyQualified(work))
        {
            Console.Error.WriteLine("WorkDirectoryRequired");
            return 2;
        }
        Directory.CreateDirectory(work);

        Check("profile.canonicalComponents", ProfileCanonical);
        Check("profile.immutableCollections", ProfileImmutable);
        Check("profile.bounds", ProfileBounds);
        Check("profile.enumerationBound", ProfileEnumerationBound);
        Check("scope.canonicalOrder", ScopeCanonical);
        Check("scope.bounds", ScopeBounds);
        Check("commands.targets", CommandsTargets);
        Check("commands.invalidRejected", CommandsInvalidRejected);
        Check("permissions.distinct", PermissionsDistinct);
        Check("storeOptions.binding", StoreOptionsBinding);
        Check("storeOptions.failClosedProfile", StoreOptionsFailClosedProfile);
        Check("supportRoot.installation", () => SupportRootInstallation(work));
        Check("loggingSupport.binding", () => LoggingSupportBinding(work));
        Check("runtime.queryProjection", RuntimeQueryProjection);
        Check("runtime.absentConfigRefusal", RuntimeAbsentConfigRefusal);
        Check("runtime.bundleReaderUnavailable", RuntimeBundleReaderUnavailable);
        Check("wpf.publicSurface", WpfPublicSurface);
        Check("wpf.closedChoices", WpfClosedChoices);
        Check("wpf.noExportSurface", WpfNoExportSurface);

        var passed = Failures.Count == 0 && Checks.Values.All(value => value);
        var detail = new
        {
            result = passed ? "Pass" : "Fail",
            verificationIds = new[] { "V158_K01", "V158_K02", "V158_K03" },
            checks = Checks,
            failures = Failures,
            productionQualificationAuthority = false,
            captureExportRuntime = "NotRun",
            physicalStationAcceptance = "NotRun",
            wpfRuntimeExercise = "NotRun"
        };
        Console.WriteLine(JsonSerializer.Serialize(detail));
        return passed ? 0 : 3;
    }

    private static void Check(string name, Func<bool> verify)
    {
        Checks[name] = false;
        try { Checks[name] = verify(); }
        catch (Exception exception) { Failures.Add(name + ":" + exception.GetType().Name); }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    private static bool ThrowsWith<T>(Action action, string fragment) where T : Exception
    {
        try { action(); return false; }
        catch (T exception) when (exception.Message.Contains(fragment, StringComparison.Ordinal)) { return true; }
        catch { return false; }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

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

    private static DiagnosticSupportPolicy SupportPolicyValue() => new("V158.Consumer", "1", "V158.consumer-approval",
        PolicyHash, OtherHash, TimeSpan.FromDays(1), 100, 4096, 64 * 1024, TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromDays(30), 64, 16 * 1024, 32L * 1024 * 1024);

    private static bool ProfileCanonical()
    {
        var components = new List<string> { "Runtime", "Camera.Front", "Plc" };
        var profile = Profile(components);
        var reordered = Profile(new[] { "Plc", "Runtime", "Camera.Front" });
        var lowered = Profile(components, policyHash: PolicyHash.ToLowerInvariant());
        return profile.Components.SequenceEqual(new[] { "Camera.Front", "Plc", "Runtime" }) &&
            profile.ContentHash == reordered.ContentHash && profile.ContentHash == lowered.ContentHash &&
            profile.LoggingPolicyHash == PolicyHash && IsHash(profile.ContentHash);
    }

    private static bool ProfileImmutable()
    {
        var components = new List<string> { "Runtime", "Camera" };
        var profile = Profile(components);
        var hash = profile.ContentHash;
        components.Clear();
        var readOnly = profile.Components is IList<string> list && Throws<NotSupportedException>(() => list.Add("Intruder"));
        return profile.Components.Count == 2 && hash == profile.ContentHash && readOnly &&
            !profile.Components.Contains("Intruder", StringComparer.Ordinal);
    }

    private static bool ProfileBounds()
    {
        var exact = Profile(components: Enumerable.Range(0, 32).Select(index => "component-" + index),
            level: DiagnosticLevel.Trace, duration: TimeSpan.FromHours(1), maximumEvents: 1_000_000);
        return exact.Components.Count == 32 && IsHash(exact.ContentHash) &&
            Throws<ArgumentException>(() => Profile(components: Array.Empty<string>())) &&
            Throws<ArgumentException>(() => Profile(components: new[] { "Camera", "Camera" })) &&
            Throws<ArgumentException>(() => Profile(components: new[] { "bad component" })) &&
            Throws<ArgumentException>(() => Profile(components: new[] { new string('a', 129) })) &&
            Throws<ArgumentException>(() => Profile(components: new[] { "Camera", null! })) &&
            Throws<ArgumentException>(() => Profile(components: Enumerable.Range(0, 33).Select(index => "component-" + index))) &&
            Throws<ArgumentNullException>(() => new DiagnosticCaptureProfile(
                null!, new[] { "Camera" }, DiagnosticLevel.Debug, TimeSpan.FromMinutes(1), 10)) &&
            Throws<ArgumentException>(() => Profile(policyHash: new string('A', 63))) &&
            Throws<ArgumentException>(() => Profile(policyHash: new string('G', 64))) &&
            Throws<ArgumentException>(() => Profile(level: DiagnosticLevel.Information)) &&
            Throws<ArgumentException>(() => Profile(level: (DiagnosticLevel)99)) &&
            Throws<ArgumentException>(() => Profile(duration: TimeSpan.Zero)) &&
            Throws<ArgumentException>(() => Profile(duration: TimeSpan.FromHours(1) + TimeSpan.FromTicks(1))) &&
            Throws<ArgumentException>(() => Profile(maximumEvents: 0)) &&
            Throws<ArgumentException>(() => Profile(maximumEvents: 1_000_001));
    }

    private static bool ProfileEnumerationBound()
    {
        var pulled = 0;
        var disposed = false;
        IEnumerable<string> Hostile()
        {
            try { while (true) { pulled++; yield return "component-" + pulled; } }
            finally { disposed = true; }
        }

        var rejected = Throws<ArgumentException>(() => Profile(components: Hostile()));
        return rejected && pulled == 33 && disposed;
    }

    private static bool ScopeCanonical()
    {
        var epoch = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z", CultureInfo.InvariantCulture);
        var executions = new[]
        {
            new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.Parse("00000000-0000-0000-0000-000000000001"))
        };
        var commands = new[]
        {
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        var captures = new[] { Guid.Parse("44444444-4444-4444-4444-444444444444") };
        var first = Scope(epoch, start, start.AddHours(6), executions, commands, captures);
        var permuted = Scope(epoch, start, start.AddHours(6), executions.Reverse(), commands.Reverse(), captures.Reverse());
        var changed = Scope(epoch, start, start.AddHours(7), executions, commands, captures);
        var expected = new[]
        {
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        return IsHash(first.ContentHash) && first.ContentHash == permuted.ContentHash &&
            first.ContentHash != changed.ContentHash && first.Executions.SequenceEqual(expected) &&
            first.CommandCorrelations.SequenceEqual(commands.OrderBy(value => value)) &&
            first.CaptureSessions.SequenceEqual(captures.OrderBy(value => value));
    }

    private static bool ScopeBounds()
    {
        var epoch = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z", CultureInfo.InvariantCulture);
        var offsetStart = DateTimeOffset.Parse("2026-09-11T02:00:00+02:00", CultureInfo.InvariantCulture);
        var offsetThrough = DateTimeOffset.Parse("2026-09-12T02:00:00+02:00", CultureInfo.InvariantCulture);
        var execution = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var maximum = Scope(epoch, start, start.AddHours(1),
            Enumerable.Range(0, 64).Select(_ => new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid())), null, null);
        var pulled = 0;
        var disposed = false;
        IEnumerable<Guid> Hostile()
        {
            try { while (true) { pulled++; yield return Guid.NewGuid(); } }
            finally { disposed = true; }
        }

        return maximum.Executions.Count == 64 &&
            Throws<ArgumentException>(() => Scope(epoch, offsetStart, start.AddDays(1))) &&
            Throws<ArgumentException>(() => Scope(epoch, start, offsetThrough)) &&
            Throws<ArgumentException>(() => Scope(Guid.Empty, start, start.AddHours(1))) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start)) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddTicks(-1))) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddDays(31) + TimeSpan.FromTicks(1))) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1), new[] { execution, execution }, null, null)) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1),
                new[] { new ExecutionCorrelationId((ExecutionKind)99, Guid.NewGuid()) }, null, null)) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1),
                new[] { new ExecutionCorrelationId(ExecutionKind.Production, Guid.Empty) }, null, null)) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1), null, new[] { Guid.Empty }, null)) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1), null, null, new[] { Guid.Empty })) &&
            Throws<ArgumentException>(() => Scope(epoch, start, start.AddHours(1), null, Hostile(), null)) &&
            pulled == 65 && disposed && Scope(epoch, start, start.AddDays(31)).ThroughUtc == start.AddDays(31);
    }

    private static bool CommandsTargets()
    {
        var sessionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, "consumer-1", Guid.NewGuid(), Guid.NewGuid());
        var start = new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId, Profile(),
            DiagnosticSupportReason.MaintenanceInvestigation);
        var equivalent = new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId, Profile(),
            DiagnosticSupportReason.MaintenanceInvestigation);
        var stop = new StopDiagnosticCaptureCommand(correlationId, invocation, sessionId,
            DiagnosticSupportReason.MaintenanceInvestigation);
        var startUtc = DateTimeOffset.Parse("2026-09-11T00:00:00Z", CultureInfo.InvariantCulture);
        var scope = Scope(Guid.NewGuid(), startUtc, startUtc.AddHours(4),
            new[] { new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid()) },
            new[] { Guid.NewGuid() }, new[] { Guid.NewGuid() });
        var bundle = new CreateSupportBundleCommand(correlationId, invocation, operationId, scope, PolicyHash,
            DiagnosticSupportReason.AcceptanceSupport);
        var lowered = new CreateSupportBundleCommand(correlationId, invocation, operationId, scope, PolicyHash.ToLowerInvariant(),
            DiagnosticSupportReason.AcceptanceSupport);
        var otherProfile = new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId,
            Profile(level: DiagnosticLevel.Trace), DiagnosticSupportReason.MaintenanceInvestigation);
        var otherSession = new StartDiagnosticCaptureCommand(correlationId, invocation, Guid.NewGuid(), Profile(),
            DiagnosticSupportReason.MaintenanceInvestigation);
        var otherReason = new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId, Profile(),
            DiagnosticSupportReason.FaultInvestigation);
        var otherScope = new CreateSupportBundleCommand(correlationId, invocation, operationId,
            Scope(scope.RuntimeEpoch, startUtc, startUtc.AddHours(5), scope.Executions, scope.CommandCorrelations, scope.CaptureSessions),
            PolicyHash, DiagnosticSupportReason.AcceptanceSupport);
        var otherPolicy = new CreateSupportBundleCommand(correlationId, invocation, operationId, scope, OtherHash,
            DiagnosticSupportReason.AcceptanceSupport);
        return IsHash(start.AuthorizationTarget) && start.AuthorizationTarget == equivalent.AuthorizationTarget &&
            start.AuthorizationTarget != stop.AuthorizationTarget &&
            start.AuthorizationTarget != bundle.AuthorizationTarget && bundle.AuthorizationTarget == lowered.AuthorizationTarget &&
            start.AuthorizationTarget != otherProfile.AuthorizationTarget &&
            start.AuthorizationTarget != otherSession.AuthorizationTarget &&
            start.AuthorizationTarget != otherReason.AuthorizationTarget &&
            bundle.AuthorizationTarget != otherScope.AuthorizationTarget &&
            bundle.AuthorizationTarget != otherPolicy.AuthorizationTarget && IsHash(stop.AuthorizationTarget) &&
            IsHash(bundle.AuthorizationTarget);
    }

    private static bool CommandsInvalidRejected()
    {
        var correlationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, "consumer-1", Guid.NewGuid(), Guid.NewGuid());
        var profile = Profile();
        var startUtc = DateTimeOffset.Parse("2026-09-11T00:00:00Z", CultureInfo.InvariantCulture);
        var scope = Scope(Guid.NewGuid(), startUtc, startUtc.AddHours(1));
        return Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(Guid.Empty, invocation, sessionId,
                profile, DiagnosticSupportReason.MaintenanceInvestigation)) &&
            Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, invocation, Guid.Empty,
                profile, DiagnosticSupportReason.MaintenanceInvestigation)) &&
            Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId,
                profile, (DiagnosticSupportReason)0)) &&
            Throws<ArgumentException>(() => new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId,
                profile, (DiagnosticSupportReason)99)) &&
            Throws<ArgumentNullException>(() => new StartDiagnosticCaptureCommand(correlationId, invocation, sessionId,
                null!, DiagnosticSupportReason.MaintenanceInvestigation)) &&
            Throws<ArgumentNullException>(() => new StartDiagnosticCaptureCommand(correlationId, null!, sessionId,
                profile, DiagnosticSupportReason.MaintenanceInvestigation)) &&
            Throws<ArgumentException>(() => new StopDiagnosticCaptureCommand(Guid.Empty, invocation, sessionId,
                DiagnosticSupportReason.FaultInvestigation)) &&
            Throws<ArgumentException>(() => new StopDiagnosticCaptureCommand(correlationId, invocation, Guid.Empty,
                DiagnosticSupportReason.FaultInvestigation)) &&
            Throws<ArgumentException>(() => new CreateSupportBundleCommand(Guid.Empty, invocation, operationId,
                scope, PolicyHash, DiagnosticSupportReason.AcceptanceSupport)) &&
            Throws<ArgumentException>(() => new CreateSupportBundleCommand(correlationId, invocation, Guid.Empty,
                scope, PolicyHash, DiagnosticSupportReason.AcceptanceSupport)) &&
            Throws<ArgumentNullException>(() => new CreateSupportBundleCommand(correlationId, invocation, operationId,
                null!, PolicyHash, DiagnosticSupportReason.AcceptanceSupport)) &&
            Throws<ArgumentException>(() => new CreateSupportBundleCommand(correlationId, invocation, operationId,
                scope, new string('z', 64), DiagnosticSupportReason.AcceptanceSupport));
    }

    private static bool PermissionsDistinct()
    {
        var start = Permission.StartDiagnosticCapture;
        var export = Permission.ExportSupportBundle;
        return (ushort)start == 42 && (ushort)export == 43 && start != export &&
            start != Permission.RunDiagnostics && export != Permission.ExportProtectedDiagnostics &&
            (int)AuditedCommandKind.StartDiagnosticCapture == 60 && (int)AuditedCommandKind.StopDiagnosticCapture == 61 &&
            AuditedCommandKind.StartDiagnosticCapture != AuditedCommandKind.StopDiagnosticCapture &&
            Enum.GetValues<SystemPermission>().All(value => !value.ToString().Contains("Diagnostic", StringComparison.Ordinal));
    }

    private static bool StoreOptionsBinding()
    {
        var option = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash);
        var lowered = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash.ToLowerInvariant());
        var rebound = new DiagnosticSupportStoreOptions(SupportPolicyValue(), new string('C', 64));
        return IsHash(option.BindingHash) && option.BindingHash == lowered.BindingHash &&
            option.BindingHash != rebound.BindingHash && option.SupportRootBindingHash == OtherHash &&
            Throws<ArgumentException>(() => new DiagnosticSupportStoreOptions(SupportPolicyValue(), "not-a-hash")) &&
            Throws<ArgumentException>(() => new DiagnosticSupportStoreOptions(SupportPolicyValue(), new string('G', 64))) &&
            Throws<ArgumentNullException>(() => new DiagnosticSupportStoreOptions(null!, OtherHash)) &&
            Throws<ArgumentOutOfRangeException>(() =>
                _ = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash) { MaximumOperations = 1 }.BindingHash) &&
            Throws<ArgumentOutOfRangeException>(() =>
                _ = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash) { MaximumOperations = 100_001 }.BindingHash) &&
            Throws<ArgumentOutOfRangeException>(() =>
                _ = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash) { MaximumTotalBytes = 4096 }.BindingHash) &&
            Throws<ArgumentOutOfRangeException>(() =>
                _ = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash) { MaximumTotalBytes = 512L * 1024 * 1024 + 1 }.BindingHash);
    }

    private static bool StoreOptionsFailClosedProfile()
    {
        var option = new DiagnosticSupportStoreOptions(SupportPolicyValue(), OtherHash);
        var audit = new AuditIntegrityPolicy("V158.Consumer", "1", "V158.consumer");
        var identity = new LocalIdentityOptions("V158.Consumer", new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("V158.Consumer.Blocklist", "1", new[] { "known-compromised-value" })
        }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, new AuthorizationPolicy("V158.Consumer", "1",
            new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
            {
                [HumanRoleBundle.Operator] = new[] { 5, 6, 29 }.Select(value => (Permission)value),
                [HumanRoleBundle.Technician] = new[] { 5, 6, 7, 11, 13, 23, 29, 30, 31 }.Select(value => (Permission)value),
                [HumanRoleBundle.Administrator] = Enumerable.Range(1, 31).Select(value => (Permission)value)
            }, Array.Empty<Permission>()));
        var trace = new TraceStoragePolicyStoreOptions();
        return ThrowsWith<ArgumentException>(() => ProbeConfiguration(new ProductionStoreOptions
                { DiagnosticSupport = option }), "DiagnosticSupportRequiresAudit") &&
            ThrowsWith<ArgumentException>(() => ProbeConfiguration(new ProductionStoreOptions
                { AuditIntegrityPolicy = audit, DiagnosticSupport = option }), "DiagnosticSupportRequiresIdentity") &&
            ThrowsWith<ArgumentException>(() => ProbeConfiguration(new ProductionStoreOptions
                { AuditIntegrityPolicy = audit, LocalIdentity = identity, DiagnosticSupport = option }),
                "DiagnosticSupportRequiresTraceStoragePolicies") &&
            ThrowsWith<ArgumentException>(() => ProbeConfiguration(new ProductionStoreOptions
                { AuditIntegrityPolicy = audit, LocalIdentity = identity, TraceStoragePolicies = trace, DiagnosticSupport = option }),
                "DiagnosticSupportRequiresStorageRetention");
    }

    private static void ProbeConfiguration(ProductionStoreOptions options)
    {
        // Exercise the public host composition surface. The SQLite writer remains
        // internal and is never granted to an independent package consumer.
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(options);
        var provider = services.BuildServiceProvider();
        try { _ = provider.GetRequiredService<IStationRuntime>(); }
        finally { provider.DisposeAsync().GetAwaiter().GetResult(); }
    }
    private static bool SupportRootInstallation(string work)
    {
        var supportPolicy = SupportPolicyValue();
        var files = new DiagnosticLocalStoreOptions(Path.Combine(work, "support-install"), true,
            supportPolicy.MaximumBundleBytes, supportPolicy.MaximumBundleBytes, 4, 4L * supportPolicy.MaximumBundleBytes,
            supportPolicy.ExportTimeout, supportPolicy.Retention);
        var binding = DiagnosticDirectoryInstallation.Install(files);
        return IsHash(binding) && DiagnosticDirectoryInstallation.Verify(files, binding) &&
            !DiagnosticDirectoryInstallation.Verify(files, new string('F', 64)) &&
            binding == DiagnosticDirectoryInstallation.Install(files);
    }

    private static bool LoggingSupportBinding(string work)
    {
        var (policy, safe, safeBinding, protectedStore, protectedBinding) = BuildLogging(Path.Combine(work, "logging"));
        var supportPolicy = SupportPolicyValue();
        var files = new DiagnosticLocalStoreOptions(Path.Combine(work, "logging-support"), true,
            supportPolicy.MaximumBundleBytes, supportPolicy.MaximumBundleBytes, 4, 4L * supportPolicy.MaximumBundleBytes,
            supportPolicy.ExportTimeout, supportPolicy.Retention);
        var support = new SupportBundleOptions(supportPolicy, files, DiagnosticDirectoryInstallation.Install(files));
        var baseLogging = new LoggingDiagnosticsOptions(policy, safe, safeBinding, protectedStore, protectedBinding);
        var logging = new LoggingDiagnosticsOptions(policy, safe, safeBinding, protectedStore, protectedBinding)
            { SupportBundles = support };
        var mismatchedFiles = new DiagnosticLocalStoreOptions(Path.Combine(work, "logging-support-mismatch"), true,
            files.MaximumRecordBytes - 1, files.MaximumFileBytes, files.MaximumFiles, files.MaximumTotalBytes,
            files.RollAfter, files.Retention);
        return IsHash(support.BindingHash) && IsHash(logging.BindingHash) && IsHash(support.InstallationBinding) &&
            logging.BindingHash != baseLogging.BindingHash &&
            new SupportBundleOptions(supportPolicy, files, new string('E', 64)).BindingHash != support.BindingHash &&
            new DiagnosticSupportStoreOptions(supportPolicy, support.BindingHash).BindingHash !=
                new DiagnosticSupportStoreOptions(supportPolicy, new string('E', 64)).BindingHash &&
            ThrowsWith<ArgumentException>(() =>
                _ = new SupportBundleOptions(supportPolicy, mismatchedFiles, support.InstallationBinding),
                "SupportBundleFilePolicyMismatch") &&
            Throws<ArgumentException>(() => _ = new SupportBundleOptions(supportPolicy, files, "not-a-binding"));
    }

    private static (LoggingDiagnosticsPolicy Policy, DiagnosticLocalStoreOptions Safe, string SafeBinding,
        DiagnosticLocalStoreOptions Protected, string ProtectedBinding) BuildLogging(string directory)
    {
        // Only the named leaf roots may be created by Install; their parent must already exist.
        Directory.CreateDirectory(directory);
        var sample = new DiagnosticEventContract("V158.Consumer.Measurement", 1, "Runtime",
            DiagnosticLevel.Information, false, new[]
            {
                new DiagnosticFieldContract("State", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                    null, null, new[] { "Idle", "Busy" }),
                new DiagnosticFieldContract("Count", DiagnosticScalarKind.Int64, DiagnosticDataClass.Safe, false, 0, 100, null),
                new DiagnosticFieldContract("VendorState", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Protected,
                    false, null, null, new[] { "Idle", "Busy" }),
                new DiagnosticFieldContract("Forbidden", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Prohibited,
                    false, null, null, new[] { "Bait" })
            });
        var policy = new LoggingDiagnosticsPolicy("V158.Consumer.Logging", "1", "V158-consumer", "1", OtherHash,
            DiagnosticLevel.Information, DiagnosticStandardContracts.All.Concat(new[] { sample }),
            new DiagnosticProducerBudget(1000, 1_000_000, 32, 128, 1000, TimeSpan.FromSeconds(10), 4),
            new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
            new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
            new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
            new DiagnosticFileBudget(4096, 65536, 4, 262144, TimeSpan.FromSeconds(1), TimeSpan.FromDays(7)),
            new DiagnosticFileBudget(4096, 65536, 4, 262144, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
            10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), 100, 65536, TimeSpan.FromMinutes(5), 1000);
        var safe = new DiagnosticLocalStoreOptions(Path.Combine(directory, "safe"), false,
            policy.SafeFiles.MaximumRecordBytes, policy.SafeFiles.MaximumFileBytes, policy.SafeFiles.MaximumFiles,
            policy.SafeFiles.MaximumTotalBytes, policy.SafeFiles.RollAfter, policy.SafeFiles.Retention);
        var protectedStore = new DiagnosticLocalStoreOptions(Path.Combine(directory, "protected"), true,
            policy.ProtectedFiles.MaximumRecordBytes, policy.ProtectedFiles.MaximumFileBytes, policy.ProtectedFiles.MaximumFiles,
            policy.ProtectedFiles.MaximumTotalBytes, policy.ProtectedFiles.RollAfter, policy.ProtectedFiles.Retention);
        return (policy, safe, DiagnosticDirectoryInstallation.Install(safe), protectedStore,
            DiagnosticDirectoryInstallation.Install(protectedStore));
    }

    private static bool RuntimeQueryProjection()
    {
        var provider = new ServiceCollection().AddSharpInspectRuntime().BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IStationRuntime>();
            var query = provider.GetRequiredService<IDiagnosticSupportQuery>();
            var capture = query.ReadCapture();
            var bundle = query.ReadBundle();
            return !capture.Configured && capture.Phase == DiagnosticCapturePhase.Baseline && !capture.Elevated &&
                capture.ReasonCode == "DiagnosticBaseline" && capture.RuntimeEpoch != Guid.Empty &&
                !bundle.Configured && bundle.Phase == SupportBundlePhase.Idle &&
                bundle.ReasonCode == "SupportBundleIdle";
        }
        finally { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static bool RuntimeAbsentConfigRefusal()
    {
        var provider = new ServiceCollection().AddSharpInspectRuntime().BuildServiceProvider();
        try
        {
            var runtime = provider.GetRequiredService<IStationRuntime>();
            var command = new StartDiagnosticCaptureCommand(Guid.NewGuid(),
                new CommandInvocation(CommandSource.PhysicalConsole), Guid.NewGuid(), Profile(),
                DiagnosticSupportReason.MaintenanceInvestigation);
            var outcome = runtime.SubmitAsync(command).AsTask().GetAwaiter().GetResult();
            return outcome.Disposition == CommandDisposition.Rejected &&
                outcome.ReasonCode == "DiagnosticSupportConfigurationRequired" &&
                outcome.Audit == AuditPersistence.NotAttempted;
        }
        finally { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static bool RuntimeBundleReaderUnavailable()
    {
        var provider = new ServiceCollection().AddSharpInspectRuntime().BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IStationRuntime>();
            var read = provider.GetRequiredService<ISupportBundleReader>()
                .ReadAsync(new SupportBundleReadRequest(Guid.NewGuid(),
                    new CommandInvocation(CommandSource.PhysicalConsole)))
                .AsTask().GetAwaiter().GetResult();
            return !read.Available && read.Bytes.IsEmpty && read.ReasonCode == "SupportBundleReadUnavailable";
        }
        finally { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static bool WpfPublicSurface()
    {
        var viewModel = typeof(DiagnosticSupportViewModel);
        var panel = typeof(DiagnosticSupportPanel);
        var shell = typeof(ShellWindow);
        var constructor = viewModel.GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var attach = shell.GetMethod("AttachDiagnosticSupport");
        return viewModel.IsPublic && viewModel.IsSealed && panel.IsPublic && shell.IsPublic &&
            parameters.Length == 7 &&
            parameters[0].ParameterType == typeof(IStationRuntime) &&
            parameters[1].ParameterType == typeof(IDiagnosticSupportQuery) &&
            parameters[2].ParameterType == typeof(IStepUpAuthentication) &&
            parameters[3].ParameterType == typeof(IInteractiveSessionService) &&
            parameters[4].ParameterType == typeof(LoggingDiagnosticsPolicy) &&
            parameters[5].ParameterType == typeof(DiagnosticSupportPolicy) &&
            parameters[6].ParameterType == typeof(IUiDispatcher) && parameters[6].HasDefaultValue &&
            attach is { IsPublic: true } && attach.GetParameters().Single().ParameterType == viewModel;
    }

    private static bool WpfClosedChoices()
    {
        var property = typeof(DiagnosticSupportViewModel).GetProperty("ReasonOptions");
        var optionType = property?.PropertyType.GetGenericArguments().Single();
        var names = optionType is null ? Array.Empty<string>() :
            optionType.GetProperties().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return optionType == typeof(DiagnosticSupportReasonOption) &&
            names.SequenceEqual(new[] { "Label", "Value" }) &&
            Enum.GetValues<DiagnosticSupportReason>().Length == 3;
    }

    private static bool WpfNoExportSurface()
    {
        var forbidden = new[] { "path", "destination", "raw", "save", "copy", "export", "dump", "protecteddetail", "secret", "password" };
        var names = typeof(DiagnosticSupportViewModel).GetProperties().Select(value => value.Name)
            .Concat(typeof(DiagnosticSupportViewModel).GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Select(value => value.Name));
        return !names.Any(name => forbidden.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
}
'@

[IO.File]::WriteAllText((Join-Path $consumerRoot 'DiagnosticSupport.Consumer.csproj'), $consumerProjectXml,
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'), $consumerProgram, [Text.UTF8Encoding]::new($false))
$consumerConfig = Join-Path $consumerRoot 'NuGet.Config'
$consumerConfigXml = '<configuration><packageSources><clear/><add key="current-ticket" value="' +
    [Security.SecurityElement]::Escape($consumerFeed) + '"/><add key="dependencies" value="' +
    [Security.SecurityElement]::Escape($DependencyFeed) + '"/></packageSources><packageSourceMapping>' +
    '<packageSource key="current-ticket"><package pattern="SharpInspect.NET.*"/></packageSource>' +
    '<packageSource key="dependencies"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
[IO.File]::WriteAllText($consumerConfig, $consumerConfigXml, [Text.UTF8Encoding]::new($false))
& dotnet restore (Join-Path $consumerRoot 'DiagnosticSupport.Consumer.csproj') --configfile $consumerConfig `
    --packages (Join-Path $consumerRoot 'cache') *> (Join-Path $consumerRoot 'restore.log')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic support consumer restore failed.' }
& dotnet build (Join-Path $consumerRoot 'DiagnosticSupport.Consumer.csproj') -c Release --no-restore -m:1 *> `
    (Join-Path $consumerRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic support consumer build failed.' }
$consumerAssets = Get-Content -LiteralPath (Join-Path $consumerRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
if (@($consumerAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ceq 'project' }).Count -ne 0) {
    throw 'Diagnostic support consumer still references source projects.'
}
$consumerOutput = Join-Path $consumerRoot 'bin/Release/net6.0-windows'
$consumerExe = Join-Path $consumerOutput 'DiagnosticSupport.Consumer.exe'
if (-not (Test-Path -LiteralPath $consumerExe -PathType Leaf)) { throw 'Diagnostic support consumer executable missing.' }

# Package metadata: each declared package asset must be byte-identical to the loaded build output,
# each nuspec must keep its package identity and the Runtime/Wpf packages must keep their
# dependency identity on Abstractions.
$consumerPackages = @(
    @{ Name='Abstractions'; Asset='lib/net6.0/SharpInspect.Abstractions.dll'; Dependency=$null },
    @{ Name='Runtime'; Asset='lib/net6.0/SharpInspect.Runtime.dll'; Dependency='SharpInspect.NET.Abstractions' },
    @{ Name='Wpf'; Asset='lib/net6.0-windows7.0/SharpInspect.Wpf.dll'; Dependency='SharpInspect.NET.Abstractions' }
)
function Get-ConsumerPackageText([string]$Package, [string]$Entry) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item = $archive.GetEntry($Entry)
        if (-not $item) { throw "Package entry missing: $Entry" }
        $stream = $item.Open()
        try { $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() } }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}
$consumerHashes = [ordered]@{}
foreach ($consumerPackage in $consumerPackages) {
    $nupkg = Join-Path $consumerFeed ('SharpInspect.NET.' + $consumerPackage.Name + '.0.1.0-dev.1.nupkg')
    if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf)) { throw "Package missing: $($consumerPackage.Name)" }
    $archive = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $entry = $archive.GetEntry($consumerPackage.Asset)
        if (-not $entry) { throw "Packaged assembly missing: $($consumerPackage.Asset)" }
        $stream = $entry.Open()
        try { $packaged = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
    $built = Join-Path $consumerOutput ('SharpInspect.' + $consumerPackage.Name + '.dll')
    if ((Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash -cne $packaged) {
        throw "Diagnostic support consumer $($consumerPackage.Name) assembly does not match its declared package."
    }
    $nuspec = Get-ConsumerPackageText $nupkg ('SharpInspect.NET.' + $consumerPackage.Name + '.nuspec')
    if ($nuspec -notmatch ('<id>' + [Regex]::Escape('SharpInspect.NET.' + $consumerPackage.Name) + '</id>')) {
        throw "Package identity metadata missing: $($consumerPackage.Name)"
    }
    if ($consumerPackage.Dependency -and
        $nuspec -notmatch ('id="' + [Regex]::Escape($consumerPackage.Dependency) + '"')) {
        throw "Package dependency metadata missing: $($consumerPackage.Name) -> $($consumerPackage.Dependency)"
    }
    $consumerHashes[$consumerPackage.Name + 'Assembly'] = $packaged
    $consumerHashes[$consumerPackage.Name + 'Package'] = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
}
$consumerHashes.Consumer = (Get-FileHash -LiteralPath $consumerExe -Algorithm SHA256).Hash

if (-not (Test-Path -LiteralPath (Join-Path $consumerRoot 'fixture'))) {
    [void][IO.Directory]::CreateDirectory((Join-Path $consumerRoot 'fixture'))
}
$consumerStart = [Diagnostics.ProcessStartInfo]::new($consumerExe)
$consumerStart.UseShellExecute = $false
$consumerStart.CreateNoWindow = $true
$consumerStart.RedirectStandardOutput = $true
$consumerStart.RedirectStandardError = $true
$consumerStart.ArgumentList.Add('--work')
$consumerStart.ArgumentList.Add((Join-Path $consumerRoot 'fixture'))
$consumerProcess = [Diagnostics.Process]::Start($consumerStart)
try {
    $consumerStdout = $consumerProcess.StandardOutput.ReadToEndAsync()
    $consumerStderr = $consumerProcess.StandardError.ReadToEndAsync()
    if (-not $consumerProcess.WaitForExit(180000)) {
        $consumerProcess.Kill($true)
        throw 'Diagnostic support consumer deadline exceeded.'
    }
    $consumerText = $consumerStdout.GetAwaiter().GetResult()
    $consumerErrorText = $consumerStderr.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $consumerRoot 'consumer.stdout.log') -Value $consumerText -Encoding utf8
    Set-Content -LiteralPath (Join-Path $consumerRoot 'consumer.stderr.log') -Value $consumerErrorText -Encoding utf8
    if ($consumerProcess.ExitCode -ne 0) {
        throw ('Diagnostic support consumer exit ' + $consumerProcess.ExitCode + '; see consumer.stdout.log.')
    }
    if (-not [string]::IsNullOrWhiteSpace($consumerErrorText)) {
        throw 'Diagnostic support consumer wrote unexpected diagnostics.'
    }
}
finally { $consumerProcess.Dispose() }
$consumerDetail = $consumerText | ConvertFrom-Json
$consumerFailed = @($consumerDetail.checks.PSObject.Properties | Where-Object { $_.Value -ne $true } |
    ForEach-Object { $_.Name })
$consumerRequired = @(
    'profile.canonicalComponents', 'profile.immutableCollections', 'profile.bounds', 'profile.enumerationBound',
    'scope.canonicalOrder', 'scope.bounds', 'commands.targets', 'commands.invalidRejected', 'permissions.distinct',
    'storeOptions.binding', 'storeOptions.failClosedProfile', 'supportRoot.installation', 'loggingSupport.binding',
    'runtime.queryProjection', 'runtime.absentConfigRefusal', 'runtime.bundleReaderUnavailable',
    'wpf.publicSurface', 'wpf.closedChoices', 'wpf.noExportSurface'
)
if ($consumerDetail.result -cne 'Pass' -or $consumerFailed.Count -ne 0 -or @($consumerDetail.failures).Count -ne 0) {
    throw ('Diagnostic support consumer failed: ' + ($consumerFailed -join ','))
}
foreach ($consumerCheck in $consumerRequired) {
    if ($consumerDetail.checks.PSObject.Properties[$consumerCheck].Value -ne $true) {
        throw "Diagnostic support consumer check missing or failed: $consumerCheck"
    }
}
foreach ($consumerVerification in @('V158_K01', 'V158_K02', 'V158_K03')) {
    if (@($consumerDetail.verificationIds) -notcontains $consumerVerification) {
        throw "Diagnostic support consumer verification id missing: $consumerVerification"
    }
}
if ($consumerDetail.productionQualificationAuthority -cne $false -or
    $consumerDetail.captureExportRuntime -cne 'NotRun' -or
    $consumerDetail.physicalStationAcceptance -cne 'NotRun' -or
    $consumerDetail.wpfRuntimeExercise -cne 'NotRun') {
    throw 'Diagnostic support consumer overstated its qualification scope.'
}
[ordered]@{
    verificationIds=@('V158_K01', 'V158_K02', 'V158_K03'); result='Pass'
    scope='isolated NuGet development consumer; canonical immutable capture/scope/command targets; distinct permissions; option and installation binding fail-closed gates; runtime absent-config refusal; packaged Runtime/Abstractions/Wpf metadata and WPF public surface by reflection; real capture/export runtime acceptance NotRun'
    consumerExe=$consumerExe; packages=$consumerHashes
    productionQualificationAuthority=$false; captureExportRuntime='NotRun'
    physicalStationAcceptance='NotRun'; wpfRuntimeExercise='NotRun'
    consumer=$consumerDetail; completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $consumerRun 'diagnostic-support-consumer.json') -Encoding utf8
$consumerDetail | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $consumerRoot 'validation.json') -Encoding utf8
Write-Output ('V158_K01 packaged diagnostic-support contract consumer PASS checks=' + $consumerRequired.Count +
    ' captureExportRuntime=NotRun wpfRuntimeExercise=NotRun')
