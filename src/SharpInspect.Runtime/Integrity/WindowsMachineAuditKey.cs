using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Globalization;
using SharpInspect.Runtime.Storage;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// A Runtime-owned ECDSA P-256 key protected by Windows DPAPI LocalMachine and
/// a restricted local ACL. The private key is only exported to memory while a
/// new protected file is being created; no production export or delete API is
/// provided by this type.
/// </summary>
internal sealed class WindowsMachineAuditKey : IAuditSigningKey
{
    private const string MissingReason = "AuditSigningKeyMissing";
    private const string InvalidReason = "AuditSigningKeyInvalid";
    private const string OpenReason = "AuditSigningKeyOpenFailed";
    private const string CreateReason = "AuditSigningKeyCreateFailed";
    private const string ProvisioningConflictReason = "AuditSigningKeyProvisioningConflict";
    private const string CorruptReason = "AuditSigningKeyCorrupt";
    private const string AclReason = "AuditSigningKeyAclInvalid";
    private const string PathReason = "AuditSigningKeyPathInvalid";
    private const string PlatformReason = "AuditSigningKeyWindowsRequired";

    private static readonly byte[] FileMagic = { (byte)'S', (byte)'A', (byte)'K', (byte)'1' };
    private const int FileVersion = 1;
    private const int HeaderLength = 12;
    private const int MaximumProtectedBlobLength = 16 * 1024;
    private const string DpapiDomain = "DPAPI-LocalMachine";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly ECDsa _signer;
    private int _disposed;

    private WindowsMachineAuditKey(ECDsa signer, string keyId, string publicKeyBase64)
    {
        _signer = signer;
        KeyId = keyId;
        PublicKeyBase64 = publicKeyBase64;
    }

    internal string KeyId { get; }
    internal string PublicKeyBase64 { get; }

    string IAuditSigningKey.KeyId => KeyId;
    string IAuditSigningKey.PublicKeyBase64 => PublicKeyBase64;

