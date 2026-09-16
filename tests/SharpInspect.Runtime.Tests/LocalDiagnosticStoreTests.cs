using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using SharpInspect.Runtime.Diagnostics;
using Xunit;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>V156 evidence: real NTFS roots under the process temp directory (SharpInspect-T56),
/// restricted ACLs, sealed rolling windows, executable retention, bounded reads and the Storage
/// Reserve. Every fixture directory is fresh and removed only after the test.</summary>
public sealed class LocalDiagnosticStoreTests
{
    [Fact, Trait("VerificationId", "V156_F14")]
    public async Task V156_F14_BoundedHistoryIsTheLatestSuffixAndDoesNotFillGapsWithOlderFiles()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot(), maximumRecordBytes: 128, maximumFileBytes: 512,
            maximumFiles: 4, maximumTotalBytes: 2048, rollAfter: TimeSpan.FromSeconds(1));
        await using var fixture = new Fixture(sandbox, options, Start);
        await fixture.WriteAsync("{\"id\":0}");
        fixture.Now = Start.AddSeconds(1);
        var newer = "{\"payload\":\"" + new string('a', 40) + "\"}";
        await fixture.WriteAsync(newer); await fixture.WriteAsync(newer); await fixture.WriteAsync(newer);
        Assert.Equal(new[] { newer, newer }, await fixture.ReadAsync(bytes: newer.Length * 2 + 10));
    }

    [Fact, Trait("VerificationId", "V156_F13")]
    public async Task V156_F13_AnotherStoreCannotAcquireTheRootUntilThePhysicalOwnerRetires()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot(), maximumFiles: 1);
        await using var first = new Fixture(sandbox, options, Start);
        await first.WriteAsync("{\"owner\":1}");
        await using var second = new Fixture(sandbox, options, Start, binding: first.Binding);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WriteAsync("{\"owner\":2}").AsTask());
        Assert.Equal("DiagnosticStoreWriterUnavailable", rejected.Message);
        Assert.Single(first.Files());
        await first.DisposeAsync();
        // The original file is unexpired. Reacquisition succeeds but never buys room by deleting it.
        var capacity = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WriteAsync("{\"owner\":2}").AsTask());
        Assert.Equal("DiagnosticStoreFileBudgetExceeded", capacity.Message);
        Assert.Equal(new[] { "{\"owner\":1}" }, await second.ReadAsync());
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact, Trait("VerificationId", "V156_F01")]
    public void V156_F01_OptionsAndInstallCreateOnlyANamedEmptyRestrictedDirectory()
    {
        using var sandbox = new Sandbox();
        Assert.Throws<ArgumentException>(() => Options("relative\\store"));
        Assert.Throws<ArgumentException>(() => Options(@"C:\"));
        Assert.Throws<ArgumentException>(() => Options(@"\\server\share\store"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), maximumRecordBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), maximumRecordBytes: 1024, maximumFileBytes: 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), maximumFiles: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), maximumFileBytes: 4096, maximumTotalBytes: 2048));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), rollAfter: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(sandbox.NewRoot(), retention: TimeSpan.Zero));

        var directory = sandbox.NewRoot();
        var options = Options(directory);
        Assert.False(Directory.Exists(directory)); // constructing options performs no IO
        var binding = DiagnosticDirectoryInstallation.Install(options);
        Assert.True(Directory.Exists(directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        Assert.True(DiagnosticDirectoryInstallation.Verify(options, binding));
        Assert.False(DiagnosticDirectoryInstallation.Verify(options, new string('A', 64)));
        Assert.False(DiagnosticDirectoryInstallation.Verify(options, "not-a-hash"));
        Assert.Equal(64, binding.Length);
        Assert.Equal(binding, binding.ToUpperInvariant());
        Assert.Equal(options.BindingHash, Options(directory).BindingHash);
        Assert.NotEqual(options.BindingHash, Options(directory, isProtected: true).BindingHash);
        Assert.NotEqual(options.BindingHash, Options(directory, maximumFiles: 9).BindingHash);
        AssertRestrictedDirectoryAcl(directory);

        // An existing non-empty directory is never adopted.
        var occupied = sandbox.NewRoot();
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        var refused = Assert.Throws<InvalidOperationException>(() => DiagnosticDirectoryInstallation.Install(Options(occupied)));
        Assert.Equal("DiagnosticDirectoryNotEmpty", refused.Message);
        Assert.Equal("keep", ReadSharedText(Path.Combine(occupied, "keep.txt")));

        // An existing empty directory is restricted before the binding is issued.
        var existing = sandbox.NewRoot();
        Directory.CreateDirectory(existing);
        var existingOptions = Options(existing);
        var existingBinding = DiagnosticDirectoryInstallation.Install(existingOptions);
        Assert.True(DiagnosticDirectoryInstallation.Verify(existingOptions, existingBinding));
        AssertRestrictedDirectoryAcl(existing);
    }

    [Fact, Trait("VerificationId", "V156_F02")]
    public void V156_F02_InstallRefusesReparseMissingAncestorsAndInventedAncestors()
    {
        using var sandbox = new Sandbox();
        var existing = sandbox.NewDirectory("existing");
        var missing = Path.Combine(existing, "missing", "leaf");
        var refused = Assert.Throws<InvalidOperationException>(() => DiagnosticDirectoryInstallation.Install(Options(missing)));
        Assert.Equal("DiagnosticDirectoryParentMissing", refused.Message);
        Assert.False(Directory.Exists(Path.Combine(existing, "missing")));

        var target = sandbox.NewDirectory("target");
        var link = sandbox.NewRoot();
        if (TryCreateDirectoryReparse(link, target))
        {
            try
            {
                var reparse = Assert.Throws<InvalidOperationException>(() => DiagnosticDirectoryInstallation.Install(Options(link)));
                Assert.StartsWith("DiagnosticDirectoryRejected", reparse.Message);
                Assert.Contains("StorePathReparsePoint", reparse.Message);
                Assert.False(DiagnosticDirectoryInstallation.Verify(Options(link), new string('A', 64)));
            }
            finally
            {
                try { Directory.Delete(link, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact, Trait("VerificationId", "V156_F03")]
    public async Task V156_F03_ActualAclAndRootIdentityAreRecheckedBeforeEveryOperation()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot());
        var binding = DiagnosticDirectoryInstallation.Install(options);
        await using var fixture = new Fixture(sandbox, options, Start, binding: binding);
        await fixture.WriteAsync("{\"n\":1}");
        var file = Assert.Single(fixture.Files());
        Assert.Equal("{\"n\":1}\n", ReadSharedText(file));

        // ACL tampering: a third trustee invalidates the actual installation, never a flag.
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var added = new FileSystemAccessRule(world, FileSystemRights.Read, AccessControlType.Allow);
        var security = new DirectorySecurity(options.Directory, AccessControlSections.Access);
        var originalAcl = security.GetSecurityDescriptorBinaryForm();
        var originalSddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        security.AddAccessRule(added);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(options.Directory), security);
        Assert.False(DiagnosticDirectoryInstallation.Verify(options, binding));
        var tampered = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"n\":2}").AsTask());
        Assert.Equal("DiagnosticStoreInstallationInvalid", tampered.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync().AsTask());

        security.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(options.Directory), security);
        var restoredSddl = new DirectorySecurity(options.Directory, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.True(DiagnosticDirectoryInstallation.Verify(options, binding), "DaclFlags=" + originalSddl.Split('(')[0] +
            " -> " + restoredSddl.Split('(')[0] + "; AceTextEqual=" +
            (originalSddl[originalSddl.IndexOf('(')..] == restoredSddl[restoredSddl.IndexOf('(')..]));
        await fixture.WriteAsync("{\"n\":2}");

        // Root replacement: the same path with a new directory identity cannot adopt the old binding.
        await fixture.DisposeAsync();
        var moved = options.Directory + "-replaced";
        Directory.Move(options.Directory, moved);
        Assert.Equal("{\"n\":1}\n{\"n\":2}\n", ReadSharedText(Path.Combine(moved, Path.GetFileName(file))));
        var freshBinding = DiagnosticDirectoryInstallation.Install(options);
        Assert.NotEqual(binding, freshBinding);
        Assert.False(DiagnosticDirectoryInstallation.Verify(options, binding));
        Assert.True(DiagnosticDirectoryInstallation.Verify(options, freshBinding));
    }

    [Fact, Trait("VerificationId", "V156_F04")]
    public async Task V156_F04_NonConformingRecordsAndBoundsAreRejectedBeforeAnyIo()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot());
        await using var fixture = new Fixture(sandbox, options, Start);

        var rejected = new[]
        {
            string.Empty, "\n", "{\"a\":1}\r\n", "{\"a\":1}\n{\"b\":2}", "not-json", "[1,2]", "{\"a\":1", "{\"a\":1}}"
        };
        foreach (var value in rejected)
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.WriteRawAsync(Encoding.UTF8.GetBytes(value)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.WriteRawAsync(new byte[] { (byte)'{', 0xC3, 0x28, (byte)'}' }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.WriteRawAsync(Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('x', 600) + "\"}")).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.WriteRawAsync(Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('x', 508) + "\"}\n\n")).AsTask());

        // Rejection happens before any open, roll or append: the root stays empty.
        Assert.Empty(fixture.Files());
        Assert.Empty(await fixture.ReadAsync(records: 0, bytes: 1024));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ReadAsync(records: -1).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ReadAsync(bytes: -1).AsTask());
    }

    [Fact, Trait("VerificationId", "V156_F12")]
    public async Task V156_F12_ValidRecordsNormalizeToSingleNewlineTerminatedJsonLines()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot());
        await using var fixture = new Fixture(sandbox, options, Start);
        await fixture.WriteAsync("{\"a\":1}");
        await fixture.WriteAsync("{\"b\":2}\n");
        await fixture.WriteAsync("{}");
        var file = Assert.Single(fixture.Files());
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n{}\n", ReadSharedText(file));
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "{}" }, await fixture.ReadAsync());
    }

    [Fact, Trait("VerificationId", "V156_F05")]
    public async Task V156_F05_RollingUsesTheSealedWindowAndRestartNeverReusesAFile()
    {
        using var sandbox = new Sandbox();
        var rollAfter = TimeSpan.FromSeconds(2);
        var retention = TimeSpan.FromSeconds(30);
        var options = Options(sandbox.NewRoot(), rollAfter: rollAfter, retention: retention);
        string binding;
        await using (var fixture = new Fixture(sandbox, options, Start))
        {
            binding = fixture.Binding;
            await fixture.WriteAsync("{\"step\":\"a\"}");
            var first = Assert.Single(fixture.Files());
            AssertSealedName(first, Start, rollAfter, retention);

            fixture.Now = Start.AddMilliseconds(1999);
            await fixture.WriteAsync("{\"step\":\"b\"}");
            Assert.Equal("{\"step\":\"a\"}\n{\"step\":\"b\"}\n", ReadSharedText(first));

            // The sealed window closes exactly at creation + rollAfter; a new file is never optional.
            fixture.Now = Start.AddMilliseconds(2000);
            await fixture.WriteAsync("{\"step\":\"c\"}");
            var files = fixture.Files();
            Assert.Equal(2, files.Length);
            var second = files.Single(x => x != first);
            Assert.Equal("{\"step\":\"a\"}\n{\"step\":\"b\"}\n", ReadSharedText(first));
            Assert.Equal("{\"step\":\"c\"}\n", ReadSharedText(second));

            // A clock rollback is conservative: nothing is retired and the still-open window keeps
            // accepting records whose own minimum retention is covered by the sealed expiry.
            fixture.Now = Start.AddMilliseconds(1500);
            await fixture.WriteAsync("{\"step\":\"d\"}");
            Assert.Equal(2, fixture.Files().Length);
            Assert.Equal("{\"step\":\"c\"}\n{\"step\":\"d\"}\n", ReadSharedText(second));
        }

        // A restarted store always opens a fresh file and never adopts an existing one.
        await using (var restarted = new Fixture(sandbox, options, Start.AddMilliseconds(1500), binding: binding))
        {
            await restarted.WriteAsync("{\"step\":\"e\"}");
            Assert.Equal(3, restarted.Files().Length);
            var fresh = restarted.Files().Single(x => ReadSharedText(x) == "{\"step\":\"e\"}\n");
            AssertSealedName(fresh, Start.AddMilliseconds(1500), rollAfter, retention);
        }
    }

    [Fact, Trait("VerificationId", "V156_F06")]
    public async Task V156_F06_ExpiryDrivesRetirementAndNeverUsesFileSystemTimestamps()
    {
        using var sandbox = new Sandbox();
        var rollAfter = TimeSpan.FromSeconds(1);
        var retention = TimeSpan.FromSeconds(1);
        var options = Options(sandbox.NewRoot(), rollAfter: rollAfter, retention: retention);
        await using var fixture = new Fixture(sandbox, options, Start);
        await fixture.WriteAsync("{\"step\":\"a\"}");
        var first = Assert.Single(fixture.Files());

        fixture.Now = Start.AddSeconds(1);
        await fixture.WriteAsync("{\"step\":\"b\"}");
        var second = fixture.Files().Single(x => x != first);

        // second is sealed at t0 + 2.5s; rewrite its file-system timestamps to an old date.
        fixture.Now = Start.AddSeconds(2.5);
        await fixture.WriteAsync("{\"step\":\"c\"}");
        Assert.False(File.Exists(first));
        var third = fixture.Files().Single(x => x != second);
        File.SetCreationTimeUtc(second, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Its sealed expiry (t0 + 3s) still governs: it survives past the rewritten timestamps.
        fixture.Now = Start.AddSeconds(2.9);
        await fixture.WriteAsync("{\"step\":\"d\"}");
        Assert.True(File.Exists(second));
        Assert.Equal("{\"step\":\"b\"}\n", ReadSharedText(second));
        Assert.Equal("{\"step\":\"c\"}\n{\"step\":\"d\"}\n", ReadSharedText(third));

        // At the sealed expiry the file is retired even though its file-system times are far older.
        fixture.Now = Start.AddSeconds(3);
        await fixture.WriteAsync("{\"step\":\"e\"}");
        Assert.False(File.Exists(second));
        Assert.Equal("{\"step\":\"c\"}\n{\"step\":\"d\"}\n{\"step\":\"e\"}\n", ReadSharedText(third));
        Assert.Single(fixture.Files());
    }

    [Fact, Trait("VerificationId", "V156_F07")]
    public async Task V156_F07_HardLinkedOrForeignEntriesFailClosedWithoutRetirement()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot());
        await using var fixture = new Fixture(sandbox, options, Start);
        await fixture.WriteAsync("{\"step\":\"a\"}");
        var file = Assert.Single(fixture.Files());

        // An owned-looking second name for the same file must fail the single-link check.
        var match = Regex.Match(Path.GetFileName(file), @"^diag-(\d{13})-(\d{13})-");
        var alias = Path.Combine(options.Directory,
            "diag-" + match.Groups[1].Value + "-" + match.Groups[2].Value + "-" + new string('0', 32) + ".jsonl");
        Assert.True(CreateHardLink(alias, file, IntPtr.Zero));
        var linked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"step\":\"b\"}").AsTask());
        Assert.Equal("DiagnosticStoreEntryLinksInvalid", linked.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync().AsTask());
        Assert.Equal("{\"step\":\"a\"}\n", ReadSharedText(file));
        File.Delete(alias);

        await fixture.WriteAsync("{\"step\":\"b\"}");
        Assert.Equal("{\"step\":\"a\"}\n{\"step\":\"b\"}\n", ReadSharedText(file));

        var foreign = Path.Combine(options.Directory, "notes.txt");
        File.WriteAllText(foreign, "keep");
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"step\":\"c\"}").AsTask());
        Assert.Equal("DiagnosticStoreInventoryUnknownEntry", unknown.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync().AsTask());
        Assert.Equal("keep", ReadSharedText(foreign));

        var decoy = Path.Combine(options.Directory, "diag-0000000000001-0000000000002-" + new string('0', 32) + ".jsonl");
        File.WriteAllText(decoy, "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"step\":\"c\"}").AsTask());
        Assert.Equal("keep", ReadSharedText(foreign));
        Assert.Equal("{}", ReadSharedText(decoy));
    }

    [Fact, Trait("VerificationId", "V156_F08")]
    public async Task V156_F08_BudgetsRefuseWithoutRetiringUnexpiredFiles()
    {
        using var sandbox = new Sandbox();

        // File-count budget: a full unexpired inventory rejects the write; only real expiry frees it.
        var countOptions = Options(sandbox.NewRoot(), rollAfter: TimeSpan.FromSeconds(1),
            retention: TimeSpan.FromSeconds(120), maximumFiles: 1);
        await using (var fixture = new Fixture(sandbox, countOptions, Start))
        {
            await fixture.WriteAsync("{\"step\":\"a\"}");
            var only = Assert.Single(fixture.Files());
            fixture.Now = Start.AddSeconds(1);
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"step\":\"b\"}").AsTask());
            Assert.Equal("DiagnosticStoreFileBudgetExceeded", refusal.Message);
            Assert.Single(fixture.Files());
            Assert.Equal("{\"step\":\"a\"}\n", ReadSharedText(only));

            fixture.Now = Start.AddSeconds(121);
            await fixture.WriteAsync("{\"step\":\"b\"}");
            Assert.False(File.Exists(only));
            var replacement = Assert.Single(fixture.Files());
            Assert.Equal("{\"step\":\"b\"}\n", ReadSharedText(replacement));
        }

        // Byte budget: exact file and total budgets refuse; nothing unexpired is ever deleted.
        var byteOptions = Options(sandbox.NewRoot(), maximumRecordBytes: 64, maximumFileBytes: 100,
            maximumTotalBytes: 100);
        var record = PaddedRecord(29);
        Assert.Equal(29, Encoding.UTF8.GetByteCount(record));
        await using (var fixture = new Fixture(sandbox, byteOptions, Start))
        {
            await fixture.WriteAsync(record);
            await fixture.WriteAsync(record);
            await fixture.WriteAsync(record);
            var only = Assert.Single(fixture.Files());
            Assert.Equal(90, new FileInfo(only).Length);

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(record).AsTask());
            Assert.Equal("DiagnosticStoreByteBudgetExceeded", refusal.Message);
            Assert.Single(fixture.Files());
            Assert.Equal(90, new FileInfo(only).Length);
            Assert.Equal(3, ReadSharedText(only).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    [Fact, Trait("VerificationId", "V156_F09")]
    public async Task V156_F09_ReadIsBoundedOrderedAndNeverReturnsPartialLines()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot(), rollAfter: TimeSpan.FromSeconds(1), retention: TimeSpan.FromSeconds(120));
        var expected = new[] { "{\"i\":1}", "{\"i\":2}", "{\"i\":3}", "{\"i\":4}", "{\"i\":5}" };
        string binding;
        await using (var fixture = new Fixture(sandbox, options, Start))
        {
            binding = fixture.Binding;
            foreach (var value in expected.Take(3)) await fixture.WriteAsync(value);
            fixture.Now = Start.AddSeconds(1);
            foreach (var value in expected.Skip(3)) await fixture.WriteAsync(value);

            Assert.Equal(expected, await fixture.ReadAsync());
            Assert.Equal(expected.Skip(3), await fixture.ReadAsync(records: 2));
            Assert.Equal(expected.TakeLast(1), await fixture.ReadAsync(records: 1000, bytes: 7));
            Assert.Empty(await fixture.ReadAsync(records: 1000, bytes: 6));
            Assert.Equal(expected.TakeLast(1), await fixture.ReadAsync(records: 1));
            Assert.Empty(await fixture.ReadAsync(records: 0, bytes: 1024));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ReadAsync(records: -1).AsTask());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ReadAsync(bytes: -1).AsTask());

            await fixture.DisposeAsync();
            var newest = fixture.Files().OrderByDescending(x => x, StringComparer.Ordinal).First();
            File.AppendAllText(newest, "{\"i\":6");
        }

        await using (var reopened = new Fixture(sandbox, options, Start.AddSeconds(1.5), binding: binding))
        {
            // The torn trailing fragment is discarded; no partial line is ever emitted.
            Assert.Equal(expected, await reopened.ReadAsync());
            Assert.Equal(expected, await reopened.ReadAsync(bytes: int.MaxValue - 1));
        }
    }

    [Fact, Trait("VerificationId", "V156_F10")]
    public async Task V156_F10_ReserveAndVolumeRulesRefuseBeforeAppending()
    {
        using var sandbox = new Sandbox();
        var reserveRoot = sandbox.NewRoot();
        var reserveOptions = Options(reserveRoot);
        var volumeTotal = new DriveInfo(Path.GetPathRoot(reserveRoot)!).TotalSize;
        await using (var fixture = new Fixture(sandbox, reserveOptions, Start, reserveBytes: volumeTotal))
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"n\":1}").AsTask());
            Assert.Equal("DiagnosticStoreReserveViolated", refusal.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(reserveRoot));
        }

        var missingDatabase = Path.Combine(sandbox.RunRoot, "absent");
        await using (var fixture = new Fixture(sandbox, Options(sandbox.NewRoot()), Start, databaseDirectory: missingDatabase))
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"n\":1}").AsTask());
            Assert.Equal("DiagnosticStoreDatabaseVolumeUnavailable", refusal.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        }

        var rootVolume = Path.GetPathRoot(reserveRoot)!;
        var other = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady && drive.DriveType == DriveType.Fixed &&
            drive.DriveFormat == "NTFS" && !string.Equals(drive.Name, rootVolume, StringComparison.OrdinalIgnoreCase));
        if (other is null) return;
        var foreignDatabase = Path.Combine(other.Name, "SharpInspect-T56-db-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(foreignDatabase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        try
        {
            await using var fixture = new Fixture(sandbox, Options(sandbox.NewRoot()), Start, databaseDirectory: foreignDatabase);
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"n\":1}").AsTask());
            Assert.Equal("DiagnosticStoreVolumeMismatch", refusal.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        }
        finally
        {
            try { Directory.Delete(foreignDatabase, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact, Trait("VerificationId", "V156_F11")]
    public async Task V156_F11_FileAclsAreRestrictedAndDisposeOrCancellationNeverDeletes()
    {
        using var sandbox = new Sandbox();
        var options = Options(sandbox.NewRoot());
        var fixture = new Fixture(sandbox, options, Start);
        await fixture.WriteAsync("{\"step\":\"a\"}");
        var file = Assert.Single(fixture.Files());
        AssertRestrictedFileAcl(file);

        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var added = new FileSystemAccessRule(world, FileSystemRights.Read, AccessControlType.Allow);
        var security = new FileSecurity(file, AccessControlSections.Access);
        var originalAcl = security.GetSecurityDescriptorBinaryForm();
        var originalSddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        security.AddAccessRule(added);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(file), security);
        var tampered = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync("{\"step\":\"b\"}").AsTask());
        Assert.Equal("DiagnosticStoreEntryAclInvalid", tampered.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync().AsTask());

        security.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(file), security);
        await fixture.WriteAsync("{\"step\":\"b\"}");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Store.WriteAsync(Encoding.UTF8.GetBytes("{\"step\":\"c\"}\n"), canceled.Token).AsTask());
        }

        Assert.Equal("{\"step\":\"a\"}\n{\"step\":\"b\"}\n", ReadSharedText(file));
        await fixture.DisposeAsync();
        await fixture.DisposeAsync(); // idempotent
        Assert.True(File.Exists(file));
        Assert.Equal("{\"step\":\"a\"}\n{\"step\":\"b\"}\n", ReadSharedText(file));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.WriteAsync("{\"step\":\"c\"}").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ReadAsync().AsTask());
    }

    private static DiagnosticLocalStoreOptions Options(string directory, long maximumRecordBytes = 512,
        long maximumFileBytes = 4096, int maximumFiles = 8, long maximumTotalBytes = 32768,
        TimeSpan? rollAfter = null, TimeSpan? retention = null, bool isProtected = false) =>
        new(directory, isProtected, maximumRecordBytes, maximumFileBytes, maximumFiles, maximumTotalBytes,
            rollAfter ?? TimeSpan.FromMinutes(10), retention ?? TimeSpan.FromHours(1));

    private static string PaddedRecord(int contentBytes)
    {
        var padding = contentBytes - 10;
        var content = "{\"pad\":\"" + new string('x', padding) + "\"}";
        Assert.Equal(contentBytes, Encoding.UTF8.GetByteCount(content));
        return content;
    }

    private static void AssertSealedName(string path, DateTimeOffset created, TimeSpan rollAfter, TimeSpan retention)
    {
        var match = Regex.Match(Path.GetFileName(path), @"^diag-(\d{13})-(\d{13})-([0-9a-f]{32})\.jsonl$");
        Assert.True(match.Success, Path.GetFileName(path));
        var createdMilliseconds = long.Parse(match.Groups[1].Value);
        var expiryMilliseconds = long.Parse(match.Groups[2].Value);
        Assert.Equal(created.ToUnixTimeMilliseconds(), createdMilliseconds);
        Assert.Equal(createdMilliseconds + (long)rollAfter.TotalMilliseconds + (long)retention.TotalMilliseconds,
            expiryMilliseconds);
    }

    private static void AssertRestrictedDirectoryAcl(string path)
    {
        var security = new DirectorySecurity(path, AccessControlSections.Access);
        AssertRestrictedAcl(security);
    }

    private static void AssertRestrictedFileAcl(string path)
    {
        var security = new FileSecurity(path, AccessControlSections.Access);
        AssertRestrictedAcl(security);
    }

    private static void AssertRestrictedAcl(FileSystemSecurity security)
    {
        var current = WindowsIdentity.GetCurrent().User!;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
        });
        var identities = rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet();
        Assert.Equal(2, identities.Count);
        Assert.Contains(current, identities);
        Assert.Contains(administrators, identities);
    }

    private static bool TryCreateDirectoryReparse(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        try
        {
            var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            process.WaitForExit(30_000);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                    or PlatformNotSupportedException)
        { return false; }
    }

    private sealed class Clock
    {
        internal DateTimeOffset Now { get; set; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Clock _clock = new();

        internal Fixture(Sandbox sandbox, DiagnosticLocalStoreOptions options, DateTimeOffset now,
            long reserveBytes = 0, decimal reservePercent = 0m, string? databaseDirectory = null, string? binding = null)
        {
            Sandbox = sandbox;
            Options = options;
            _clock.Now = now;
            DatabaseDirectory = databaseDirectory ?? sandbox.NewDirectory("db");
            DatabasePath = Path.Combine(DatabaseDirectory, "trace.db");
            if (binding is null) Binding = DiagnosticDirectoryInstallation.Install(options);
            else if (DiagnosticDirectoryInstallation.Verify(options, binding)) Binding = binding;
            else throw new InvalidOperationException("LocalDiagnosticStoreTestsBindingInvalid");
            Store = new LocalDiagnosticStore(options, Binding, DatabasePath, reserveBytes, reservePercent, () => _clock.Now);
        }

        internal Sandbox Sandbox { get; }
        internal string Root => Options.Directory;
        internal DiagnosticLocalStoreOptions Options { get; }
        internal string Binding { get; }
        internal string DatabaseDirectory { get; }
        internal string DatabasePath { get; }
        internal LocalDiagnosticStore Store { get; }
        internal DateTimeOffset Now { get => _clock.Now; set => _clock.Now = value; }

        internal ValueTask WriteAsync(string json) => Store.WriteAsync(Encoding.UTF8.GetBytes(json), CancellationToken.None);

        internal ValueTask WriteRawAsync(byte[] bytes) => Store.WriteAsync(bytes, CancellationToken.None);

        internal async ValueTask<IReadOnlyList<string>> ReadAsync(int records = 1000, int bytes = 1 << 20) =>
            await Store.ReadLinesAsync(records, bytes, CancellationToken.None);

        internal string[] Files() => Directory.GetFiles(Root, "*.jsonl");

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }

    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            RunRoot = Path.Combine(Path.GetTempPath(), "SharpInspect-T56", "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RunRoot);
        }

        internal string RunRoot { get; }

        internal string NewRoot() => Path.Combine(RunRoot, "store-" + Guid.NewGuid().ToString("N"));

        internal string NewDirectory(string prefix)
        {
            var path = Path.Combine(RunRoot, prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            var expected = Path.Combine(Path.GetTempPath(), "SharpInspect-T56");
            if (!RunRoot.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(RunRoot, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
