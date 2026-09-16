using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SupportBundleStoreTests
{
    [Fact, Trait("VerificationId", "V158_F01")]
    public void V158_F01_PrivateStageHasExactBytesIdentityAndFixedExpiryAndConsumesCapacity()
    {
        using var fixture = new Fixture(); using var store = fixture.Store();
        var id = Guid.NewGuid(); string hash; DateTimeOffset expires;
        using (var stage = fixture.Stage(store, id))
        {
            Assert.Equal(id, stage.Id); Assert.Equal(fixture.Now, stage.Created);
            Assert.Equal(fixture.Now + fixture.Policy.ExportTimeout + fixture.Policy.Retention, stage.Expires);
            hash = stage.Projection.ContentHash; expires = stage.Expires;
        }
        var path = Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.True(DiagnosticDirectoryInstallation.ValidateRestrictedAcl(path, false));
        Assert.Contains(expires.UtcTicks.ToString("D19", CultureInfo.InvariantCulture), Path.GetFileName(path));
        Assert.Throws<InvalidOperationException>(() => fixture.Stage(store, Guid.NewGuid()));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
    }

    [Fact, Trait("VerificationId", "V158_F02")]
    public void V158_F02_ExpiredFileWithoutOriginalAuditSealRemainsChargedAfterRestart()
    {
        using var fixture = new Fixture();
        using (var store = fixture.Store()) using (fixture.Stage(store, Guid.NewGuid())) { }
        fixture.Now += TimeSpan.FromDays(1);
        using var restarted = fixture.Store();
        Assert.Throws<InvalidOperationException>(() => fixture.Stage(restarted, Guid.NewGuid()));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
    }

    [Fact, Trait("VerificationId", "V158_F03")]
    public void V158_F03_OriginalAuditSealPermitsOnlyExactExpiredArtifactRetirement()
    {
        using var fixture = new Fixture(); SupportBundleStore.RetentionSeal seal;
        using (var store = fixture.Store()) using (var stage = fixture.Stage(store, Guid.NewGuid()))
            seal = new(stage.Id, stage.Projection.ContentHash, stage.Projection.Bytes.Length, stage.Expires);
        var oldPath = Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
        fixture.Now = seal.Expires;
        using var restarted = fixture.Store(seal); using var replacement = fixture.Stage(restarted, Guid.NewGuid());
        Assert.False(File.Exists(oldPath)); Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
    }

    [Fact, Trait("VerificationId", "V158_F04")]
    public void V158_F04_RewritingFilenameToAnEarlierExpiryCannotAuthorizeEarlyDeletion()
    {
        using var fixture = new Fixture(); SupportBundleStore.RetentionSeal seal;
        using (var store = fixture.Store()) using (var stage = fixture.Stage(store, Guid.NewGuid()))
            seal = new(stage.Id, stage.Projection.ContentHash, stage.Projection.Bytes.Length, stage.Expires);
        var original = Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
        var shiftedCreated = fixture.Now.AddDays(-1);
        var shiftedExpiry = seal.Expires.AddDays(-1);
        var moved = Path.Combine(fixture.Root, "support-" + shiftedCreated.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" +
            shiftedExpiry.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + seal.BundleId.ToString("N") + ".sib");
        File.Move(original, moved);
        using var restarted = fixture.Store(seal);
        Assert.Throws<InvalidOperationException>(() => fixture.Stage(restarted, Guid.NewGuid()));
        Assert.True(File.Exists(moved));
    }

    [Fact, Trait("VerificationId", "V158_F05")]
    public void V158_F05_ExpiredArtifactTamperingRefusesDeletionInsteadOfBuyingCapacity()
    {
        using var fixture = new Fixture(); SupportBundleStore.RetentionSeal seal;
        using (var store = fixture.Store()) using (var stage = fixture.Stage(store, Guid.NewGuid()))
            seal = new(stage.Id, stage.Projection.ContentHash, stage.Projection.Bytes.Length, stage.Expires);
        var path = Assert.Single(Directory.GetFiles(fixture.Root, "*.sib"));
        var bytes = File.ReadAllBytes(path); bytes[^2] ^= 1; File.WriteAllBytes(path, bytes);
        fixture.Now = seal.Expires;
        using var restarted = fixture.Store(seal);
        Assert.Throws<InvalidOperationException>(() => fixture.Stage(restarted, Guid.NewGuid()));
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("VerificationId", "V158_F06")]
    public void V158_F06_CancelledOrReserveRejectedStageWritesNoArtifact()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using (var store = fixture.Store())
            Assert.Throws<OperationCanceledException>(() => store.Stage(Guid.NewGuid(), fixture.Scope, fixture.Logging,
                Array.Empty<string>(), cancellation.Token));
        using (var store = fixture.Store(reserve: long.MaxValue))
            Assert.Throws<InvalidOperationException>(() => fixture.Stage(store, Guid.NewGuid()));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.sib"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        internal Fixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "si58files", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory); Directory.CreateDirectory(Path.Combine(_directory, "db"));
            Root = Path.Combine(_directory, "support"); Logging = DiagnosticCapturePipelineTests.Policy();
            Policy = SupportBundleProjectionTests.Policy(Logging);
            var files = new DiagnosticLocalStoreOptions(Root, true, Policy.MaximumBundleBytes, Policy.MaximumBundleBytes,
                1, Policy.MaximumBundleBytes, Policy.ExportTimeout, Policy.Retention);
            Options = new(Policy, files, DiagnosticDirectoryInstallation.Install(files));
            Scope = new(Guid.NewGuid(), Now.AddHours(-1), Now, Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
        }
        internal DateTimeOffset Now { get; set; } = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        internal string Root { get; }
        internal LoggingDiagnosticsPolicy Logging { get; }
        internal DiagnosticSupportPolicy Policy { get; }
        internal SupportBundleOptions Options { get; }
        internal SupportBundleScope Scope { get; }
        internal SupportBundleStore Store(SupportBundleStore.RetentionSeal? seal = null, long reserve = 0) =>
            new(Options, Path.Combine(_directory, "db", "trace.db"), reserve, 0,
                seal is null ? Array.Empty<SupportBundleStore.RetentionSeal>() : new[] { seal }, () => Now);
        internal SupportBundleStore.PreparedBundle Stage(SupportBundleStore store, Guid id) =>
            store.Stage(id, Scope, Logging, Array.Empty<string>(), CancellationToken.None);
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "si58files")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SupportFixtureCleanupBoundaryInvalid");
            Directory.Delete(_directory, recursive: true);
        }
    }
}
