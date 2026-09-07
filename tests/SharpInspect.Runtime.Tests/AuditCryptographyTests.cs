using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class AuditCryptographyTests
{
    [Fact]
    public void CanonicalEncodingIsLengthPrefixedNullDistinctAndCultureIndependent()
    {
        var bytes = AuditCanonical.Encode("vector", "alpha", null, "é", "İ");

        Assert.Equal(
            "000000010100000006766563746F72000000040100000005616C706861000100000002C3A90100000002C4B0",
            Convert.ToHexString(bytes));
        Assert.NotEqual(
            Convert.ToHexString(AuditCanonical.Encode("vector", string.Empty)),
            Convert.ToHexString(AuditCanonical.Encode("vector", (string?)null)));

        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(
                "C7E70D1E37532AE9348188D10C73FE5BCA3CB490FF08327E09B3DCBA65996A4C",
                AuditCanonical.Hash("station", 7, AuditCanonical.GenesisHash, new byte[] { 1, 2, 3 }));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void PolicyValidationAndContentHashCoverAllPolicyFields()
    {
        var policy = new AuditIntegrityPolicy("station", "v1", "audit-key");
        policy.Validate();
        Assert.Equal(64, policy.ContentHash.Length);
        Assert.Equal(policy.ContentHash, new AuditIntegrityPolicy("station", "v1", "audit-key").ContentHash);

        Assert.NotEqual(policy.ContentHash,
            (policy with { AnchorTimeout = TimeSpan.FromSeconds(3) }).ContentHash);
        Assert.NotEqual(policy.ContentHash,
            (policy with { KeyDirectory = Path.Combine(Path.GetTempPath(), "other-key-root") }).ContentHash);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { CheckpointEveryEntries = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { BackgroundVerificationEntries = 10_001 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (policy with { StationId = new string('x', 129) }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (policy with { RequireExternalAnchor = true }).Validate());
    }

    [Fact]
    public void CheckpointVerificationRejectsTamperedFieldsAndSignature()
    {
        using var key = new TestSigningKey();
        var policy = new AuditIntegrityPolicy("station", "v1", "audit-key");
        var checkpoint = AuditCheckpointCrypto.Create(
            policy, key, 7, AuditCanonical.GenesisHash, new DateTimeOffset(2026, 9, 7, 1, 2, 3, TimeSpan.Zero));

        Assert.True(AuditCheckpointCrypto.Verify(checkpoint));
        Assert.False(AuditCheckpointCrypto.Verify(checkpoint with { Sequence = 8 }));
        Assert.False(AuditCheckpointCrypto.Verify(checkpoint with
        {
            SignatureBase64 = Convert.ToBase64String(Convert.FromBase64String(checkpoint.SignatureBase64)
                .Select((value, index) => index == 0 ? (byte)(value ^ 0x01) : value).ToArray())
        }));
        Assert.False(AuditCheckpointCrypto.Verify(checkpoint with { SigningKeyId = new string('A', 64) }));
        Assert.False(AuditCheckpointCrypto.Verify(checkpoint with { PublicKeyBase64 = "AQ==" }));
        Assert.Throws<ArgumentException>(() => AuditCheckpointCrypto.Create(
            policy, key, 7, AuditCanonical.GenesisHash, default));
    }

    [Fact]
    public async Task WindowsDpapiKeyCreationIsProtectedReopenBoundAndRejectsTampering()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Windows DPAPI machine protection is only applicable on Windows hosts.");

        var keyDirectory = Path.Combine(Path.GetTempPath(), "SharpInspect.AuditTests." + Guid.NewGuid().ToString("N"));
        var keyName = "test-key-" + Guid.NewGuid().ToString("N");
        var policy = new AuditIntegrityPolicy("test-station", "v1", keyName)
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = keyDirectory
        };
        var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
        var expectedNameHash = Convert.ToHexString(SHA256.HashData(
            new UTF8Encoding(false, true).GetBytes(keyName))) + ".key";
        Assert.Equal(expectedNameHash, Path.GetFileName(keyPath));

        try
        {
            using (var key = WindowsMachineAuditKey.Open(policy, allowCreation: true, out var created))
            {
                Assert.True(created);
                Assert.Equal(64, key.KeyId.Length);
                Assert.Equal(64, key.Sign(Encoding.UTF8.GetBytes("test")).Length);
                var checkpoint = AuditCheckpointCrypto.Create(
                    policy, key, 1, AuditCanonical.GenesisHash, DateTimeOffset.UtcNow);
                Assert.True(AuditCheckpointCrypto.Verify(checkpoint));
            }
            Assert.True(File.Exists(keyPath));
            var protectedFile = File.ReadAllBytes(keyPath);
            Assert.True(protectedFile.Length > 12 && protectedFile.Length <= 12 + 16 * 1024);
            Assert.Equal(Encoding.ASCII.GetBytes("SAK1"), protectedFile.Take(4).ToArray());
            Assert.True(new DirectorySecurity(keyDirectory, AccessControlSections.Access).AreAccessRulesProtected);
            Assert.True(new FileSecurity(keyPath, AccessControlSections.Access).AreAccessRulesProtected);

            using (var key = WindowsMachineAuditKey.Open(policy, allowCreation: false, out var created))
            {
                Assert.False(created);
                Assert.False(string.IsNullOrEmpty(key.PublicKeyBase64));
            }

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var key = WindowsMachineAuditKey.Open(policy, allowCreation: false, out var wasCreated);
                return (key.KeyId, key.PublicKeyBase64,
                    SignatureLength: key.Sign(Encoding.UTF8.GetBytes("concurrent")).Length, wasCreated);
            })));
            Assert.All(concurrent, result =>
            {
                Assert.False(result.wasCreated);
                Assert.Equal(concurrent[0].KeyId, result.KeyId);
                Assert.Equal(concurrent[0].PublicKeyBase64, result.PublicKeyBase64);
                Assert.Equal(64, result.SignatureLength);
            });

            var conflict = Assert.Throws<InvalidOperationException>(() =>
                WindowsMachineAuditKey.Open(policy, allowCreation: true, out _));
            Assert.Equal("AuditSigningKeyProvisioningConflict", conflict.Message);

            AddWorldReadAllowRule(keyPath);
            var aclFailure = Assert.Throws<InvalidOperationException>(() =>
                WindowsMachineAuditKey.Open(policy, allowCreation: false, out _));
            Assert.Equal("AuditSigningKeyAclInvalid", aclFailure.Message);

            File.Delete(keyPath);
            using (var key = WindowsMachineAuditKey.Open(policy, allowCreation: true, out var created))
                Assert.True(created);
            var corrupted = File.ReadAllBytes(keyPath);
            corrupted[0] ^= 0x01;
            File.WriteAllBytes(keyPath, corrupted);
            var corruptFailure = Assert.Throws<InvalidOperationException>(() =>
                WindowsMachineAuditKey.Open(policy, allowCreation: false, out _));
            Assert.Equal("AuditSigningKeyCorrupt", corruptFailure.Message);

            File.Delete(keyPath);
            File.WriteAllBytes(keyPath, new byte[] { 0x01, 0x02, 0x03 });
            var orphanFailure = Assert.Throws<InvalidOperationException>(() =>
                WindowsMachineAuditKey.Open(policy, allowCreation: true, out _));
            Assert.Equal("AuditSigningKeyProvisioningConflict", orphanFailure.Message);
        }
        finally
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
            if (Directory.Exists(keyDirectory)) Directory.Delete(keyDirectory);
        }
    }

    private static void AddWorldReadAllowRule(string path)
    {
        var security = new FileSecurity(path, AccessControlSections.Access);
        security.SetAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        if (!SetFileSecurity(path, SecurityInfos.DiscretionaryAcl, descriptor))
            throw new InvalidOperationException("AuditSigningKeyAclTestSetupFailed:" +
                Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurity(string fileName, SecurityInfos requestedInformation,
        byte[] securityDescriptor);

    private sealed class TestSigningKey : IAuditSigningKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public TestSigningKey()
        {
            var publicKey = _key.ExportSubjectPublicKeyInfo();
            PublicKeyBase64 = Convert.ToBase64String(publicKey);
            KeyId = Convert.ToHexString(SHA256.HashData(publicKey));
        }

        public string KeyId { get; }
        public string PublicKeyBase64 { get; }

        public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}

#pragma warning restore CA1416
