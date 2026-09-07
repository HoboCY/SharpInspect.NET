using System.Text.Json.Serialization;

namespace SharpInspect.Abstractions;

/// <summary>
/// A self-describing password verifier. The encoded salt and derived value are
/// deliberately omitted from text and default JSON representations.
/// </summary>
public sealed record PasswordHashRecord
{
    public PasswordHashRecord(
        string algorithm,
        int formatVersion,
        int parameterVersion,
        int cost,
        string saltBase64,
        string derivedBase64)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        FormatVersion = formatVersion;
        ParameterVersion = parameterVersion;
        Cost = cost;
        SaltBase64 = saltBase64 ?? throw new ArgumentNullException(nameof(saltBase64));
        DerivedBase64 = derivedBase64 ?? throw new ArgumentNullException(nameof(derivedBase64));
    }

    public string Algorithm { get; init; }
    public int FormatVersion { get; init; }
    public int ParameterVersion { get; init; }
    public int Cost { get; init; }

    /// <summary>Storage value for the random salt. It is not emitted by default JSON.</summary>
    [JsonIgnore]
    public string SaltBase64 { get; init; }

    /// <summary>Storage value for the derived verifier. It is not emitted by default JSON.</summary>
    [JsonIgnore]
    public string DerivedBase64 { get; init; }

    public override string ToString()
        => $"{nameof(PasswordHashRecord)}(Algorithm={Algorithm},FormatVersion={FormatVersion}," +
           $"ParameterVersion={ParameterVersion},Cost={Cost})";
}

/// <summary>Algorithm-agile password hashing boundary used by the local identity provider.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes an already NFC-normalized password.</summary>
    PasswordHashRecord Hash(string normalizedPassword);

    /// <summary>Verifies an already NFC-normalized password against a stored record.</summary>
    bool Verify(string normalizedPassword, PasswordHashRecord record);

    /// <summary>Reports whether a successfully verified record should be replaced.</summary>
    bool NeedsRehash(PasswordHashRecord record);
}
