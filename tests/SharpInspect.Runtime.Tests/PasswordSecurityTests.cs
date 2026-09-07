using System.Text;
using System.Text.Json;
using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PasswordSecurityTests
{
    [Fact]
    public void V104_C01_PasswordPolicyNormalizesNfcAndCountsUnicodeCodePoints()
    {
        var blocklist = PasswordBlocklist.Create("test", "2026-09", new[] { "known-compromised-value" });
        var policy = new LocalPasswordPolicy { Blocklist = blocklist };
        var decomposed = "e\u0301" + new string('a', 14);

        var normalized = policy.NormalizeAndValidate(decomposed);

        Assert.Equal("é" + new string('a', 14), normalized);
    }

    [Fact]
    public void V104_C02_PasswordPolicyRejectsInvalidSurrogatesAndWrongCodePointBoundsWithoutTrimming()
    {
        var policy = new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("test", "2026-09", new[] { "known-compromised-value" })
        };

        Assert.Throws<ArgumentException>(() => policy.NormalizeAndValidate("short"));
        Assert.Throws<ArgumentException>(() => policy.NormalizeAndValidate(new string('a', 129)));
        Assert.Throws<ArgumentException>(() => policy.NormalizeAndValidate(new string('\ud800', 15)));
        Assert.Equal(" " + new string('a', 14), policy.NormalizeAndValidate(" " + new string('a', 14)));
    }

    [Fact]
    public void V104_C03_PasswordPolicyRequiresTrustedBlocklistAndMatchesCompleteContextDerivatives()
    {
        var noBlocklist = new LocalPasswordPolicy();
        Assert.Throws<InvalidOperationException>(() => noBlocklist.NormalizeAndValidate(new string('a', 15)));
        Assert.Throws<ArgumentException>(() => PasswordBlocklist.Create("empty", "2026-09", Array.Empty<string>()));
        var tooLong = Assert.Throws<ArgumentException>(() => PasswordBlocklist.Create(
            "long", "2026-09", new[] { new string('x', 4_097) }));
        Assert.Contains("PasswordBlocklistValueInvalid", tooLong.Message, StringComparison.Ordinal);
        var tooLarge = Assert.Throws<ArgumentException>(() => PasswordBlocklist.Create(
            "large", "2026-09", OversizedBlocklistValues()));
        Assert.Contains("PasswordBlocklistContentTooLarge", tooLarge.Message, StringComparison.Ordinal);

        var blocklist = PasswordBlocklist.Create("deploy-1", "2026-09", new[] { "station-12345678" });
        var policy = new LocalPasswordPolicy { Blocklist = blocklist };
        Assert.Throws<InvalidOperationException>(() => policy.NormalizeAndValidate("station-12345678"));
        Assert.Equal("prefix-station-123-suffix", policy.NormalizeAndValidate("prefix-station-123-suffix"));

        var contextPolicy = new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("deploy-2", "2026-09", new[] { "known-compromised-value" })
        };
        Assert.Throws<InvalidOperationException>(() =>
            contextPolicy.NormalizeAndValidate("StationABCDEFGHI123", stationId: "StationABCDEFGHI"));
        Assert.Throws<InvalidOperationException>(() =>
            contextPolicy.NormalizeAndValidate("stationabcdefghi123", stationId: "StationABCDEFGHI"));
        Assert.Equal("prefixStation123", contextPolicy.NormalizeAndValidate(
            "prefixStation123", stationId: "StationABCDEFGHI"));
        const string mixedCasePassword = "MiXeD-pass-word!";
        Assert.Equal(mixedCasePassword, contextPolicy.NormalizeAndValidate(
            mixedCasePassword, stationId: "StationABCDEFGHI"));
    }

    [Fact]
    public void V104_C04_BlocklistContentHashMustMatchExactDeploymentValues()
    {
        var blocklist = PasswordBlocklist.Create("deploy", "v1", new[] { "alpha", "beta" });
        Assert.Equal("D7F62B7EB3A0C0AE7BB9D34C670A80461B705D94797ABDE782DDD2917489DCCB",
            blocklist.ContentHash);
        Assert.Equal(blocklist.ContentHash, PasswordBlocklist.ComputeContentHash(
            blocklist.Id, blocklist.Version, blocklist.Values));
        Assert.Throws<ArgumentException>(() =>
            (blocklist with { ContentHash = new string('0', 64) }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (blocklist with { Values = Array.Empty<string>() }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (blocklist with { Values = new[] { "changed" } }).Validate());
    }

    [Fact]
    public void V104_C05_PasswordHashRecordDoesNotRevealSecretMaterialInTextOrDefaultJson()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var record = hasher.Hash(new string('p', 15));
        var second = hasher.Hash(new string('p', 15));
        var text = record.ToString();
        var json = JsonSerializer.Serialize(record);

        Assert.NotEqual(record.SaltBase64, second.SaltBase64);
        Assert.DoesNotContain(record.SaltBase64, text, StringComparison.Ordinal);
        Assert.DoesNotContain(record.DerivedBase64, text, StringComparison.Ordinal);
        Assert.DoesNotContain(record.SaltBase64, json, StringComparison.Ordinal);
        Assert.DoesNotContain(record.DerivedBase64, json, StringComparison.Ordinal);
    }

    [Fact]
    public void V104_C06_Pbkdf2HashesVerifyWithConstantShapeRecordAndNeedRehashWhenBelowBaseline()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var password = new string('p', 15);
        var record = hasher.Hash(password);

        Assert.True(hasher.Verify(password, record));
        Assert.False(hasher.Verify(password + "x", record));
        Assert.True(Convert.FromBase64String(record.SaltBase64).Length >= 16);
        Assert.True(Convert.FromBase64String(record.DerivedBase64).Length >= 32);
        Assert.False(hasher.NeedsRehash(record));

        var historical = new PasswordHashRecord(
            record.Algorithm,
            record.FormatVersion,
            record.ParameterVersion,
            1,
            Convert.ToBase64String(Encoding.ASCII.GetBytes("0123456789abcdef")),
            Convert.ToBase64String(Convert.FromHexString(
                "b14286d992a227518013a10aae3e6d023f3a1edea1589411741755ec10d9bd1f")));
        Assert.True(hasher.Verify(password, historical));
        Assert.True(hasher.NeedsRehash(historical));
    }

    [Fact]
    public void V104_C07_Pbkdf2RejectsMalformedOrUnsupportedRecords()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var record = hasher.Hash(new string('p', 15));

        Assert.False(hasher.Verify(new string('p', 15), record with { Algorithm = "other" }));
        Assert.False(hasher.Verify(new string('p', 15), record with { DerivedBase64 = "%%%" }));
        Assert.True(hasher.NeedsRehash(record with { FormatVersion = 99 }));
        Assert.True(hasher.NeedsRehash(record with { Cost = 10_000_001 }));
    }

    [Fact]
    public void V104_C08_PasswordHashBaselineCannotBeLoweredBelowReleaseFloor()
    {
        var lower = new PasswordHashBaseline
        {
            MinimumIterations = PasswordHashBaseline.SecurityFloorIterations - 1
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => lower.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pbkdf2PasswordHasher(lower));
    }

    [Fact]
    public void V104_C09_Pbkdf2MatchesKnownVectorForHistoricalCost()
    {
        const string password = "password";
        var salt = Encoding.ASCII.GetBytes("0123456789abcdef");
        var expected = "ef9d5f6add4a5d19f4a7fc92b48f2351ea95bb977642c0071ed4e4010a42cb6c";
        var record = new PasswordHashRecord(
            Pbkdf2PasswordHasher.Algorithm,
            Pbkdf2PasswordHasher.FormatVersion,
            Pbkdf2PasswordHasher.ParameterVersion,
            1,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(Convert.FromHexString(expected)));

        Assert.True(new Pbkdf2PasswordHasher().Verify(password, record));
    }

    private static IEnumerable<string> OversizedBlocklistValues()
    {
        var suffix = new string('x', 4_090);
        for (var index = 0; index <= 16_384; index++)
            yield return index.ToString("D6", CultureInfo.InvariantCulture) + suffix;
    }
}
