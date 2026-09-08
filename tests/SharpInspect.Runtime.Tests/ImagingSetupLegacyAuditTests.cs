using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ImagingSetupLegacyAuditTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void V123_L01_PreStepUpIdentityFormatsRemainReadable(int schema)
    {
        var legacy = new IdentityAuditEvent(Guid.Parse("db7162a0-155b-4d92-ad31-70181380eb7f"),
            IdentityEventKind.IdentityConfigured, DateTimeOffset.UnixEpoch, "LegacyStation",
            null, null, null, null, "IdentityConfigured", AuthenticationPolicyId: "LegacyAuth",
            AuthenticationPolicyVersion: "1", AuthenticationPolicyHash: new string('A', 64));
        var payload = legacy.Encode(1, schema);
        Assert.Equal(0, IdentityAuditEvent.VerifyPayload(payload, 1, "LegacyStation", schema));
    }
}
