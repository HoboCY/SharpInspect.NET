using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationImportActivationCatalogTests
{
    [Fact]
    public void V134_A01_SelectedImportedProfileResolvesExactLocalLineage()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var catalog = CalibrationImportActivationCatalog.Create(fixture.Governance,
            fixture.Records, null, new[] { fixture.Publication.Reference },
            fixture.PolicyRevision.Position, fixture.PolicyRevision.ContentHash,
            fixture.Publication.Position, fixture.Publication.ContentHash);

        Assert.True(catalog.Available);
        Assert.True(catalog.TryGetImportedProfile(fixture.Publication.Reference, out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal(fixture.Publication.Reference, resolution!.Profile.Reference);
        Assert.Equal(fixture.Candidate.Reference, resolution.Candidate.Reference);
        Assert.Equal(fixture.Evaluation.Reference, resolution.Evaluation.Reference);
        Assert.Equal(fixture.Physical.Reference, resolution.PhysicalVerification!.Reference);
        Assert.Equal(fixture.Publication.Candidate, resolution.Profile.Candidate);
        Assert.Equal(fixture.Publication.Evaluation, resolution.Profile.Evaluation);
        Assert.Equal(fixture.Publication.PhysicalVerification, resolution.Profile.PhysicalVerification);
        Assert.True(catalog.TailsMatch(fixture.PolicyRevision.Position, fixture.PolicyRevision.ContentHash,
            fixture.Publication.Position, fixture.Publication.ContentHash));
        Assert.False(catalog.TailsMatch(fixture.PolicyRevision.Position + 1, fixture.PolicyRevision.ContentHash,
            fixture.Publication.Position, fixture.Publication.ContentHash));
    }

    [Fact]
    public void V134_A02_SelectionDoesNotFallbackToAnotherImportedIdentity()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var otherIdentity = new CalibrationProfileReference(fixture.Publication.ProfileId,
            fixture.Publication.Version, CalibrationImportTestDataFactory.HashD);
        var catalog = CalibrationImportActivationCatalog.Create(fixture.Governance,
            fixture.Records, null, new[] { otherIdentity }, 1, fixture.PolicyRevision.ContentHash,
            fixture.Publication.Position, fixture.Publication.ContentHash);

        Assert.True(catalog.Available);
        Assert.False(catalog.TryGetImportedProfile(otherIdentity, out _));
        Assert.Empty(catalog.ImportedProfiles);
    }

    [Fact]
    public void V134_A03_DuplicatePublicationIsRejectedBeforeSelection()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var records = fixture.Records.Concat(new CalibrationImportRecord[] { fixture.Publication }).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            CalibrationImportActivationCatalog.Create(fixture.Governance, records, null,
                new[] { fixture.Publication.Reference }, 1, fixture.PolicyRevision.ContentHash,
                fixture.Publication.Position, fixture.Publication.ContentHash));

        Assert.Equal("CalibrationImportActivationProfileDuplicate", error.Message);
    }

    [Fact]
    public void V134_A04_ImportedPublicationRetainsDevelopmentOnlyBoundary()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var catalog = CalibrationImportActivationCatalog.Create(fixture.Governance,
            fixture.Records, null, new[] { fixture.Publication.Reference }, 1,
            fixture.PolicyRevision.ContentHash, fixture.Publication.Position,
            fixture.Publication.ContentHash);

        Assert.True(catalog.TryGetImportedProfile(fixture.Publication.Reference, out var resolution));
        Assert.NotNull(resolution);
        Assert.True(resolution!.Profile.DevelopmentOnly);
        Assert.False(resolution.Profile.ProductionAuthority);
        Assert.False(resolution.Profile.CanActivate);
    }
}
