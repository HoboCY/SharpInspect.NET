using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Runtime.Conformance;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class ConformanceAnchorReplacementTests
{
    [Fact]
    public async Task V107_A01_TransientReadHandleIsRetriedAndSameAppendCompletesAfterRelease()
    {
        RequireWindows();
        using var fixture = TestFixture.Create(TimeSpan.FromSeconds(2));
        using var ledger = new ConformanceLedger(fixture.Options());
        ledger.Append(new[] { new ConformanceLedgerItem("result", "anchor-retry-1", "one") });

        using var holder = fixture.OpenAnchor(FileShare.Read);
        var pending = Task.Run(() => ledger.Append(
            new[] { new ConformanceLedgerItem("result", "anchor-retry-2", "two") }));
        try
        {
            Assert.True(await WaitForTemporaryFileAsync(fixture.DirectoryPath),
                "The anchor replacement did not reach its temporary file while the read handle was held.");
            Assert.False(pending.IsCompleted,
                "The append finished before the controlled read handle was released.");

            holder.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            holder.Dispose();
            if (!pending.IsCompleted)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { }
            }
        }

        var rows = ledger.ReadAll();
        Assert.Equal(new[] { "anchor-retry-1", "anchor-retry-2" }, rows.Select(row => row.Id));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task V107_A02_PersistentReadHandleFailsWithinDeadlineAndBlocksLedger()
    {
        RequireWindows();
        using var fixture = TestFixture.Create(TimeSpan.FromMilliseconds(300));
        using var ledger = new ConformanceLedger(fixture.Options());
        ledger.Append(new[] { new ConformanceLedgerItem("result", "anchor-timeout-1", "one") });

        using var holder = fixture.OpenAnchor(FileShare.Read);
        var started = Stopwatch.GetTimestamp();
        var pending = Task.Run(() => Assert.Throws<InvalidOperationException>(() => ledger.Append(
            new[] { new ConformanceLedgerItem("result", "anchor-timeout-2", "two") })));
        var error = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) /
            (double)Stopwatch.Frequency);

        Assert.Equal("ConformanceLedgerAnchorWriteFailed", error.Message);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(200),
            $"The persistent handle was not bounded by the configured retry window: {elapsed}.");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Append exceeded bound: {elapsed}.");
        var blocked = Assert.Throws<InvalidOperationException>(() => { _ = ledger.ReadAll(); });
        Assert.Equal("ConformanceLedgerBlocked", blocked.Message);

        using var connection = fixture.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conformance_ledger_entries;";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    private static async Task<bool> WaitForTemporaryFileAsync(string directory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.EnumerateFiles(directory, "*.tmp").Any()) return true;
            await Task.Delay(10);
        }

        return Directory.EnumerateFiles(directory, "*.tmp").Any();
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Conformance ledger acceptance requires Windows local NTFS and DPAPI.");
    }

    private sealed class TestFixture : IDisposable
    {
        private TestFixture(string directoryPath, string databasePath, string keyDirectory,
            string signingKeyName, TimeSpan operationTimeout)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            KeyDirectory = keyDirectory;
            SigningKeyName = signingKeyName;
            OperationTimeout = operationTimeout;
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string KeyDirectory { get; }
        public string SigningKeyName { get; }
        public TimeSpan OperationTimeout { get; }

        public static TestFixture Create(TimeSpan operationTimeout)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V107-ConformanceAnchorReplacement", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TestFixture(directory, Path.Combine(directory, "conformance.sqlite"),
                Path.Combine(directory, "keys"), "SharpInspect.Test.V107.Anchor." +
                Guid.NewGuid().ToString("N"), operationTimeout);
        }

        public ConformanceLedgerOptions Options() => new(DatabasePath, KeyDirectory, SigningKeyName)
        {
            OperationTimeout = OperationTimeout
        };

        public FileStream OpenAnchor(FileShare share) =>
            new(DatabasePath + ".anchor", FileMode.Open, FileAccess.Read, share);

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            // Evidence directories remain available for post-failure inspection.
        }
    }
}

#pragma warning restore CA1416
