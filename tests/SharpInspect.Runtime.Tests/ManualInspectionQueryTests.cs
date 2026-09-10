using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded, read-only boundary checks for the schema-21 Manual history query.
/// These tests deliberately do not create Manual rows: availability and schema
/// admission must be decided before any projection is attempted.
/// </summary>
public sealed class ManualInspectionQueryTests
{
    [Fact]
    public async Task V135_Q01_MissingManualConfigurationDoesNotCreateDatabaseOrKey()
    {
        await using var directory = new TemporaryStoreDirectory();
        var query = new SqliteManualInspectionQuery(directory.Options());
        var before = SnapshotTree(directory.Root);

        var current = await query.ReadCurrentAsync();
        Assert.False(current.Available);
        Assert.Equal("ManualInspectionUnavailable", current.ReasonCode);

        var exact = await query.ReadAsync(Guid.NewGuid());
        Assert.False(exact.Available);
        Assert.Equal("ManualInspectionUnavailable", exact.ReasonCode);

        var page = await query.QueryAsync(new ManualInspectionHistoryFilter(PageSize: 20));
        Assert.False(page.Available);
        Assert.Equal("ManualInspectionUnavailable", page.ReasonCode);
        Assert.Empty(page.Events);
        Assert.Empty(page.Runs);

        AssertSnapshotUnchanged(before, directory.Root);
    }

    [Fact]
    public async Task V135_Q02_MissingDatabaseIsUnavailableWithoutCreatingDatabaseOrKey()
    {
        RequireWindows();
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            cameraSetup: new CameraSetupStoreOptions());
        await StopFixtureAsync(fixture);

        var root = Path.GetDirectoryName(fixture.Options.DatabasePath)!;
        var missingDatabase = Path.Combine(root, "manual-query-missing.sqlite");
        var options = Rebind(fixture.Options, missingDatabase,
            new ManualInspectionStoreOptions());
        var query = new SqliteManualInspectionQuery(options);
        var before = SnapshotTree(root);

        var current = await query.ReadCurrentAsync();
        Assert.False(current.Available);
        Assert.Equal("ManualInspectionHistoryUnavailable", current.ReasonCode);

        var exact = await query.ReadAsync(Guid.NewGuid());
        Assert.False(exact.Available);
        Assert.Equal("ManualInspectionHistoryUnavailable", exact.ReasonCode);

        var page = await query.QueryAsync(new ManualInspectionHistoryFilter(PageSize: 20));
        Assert.False(page.Available);
        Assert.Equal("ManualInspectionHistoryUnavailable", page.ReasonCode);

