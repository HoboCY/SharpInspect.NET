using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V1 Evidence Capture Policy identity and exact deployment catalog resolution. These
/// tests are descriptive-contract only: they never claim that an image exists.
/// </summary>
public sealed class EvidenceCapturePolicyTests
{
    [Fact]
    [Trait("VerificationId", "V150_P01")]
    public void V150_P01_ModeDecisionMatrixCoversSuccessFailUnknownTimeoutAndNoFrame()
    {
        var none = new EvidenceCapturePolicySnapshot("Evidence.None", "1", EvidenceCaptureMode.None);
        var all = new EvidenceCapturePolicySnapshot("Evidence.All", "1", EvidenceCaptureMode.All);
        var failOrUnknown = new EvidenceCapturePolicySnapshot("Evidence.FailOrUnknown", "1",
            EvidenceCaptureMode.FailOrUnknown);
        var statuses = new[]
        {
            ExecutionStatus.Success, ExecutionStatus.Error, ExecutionStatus.Timeout, ExecutionStatus.Cancelled
        };
        var decisions = new[] { InspectionDecision.Pass, InspectionDecision.Fail, InspectionDecision.Unknown };
        foreach (var status in statuses)
        {
            foreach (var decision in decisions)
            {
                Assert.False(none.RequiresImage(true, status, decision));
                Assert.True(all.RequiresImage(true, status, decision));
                Assert.Equal(status != ExecutionStatus.Success || decision != InspectionDecision.Pass,
                    failOrUnknown.RequiresImage(true, status, decision));
                // A run that obtained no frame never claims an image obligation in any mode.
                Assert.False(none.RequiresImage(false, status, decision));
                Assert.False(all.RequiresImage(false, status, decision));
                Assert.False(failOrUnknown.RequiresImage(false, status, decision));
            }
        }
        Assert.False(failOrUnknown.RequiresImage(true, ExecutionStatus.Success, InspectionDecision.Pass));
        Assert.True(failOrUnknown.RequiresImage(true, ExecutionStatus.Success, InspectionDecision.Fail));
        Assert.True(failOrUnknown.RequiresImage(true, ExecutionStatus.Success, InspectionDecision.Unknown));
        Assert.True(failOrUnknown.RequiresImage(true, ExecutionStatus.Timeout, InspectionDecision.Pass));
        Assert.False(failOrUnknown.RequiresImage(false, ExecutionStatus.Timeout, InspectionDecision.Unknown));
        // Undefined enum values can never be interpreted as an image obligation.
        Assert.Throws<ArgumentException>(() =>
            failOrUnknown.RequiresImage(true, (ExecutionStatus)9, InspectionDecision.Pass));
        Assert.Throws<ArgumentException>(() =>
            failOrUnknown.RequiresImage(true, ExecutionStatus.Success, (InspectionDecision)9));

        // The exported identity binds the versioned domain, id, version and mode exactly.
        var same = new EvidenceCapturePolicySnapshot("Evidence.FailOrUnknown", "1", EvidenceCaptureMode.FailOrUnknown);
        Assert.Equal(failOrUnknown.ContentHash, same.ContentHash);
        Assert.Equal(64, failOrUnknown.ContentHash.Length);
        Assert.Equal(new RecipeContractReference("Evidence.FailOrUnknown", "1", failOrUnknown.ContentHash),
            failOrUnknown.Reference);
        Assert.NotEqual(failOrUnknown.ContentHash,
            new EvidenceCapturePolicySnapshot("Evidence.FailOrUnknown", "2", EvidenceCaptureMode.FailOrUnknown).ContentHash);
        Assert.NotEqual(failOrUnknown.ContentHash,
            new EvidenceCapturePolicySnapshot("Evidence.Other", "1", EvidenceCaptureMode.FailOrUnknown).ContentHash);
        Assert.NotEqual(failOrUnknown.ContentHash,
            new EvidenceCapturePolicySnapshot("Evidence.FailOrUnknown", "1", EvidenceCaptureMode.All).ContentHash);
        // Descriptive identity only: no drive, share, folder or timestamp can be expressed.
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCapturePolicySnapshot(@"C:\evidence\images", "1", EvidenceCaptureMode.All));
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCapturePolicySnapshot("Evidence.Production", @"\\station\share", EvidenceCaptureMode.All));
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCapturePolicySnapshot("Evidence.Production", "1", (EvidenceCaptureMode)9));
        Assert.Throws<ArgumentNullException>(() =>
            new EvidenceCapturePolicySnapshot(null!, "1", EvidenceCaptureMode.All));
        Assert.Throws<ArgumentNullException>(() =>
            new EvidenceCapturePolicySnapshot("Evidence.Production", null!, EvidenceCaptureMode.All));
        // The kind is appended so every existing ordinal and canonical payload stays frozen.
        Assert.Equal(0, (int)RecipePolicyKind.AlgorithmExecution);
        Assert.Equal(1, (int)RecipePolicyKind.ImageAcquisition);
        Assert.Equal(2, (int)RecipePolicyKind.RecipeGovernance);
        Assert.Equal(3, (int)RecipePolicyKind.CalibrationAcceptance);
        Assert.Equal(4, (int)RecipePolicyKind.EvidenceCapture);
    }

    [Fact]
    [Trait("VerificationId", "V150_P02")]
    public void V150_P02_CatalogIsAFrozenSortedCopyThatCallersCannotMutate()
    {
        var beta = new EvidenceCapturePolicySnapshot("Evidence.Beta", "1", EvidenceCaptureMode.All);
        var alphaVersionOne = new EvidenceCapturePolicySnapshot("Evidence.Alpha", "1",
            EvidenceCaptureMode.FailOrUnknown);
        var alphaVersionTwo = new EvidenceCapturePolicySnapshot("Evidence.Alpha", "2", EvidenceCaptureMode.None);
        var catalog = new EvidenceCapturePolicyCatalog(new[] { beta, alphaVersionTwo, alphaVersionOne });
        Assert.Equal(new[] { "Evidence.Alpha", "Evidence.Alpha", "Evidence.Beta" },
            catalog.Policies.Select(value => value.Id));
        Assert.Equal(new[] { "1", "2", "1" }, catalog.Policies.Select(value => value.Version));
        Assert.Equal(64, catalog.ContentHash.Length);
        // The exact identities, not the caller's enumeration order, bind the catalog hash.
        Assert.Equal(catalog.ContentHash,
            new EvidenceCapturePolicyCatalog(new[] { alphaVersionOne, beta, alphaVersionTwo }).ContentHash);
        Assert.NotEqual(catalog.ContentHash,
            new EvidenceCapturePolicyCatalog(new[] { beta, alphaVersionOne }).ContentHash);
        Assert.NotEqual(new EvidenceCapturePolicyCatalog(new[] { beta }).ContentHash,
            new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot("Evidence.Beta", "1", EvidenceCaptureMode.None)
            }).ContentHash);

        // The catalog owns a frozen copy of its inputs and exposes no mutation path.
        var source = new[] { beta };
        var frozen = new EvidenceCapturePolicyCatalog(source);
        source[0] = alphaVersionTwo;
        Assert.Equal(beta.Reference, frozen.Policies.Single().Reference);
        var exposed = Assert.IsAssignableFrom<IList<EvidenceCapturePolicySnapshot>>(catalog.Policies);
        Assert.Throws<NotSupportedException>(() => exposed[0] = beta);
        Assert.Throws<NotSupportedException>(exposed.Clear);

        var full = new EvidenceCapturePolicyCatalog(Enumerable.Range(0, 64).Select(index =>
            new EvidenceCapturePolicySnapshot("Evidence.P" + index.ToString("D2"), "1", EvidenceCaptureMode.All)));
        Assert.Equal(64, full.Policies.Count);
    }

    [Fact]
    [Trait("VerificationId", "V150_P03")]
    public void V150_P03_DuplicateIdentitiesAndMismatchedReferencesAreRejected()
    {
        var beta = new EvidenceCapturePolicySnapshot("Evidence.Beta", "1", EvidenceCaptureMode.All);
        var alphaVersionOne = new EvidenceCapturePolicySnapshot("Evidence.Alpha", "1",
            EvidenceCaptureMode.FailOrUnknown);
        var alphaVersionTwo = new EvidenceCapturePolicySnapshot("Evidence.Alpha", "2", EvidenceCaptureMode.None);
        var catalog = new EvidenceCapturePolicyCatalog(new[] { beta, alphaVersionTwo, alphaVersionOne });

        // Exact identity only: no name, latest-version or partial-hash fallback exists.
        Assert.Equal(beta, catalog.Resolve(beta.Reference));
        Assert.Equal(alphaVersionTwo, catalog.Resolve(
            new RecipeContractReference("Evidence.Alpha", "2", alphaVersionTwo.ContentHash)));
        Assert.Null(catalog.Resolve(new RecipeContractReference("Evidence.Alpha", "3", alphaVersionTwo.ContentHash)));
        Assert.Null(catalog.Resolve(new RecipeContractReference("Evidence.Alpha", "2", beta.ContentHash)));
        Assert.Null(catalog.Resolve(new RecipeContractReference("Evidence.Missing", "1", beta.ContentHash)));
        Assert.False(catalog.TryResolve(new RecipeContractReference("Evidence.Beta", "2", beta.ContentHash),
            out var unresolved));
        Assert.Null(unresolved);
        Assert.True(catalog.TryResolve(alphaVersionOne.Reference, out var resolved));
        Assert.Equal(alphaVersionOne, resolved);
        Assert.Throws<ArgumentNullException>(() => catalog.Resolve(null!));

        // Duplicate identities, conflicting hashes, the 64-policy bound and null input fail closed;
        // an identical duplicate is no more acceptable than a conflicting one.
        Assert.Throws<ArgumentException>(() => new EvidenceCapturePolicyCatalog(new[] { beta, beta }));
        Assert.Throws<ArgumentException>(() => new EvidenceCapturePolicyCatalog(new[]
        {
            beta, new EvidenceCapturePolicySnapshot("Evidence.Beta", "1", EvidenceCaptureMode.None)
        }));
        Assert.Throws<ArgumentException>(() => new EvidenceCapturePolicyCatalog(
            Enumerable.Range(0, 65).Select(index =>
                new EvidenceCapturePolicySnapshot("Evidence.P" + index.ToString("D2"), "1", EvidenceCaptureMode.All))));
        Assert.Throws<ArgumentException>(() => new EvidenceCapturePolicyCatalog(new[] { beta, null! }));
        Assert.Throws<ArgumentNullException>(() => new EvidenceCapturePolicyCatalog(null!));
    }
}
