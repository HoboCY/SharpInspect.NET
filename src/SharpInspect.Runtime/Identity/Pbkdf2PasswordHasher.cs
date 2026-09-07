using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Identity;

/// <summary>PBKDF2-HMAC-SHA256 implementation for the V1 local identity provider.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const string Algorithm = "PBKDF2-HMAC-SHA256";
    public const int FormatVersion = 1;
    public const int ParameterVersion = PasswordHashBaseline.CurrentParameterVersion;
    private const int MaximumEncodedMaterialLength = 1_368;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly PasswordHashBaseline _baseline;

    public Pbkdf2PasswordHasher(PasswordHashBaseline? baseline = null)
    {
        _baseline = baseline ?? PasswordHashBaseline.Current;
        _baseline.Validate();
    }

    public PasswordHashBaseline Baseline => _baseline;

    public PasswordHashRecord Hash(string normalizedPassword)
    {
        var password = LocalPasswordPolicy.ValidateNormalizedPassword(normalizedPassword);
        var salt = RandomNumberGenerator.GetBytes(_baseline.SaltBytes);
        var passwordBytes = StrictUtf8.GetBytes(password);
        try
        {
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                _baseline.TargetIterations,
                HashAlgorithmName.SHA256,
                _baseline.DerivedBytes);
            try
            {
                return new PasswordHashRecord(
                    Algorithm,
                    FormatVersion,
                    _baseline.ParameterVersion,
                    _baseline.TargetIterations,
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(derived));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public bool Verify(string normalizedPassword, PasswordHashRecord record)
    {
        if (normalizedPassword is null || record is null) return false;

        byte[]? passwordBytes = null;
        byte[]? salt = null;
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            var password = LocalPasswordPolicy.ValidateNormalizedPassword(normalizedPassword);
            if (!TryDecodeRecord(record, out salt, out expected) || salt is null || expected is null)
                return false;

            passwordBytes = StrictUtf8.GetBytes(password);
            actual = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                record.Cost,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    public bool NeedsRehash(PasswordHashRecord record)
    {
        if (record is null || !IsRecordShapeSupported(record, out var saltLength, out var derivedLength))
            return true;

        return record.ParameterVersion != _baseline.ParameterVersion ||
            record.Cost < _baseline.TargetIterations ||
            saltLength < _baseline.SaltBytes ||
            derivedLength < _baseline.DerivedBytes;
    }

    private bool TryDecodeRecord(PasswordHashRecord record, out byte[]? salt, out byte[]? expected)
    {
        salt = null;
        expected = null;
        if (!IsRecordShapeSupported(record, out _, out _)) return false;

        try
        {
            salt = DecodeCanonicalBase64(record.SaltBase64);
            expected = DecodeCanonicalBase64(record.DerivedBase64);
            if (salt.Length < PasswordHashBaseline.MinimumSaltBytes || salt.Length > 1024 ||
                expected.Length < PasswordHashBaseline.MinimumDerivedBytes || expected.Length > 1024)
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expected);
                salt = null;
                expected = null;
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            salt = null;
            expected = null;
            return false;
        }
    }

    private bool IsRecordShapeSupported(
        PasswordHashRecord record,
        out int saltLength,
        out int derivedLength)
    {
        saltLength = 0;
        derivedLength = 0;
        if (!string.Equals(record.Algorithm, Algorithm, StringComparison.Ordinal) ||
            record.FormatVersion != FormatVersion ||
            record.ParameterVersion != _baseline.ParameterVersion ||
            record.Cost < 1 || record.Cost > _baseline.MaximumIterations)
            return false;

        try
        {
            var salt = DecodeCanonicalBase64(record.SaltBase64);
            var derived = DecodeCanonicalBase64(record.DerivedBase64);
            saltLength = salt.Length;
            derivedLength = derived.Length;
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(derived);
            return saltLength >= PasswordHashBaseline.MinimumSaltBytes && saltLength <= 1024 &&
                derivedLength >= PasswordHashBaseline.MinimumDerivedBytes && derivedLength <= 1024;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DecodeCanonicalBase64(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new FormatException("PasswordHashBase64Empty");
        if (value.Length > MaximumEncodedMaterialLength)
            throw new FormatException("PasswordHashBase64Oversize");
        var decoded = Convert.FromBase64String(value);
        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("PasswordHashBase64NonCanonical");
        }
        return decoded;
    }
}