        Assert.False(File.Exists(missingDatabase));
        AssertSnapshotUnchanged(before, root);
    }

    [Fact]
    public async Task V135_Q03_InvalidFiltersAreRejectedBeforeStoreAccess()
    {
        await using var directory = new TemporaryStoreDirectory();
        var query = new SqliteManualInspectionQuery(directory.Options());
        var before = SnapshotTree(directory.Root);
        var invalidFilters = new[]
        {
            new ManualInspectionHistoryFilter(SessionId: Guid.Empty, PageSize: 20),
            new ManualInspectionHistoryFilter(RunId: Guid.Empty, PageSize: 20),
            new ManualInspectionHistoryFilter(AfterPosition: -1, PageSize: 20),
            new ManualInspectionHistoryFilter(AfterPosition: 2, ThroughPosition: 1, PageSize: 20),
            new ManualInspectionHistoryFilter(PageSize: 0),
            new ManualInspectionHistoryFilter(PageSize: 129)
        };

        foreach (var filter in invalidFilters)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                query.QueryAsync(filter).AsTask());
        }

        AssertSnapshotUnchanged(before, directory.Root);
    }

    [Fact]
    public async Task V135_Q04_PreCancelledReadDoesNotOpenOrCreateStore()
    {
        await using var directory = new TemporaryStoreDirectory();
        var query = new SqliteManualInspectionQuery(directory.Options());
        var before = SnapshotTree(directory.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            query.ReadCurrentAsync(cancellation.Token).AsTask());

        AssertSnapshotUnchanged(before, directory.Root);
    }

    [Fact]
    public async Task V135_Q05_OldSchemaIsRejectedWithoutMutatingDatabaseOrKey()
    {
        RequireWindows();
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            cameraSetup: new CameraSetupStoreOptions());
        Assert.Equal(10L, await fixture.ScalarAsync("PRAGMA user_version;"));
        await StopFixtureAsync(fixture);

        var root = Path.GetDirectoryName(fixture.Options.DatabasePath)!;
        var options = Rebind(fixture.Options, fixture.Options.DatabasePath,
            new ManualInspectionStoreOptions());
        var query = new SqliteManualInspectionQuery(options);
        var before = SnapshotTree(root);

        var current = await query.ReadCurrentAsync();
        AssertRejected(current);

        var exact = await query.ReadAsync(Guid.NewGuid());
        AssertRejected(exact);

        var page = await query.QueryAsync(new ManualInspectionHistoryFilter(PageSize: 20));
        Assert.False(page.Available);
        Assert.Equal("ManualInspectionGovernedMigrationRequired", page.ReasonCode);
        Assert.Empty(page.Events);
        Assert.Empty(page.Runs);

        AssertSnapshotUnchanged(before, root);
    }

    private static void AssertRejected(ManualInspectionHistoryReadResult result)
    {
        Assert.False(result.Available);
        Assert.Equal("ManualInspectionGovernedMigrationRequired", result.ReasonCode);
        Assert.Null(result.Header);
        Assert.Null(result.LatestRun);
    }

    private static ProductionStoreOptions Rebind(ProductionStoreOptions source,
        string databasePath, ManualInspectionStoreOptions? manualInspections) =>
        new(databasePath)
        {
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity,
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            ExternalAuditAnchor = source.ExternalAuditAnchor,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationSessions = source.CalibrationSessions,
            CalibrationGovernance = source.CalibrationGovernance,
            RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts,
            RecipeActivations = source.RecipeActivations,
            PreviewSessions = source.PreviewSessions,
            CalibrationImports = source.CalibrationImports,
            ManualInspections = manualInspections
        };

    private static async Task StopFixtureAsync(RecipeDraftStorageTests.Fixture fixture)
    {
        fixture.Authorization.Dispose();
        await fixture.Sessions.DisposeAsync();
        await fixture.Store.DisposeAsync();
    }

    private static string[] SnapshotTree(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            // SQLite may materialize empty WAL/SHM metadata sidecars while a
            // WAL database is opened read-only. The database, non-empty WAL
            // and signing-key files remain the immutable evidence under test;
            // only a newly-created empty WAL is allowed below.
            .Where(path => !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(SnapshotFile)
            .ToArray();
    }

    private static void AssertSnapshotUnchanged(string[] before, string root)
    {
        var after = SnapshotTree(root);
        var beforeSet = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterSet = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = afterSet.Except(beforeSet, StringComparer.OrdinalIgnoreCase).ToArray();
        var allowedEmptyWal = added.Where(IsNewEmptyWalSidecar).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpectedAdded = added.Except(allowedEmptyWal, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = beforeSet.Except(afterSet, StringComparer.OrdinalIgnoreCase).ToArray();

        // Q05 observed one zero-byte drafts.sqlite-wal created by the
        // read-only WAL open. This metadata sidecar does not contain store or
        // key bytes; any non-empty WAL or changed/removed file still fails.
        Assert.True(unexpectedAdded.Length == 0 && removed.Length == 0,
            "Store or signing-key files changed.\n" + SnapshotDiff(before, after));
    }

    private static string SnapshotDiff(string[] before, string[] after)
    {
        var beforeSet = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterSet = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = afterSet.Except(beforeSet, StringComparer.OrdinalIgnoreCase).OrderBy(value => value,
            StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = beforeSet.Except(afterSet, StringComparer.OrdinalIgnoreCase).OrderBy(value => value,
            StringComparer.OrdinalIgnoreCase).ToArray();
        var allowedEmptyWal = added.Where(IsNewEmptyWalSidecar).ToArray();
        var unexpectedAdded = added.Except(allowedEmptyWal, StringComparer.OrdinalIgnoreCase).ToArray();
        return "Added:\n" + (unexpectedAdded.Length == 0 ? "(none)" : string.Join("\n", unexpectedAdded)) +
            "\nAllowed empty WAL sidecars:\n" +
            (allowedEmptyWal.Length == 0 ? "(none)" : string.Join("\n", allowedEmptyWal)) +
            "\nRemoved:\n" + (removed.Length == 0 ? "(none)" : string.Join("\n", removed));
    }

    private static bool IsNewEmptyWalSidecar(string snapshot)
    {
        var pathSeparator = snapshot.IndexOf('|');
        if (pathSeparator <= 0 ||
            !snapshot[..pathSeparator].EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            return false;

        var lengthSeparator = snapshot.IndexOf('|', pathSeparator + 1);
        return lengthSeparator > pathSeparator + 1 &&
            long.TryParse(snapshot.AsSpan(pathSeparator + 1,
                lengthSeparator - pathSeparator - 1), out var length) &&
            length == 0;
    }

    private static string SnapshotFile(string path)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(stream));
        return string.Join("|", path, info.Length, info.LastWriteTimeUtc.Ticks, hash);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Manual query store requires Windows machine key protection.");
    }

    private sealed class TemporaryStoreDirectory : IAsyncDisposable
    {
        internal TemporaryStoreDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V135-ManualQuery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "manual.sqlite");
        }

        internal string Root { get; }
        internal string DatabasePath { get; }

        internal ProductionStoreOptions Options() => new(DatabasePath);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}