    internal static string GetKeyPath(AuditIntegrityPolicy policy)
    {
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));
        policy.Validate();

        var keyDirectory = policy.GetAbsoluteKeyDirectory();
        var nameBytes = new UTF8Encoding(false, true).GetBytes(policy.SigningKeyName);
        var fileName = Convert.ToHexString(SHA256.HashData(nameBytes)) + ".key";
        CryptographicOperations.ZeroMemory(nameBytes);
        return Path.Combine(keyDirectory, fileName);
    }

    internal static WindowsMachineAuditKey Open(AuditIntegrityPolicy policy, bool allowCreation,
        out bool created)
    {
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));
        policy.Validate();
        created = false;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(PlatformReason);

        string keyPath;
        try
        {
            keyPath = GetKeyPath(policy);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(PathReason);
        }

        var directory = Path.GetDirectoryName(keyPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException(PathReason);

        try
        {
            // 已有密钥只能按原身份打开；创建模式遇到旧文件必须报冲突，不能重生成密钥掩盖历史链失配。
            if (File.Exists(keyPath))
            {
                if (allowCreation)
                    throw new InvalidOperationException(ProvisioningConflictReason);
                ValidateStorageAndAcl(keyPath, directory);
                return OpenExisting(policy, keyPath);
            }

            // 密钥丢失时禁止自动自愈，只有明确允许的初始建库路径可以创建新的签名身份。
            if (!allowCreation || !policy.AllowInitialKeyCreation)
                throw new InvalidOperationException(MissingReason);

            EnsureNewDirectory(directory);
            ValidateStoragePath(keyPath);
            if (File.Exists(keyPath))
                throw new InvalidOperationException(ProvisioningConflictReason);

            return CreateNew(policy, keyPath, directory, out created);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(OpenReason);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(OpenReason);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(CorruptReason);
        }
    }

    public byte[] Sign(byte[] data)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        ThrowIfDisposed();
        try
        {
            return _signer.SignData(data, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("AuditSigningKeySignFailed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _signer.Dispose();
    }

    private static WindowsMachineAuditKey OpenExisting(AuditIntegrityPolicy policy, string keyPath)
    {
        var protectedBlob = ReadContainer(keyPath);
        var entropy = CreateEntropy(policy);
        byte[]? privateBytes = null;
        try
        {
            privateBytes = ProtectedData.Unprotect(protectedBlob, entropy, DataProtectionScope.LocalMachine);
            if (privateBytes.Length == 0 || privateBytes.Length > MaximumProtectedBlobLength)
                throw new InvalidOperationException(CorruptReason);

            var signer = ECDsa.Create();
            try
            {
                signer.ImportPkcs8PrivateKey(privateBytes, out var bytesRead);
                if (bytesRead != privateBytes.Length || !IsP256(signer))
                    throw new InvalidOperationException(InvalidReason);
                return CreateInstance(signer);
            }
            catch
            {
                signer.Dispose();
                throw;
            }
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(CorruptReason);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            if (privateBytes is not null)
                CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(protectedBlob);
        }
    }

    private static WindowsMachineAuditKey CreateNew(AuditIntegrityPolicy policy, string keyPath,
        string directory, out bool created)
    {
        created = false;
        ECDsa? signer = null;
        byte[]? privateBytes = null;
        byte[]? entropy = null;
        byte[]? protectedBlob = null;
        try
        {
            signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (!IsP256(signer))
                throw new InvalidOperationException(InvalidReason);

            privateBytes = signer.ExportPkcs8PrivateKey();
            if (privateBytes.Length == 0 || privateBytes.Length > MaximumProtectedBlobLength)
                throw new InvalidOperationException(InvalidReason);

            entropy = CreateEntropy(policy);
            protectedBlob = ProtectedData.Protect(privateBytes, entropy, DataProtectionScope.LocalMachine);
            if (protectedBlob.Length == 0 || protectedBlob.Length > MaximumProtectedBlobLength)
                throw new InvalidOperationException(CreateReason);

            WriteContainerCreateNew(keyPath, protectedBlob);
            ValidateStorageAndAcl(keyPath, directory);
            created = true;

            var result = CreateInstance(signer);
            signer = null;
            return result;
        }
        catch (IOException)
        {
            if (File.Exists(keyPath))
                throw new InvalidOperationException(ProvisioningConflictReason);
            throw new InvalidOperationException(CreateReason);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(CreateReason);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(CreateReason);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(CreateReason);
        }
        finally
        {
            if (signer is not null)
                signer.Dispose();
            if (privateBytes is not null)
                CryptographicOperations.ZeroMemory(privateBytes);
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
            if (protectedBlob is not null)
                CryptographicOperations.ZeroMemory(protectedBlob);
        }
    }

    private static WindowsMachineAuditKey CreateInstance(ECDsa signer)
    {
        try
        {
            var publicKey = signer.ExportSubjectPublicKeyInfo();
            var publicKeyBase64 = Convert.ToBase64String(publicKey);
            var keyId = Convert.ToHexString(SHA256.HashData(publicKey));
            return new WindowsMachineAuditKey(signer, keyId, publicKeyBase64);
        }
        catch
        {
            signer.Dispose();
            throw new InvalidOperationException(InvalidReason);
        }
    }

    private static byte[] CreateEntropy(AuditIntegrityPolicy policy) =>
        AuditCanonical.Encode("audit-key-dpapi-entropy", DpapiDomain, policy.StationId,
            policy.SigningKeyName, AuditCanonical.CanonicalizationVersion.ToString(CultureInfo.InvariantCulture));

    private static byte[] ReadContainer(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length < HeaderLength ||
            fileInfo.Length > HeaderLength + MaximumProtectedBlobLength)
            throw new InvalidOperationException(CorruptReason);

        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            if (stream.Length < HeaderLength || stream.Length > HeaderLength + MaximumProtectedBlobLength)
                throw new InvalidOperationException(CorruptReason);
            bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new InvalidOperationException(CorruptReason);
                offset += read;
            }
        }
        catch (IOException)
        {
            throw new InvalidOperationException(OpenReason);
        }

        if (!bytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4)) != FileVersion)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException(CorruptReason);
        }

        var blobLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4));
        if (blobLength <= 0 || blobLength > MaximumProtectedBlobLength ||
            blobLength != bytes.Length - HeaderLength)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException(CorruptReason);
        }

        var result = bytes.AsSpan(HeaderLength, blobLength).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    private static void WriteContainerCreateNew(string path, byte[] protectedBlob)
    {
        var bytes = new byte[HeaderLength + protectedBlob.Length];
        FileMagic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), FileVersion);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), protectedBlob.Length);
        protectedBlob.CopyTo(bytes, HeaderLength);

        try
        {
            using var stream = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew,
                FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough,
                CreateRestrictedFileSecurity());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateStorageAndAcl(string keyPath, string directory)
    {
        ValidateStoragePath(keyPath);
        if (!ValidateRestrictedAcl(directory, isDirectory: true) ||
            !ValidateRestrictedAcl(keyPath, isDirectory: false))
            throw new InvalidOperationException(AclReason);
    }

    private static void ValidateStoragePath(string keyPath)
    {
        var options = new ProductionStoreOptions(keyPath);
        if (!StoragePathValidator.TryValidate(options, out _, out _))
            throw new InvalidOperationException(PathReason);
    }

    private static void EnsureNewDirectory(string directory)
    {
        ValidateExistingAncestors(directory);
        var existed = Directory.Exists(directory);
        try
        {
            if (!existed)
            {
                FileSystemAclExtensions.Create(new DirectoryInfo(directory),
                    CreateRestrictedDirectorySecurity());
            }
            if (!existed)
                return;
            else if (!ValidateRestrictedAcl(directory, isDirectory: true))
                throw new InvalidOperationException(AclReason);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(CreateReason);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(CreateReason);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(CreateReason);
        }
    }

    private static DirectorySecurity CreateRestrictedDirectorySecurity()
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is null)
            throw new InvalidOperationException(AclReason);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SetFullControl(security, current);
        SetFullControl(security, system);
        SetFullControl(security, administrators);
        return security;
    }

    private static void SetFullControl(FileSystemSecurity security, SecurityIdentifier sid) =>
        security.SetAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            AccessControlType.Allow));

    private static bool ValidateRestrictedAcl(string path, bool isDirectory)
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is null)
            return false;

        var allowed = new HashSet<SecurityIdentifier>
        {
            current,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var seen = new HashSet<SecurityIdentifier>();
        try
        {
            AuthorizationRuleCollection rules;
            if (isDirectory)
            {
                var security = new DirectorySecurity(path, AccessControlSections.Access);
                if (!security.AreAccessRulesProtected)
                    return false;
                rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
                    targetType: typeof(SecurityIdentifier));
            }
            else
            {
                var security = new FileSecurity(path, AccessControlSections.Access);
                if (!security.AreAccessRulesProtected)
                    return false;
                rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
                    targetType: typeof(SecurityIdentifier));
            }

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Deny)
                    return false;
                if (rule.AccessControlType != AccessControlType.Allow ||
                    rule.IdentityReference is not SecurityIdentifier sid || !allowed.Contains(sid) ||
                    (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                    return false;
                seen.Add(sid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            return false;
        }

        return allowed.All(seen.Contains);
    }

    private static FileSecurity CreateRestrictedFileSecurity()
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is null)
            throw new InvalidOperationException(AclReason);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SetFullControl(security, current);
        SetFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        SetFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    private static void ValidateExistingAncestors(string directory)
    {
        var existing = directory;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(PathReason);
            existing = parent;
        }

        var probePath = Path.Combine(existing, ".sharpinspect-audit-key-path-check");
        ValidateStoragePath(probePath);
    }

    private static bool IsP256(ECDsa key)
    {
        try
        {
            var parameters = key.ExportParameters(includePrivateParameters: false);
            return key.KeySize == 256 && string.Equals(parameters.Curve.Oid.Value, P256Oid,
                StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsMachineAuditKey));
    }
}

#pragma warning restore CA1416
