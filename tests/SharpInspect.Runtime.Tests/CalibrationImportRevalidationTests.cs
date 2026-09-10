using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationImportRevalidationTests
{
    [Fact]
    public async Task V134_I01_LocalProcedureReadsRetainedPixelsAndReproducesCoefficientsUnderLocalPolicy()
    {
        var fixture = new RevalidationFixture();
        var actual = await fixture.RecomputeAsync();
        Assert.Empty(actual.Failures);
        Assert.All(actual.Sections, section => Assert.True(section.Passed));
        Assert.Equal(1, fixture.Procedure.Extractions);
        Assert.Equal(1, fixture.Procedure.Computations);
        Assert.Equal(fixture.Source.Evidence.Candidate!.Result.Coefficients.ContentHash, actual.Coefficients.ContentHash);
        Assert.NotEqual(fixture.Source.Evidence.Candidate.ContentHash, actual.Evidence.ContentHash);
        Assert.False(fixture.Candidate.CanPublish);
        Assert.False(fixture.Candidate.CanActivate);
    }

    [Theory]
    [InlineData("device", "CalibrationImportDeviceIdentityMismatch")]
    [InlineData("imaging", "CalibrationImportImagingRevisionMismatch")]
    [InlineData("requested", "CalibrationImportRequestedGeometryMismatch")]
    [InlineData("effective", "CalibrationImportEffectiveGeometryMismatch")]
    public async Task V134_I02_ExactCurrentCompatibilityIsCheckedBeforeAnyProcedureCall(string changed, string reason)
    {
        var fixture = new RevalidationFixture();
        var camera = fixture.Camera;
        var binding = camera.Binding!;
        if (changed == "device")
            binding = new CameraBindingRevision(binding.Position, binding.LogicalRole, binding.Revision, binding.OperationId,
                binding.PreviousRevisionHash, binding.RevisionHash,
                new CameraBindingTarget(binding.Target.Provider, "different-physical-camera"), binding.AuthorPrincipalId,
                binding.AuthorSessionId, binding.AuthorAuthorizationRevision, binding.ChangeReason, binding.RecordedAtUtc);
        var current = new CameraSetupSnapshot(camera.LogicalRole, binding, camera.Health,
            changed == "requested" ? fixture.Source.Evidence.Header.BaselineRequested : camera.Requested,
            changed == "effective" ? fixture.Source.Evidence.Header.BaselineEffective : camera.Effective);
        var imaging = changed == "imaging" ? new ImagingSetupRevisionReference(camera.LogicalRole, Guid.NewGuid(), 1,
            fixture.Source.Manifest.ImagingSetup.RevisionHash) : fixture.Source.Manifest.ImagingSetup;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Revalidator.RecomputeAsync(
            fixture.Source, fixture.Candidate, fixture.Requirement, fixture.Source.Policy, current, imaging,
            TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(reason, exception.Message);
        Assert.Equal(0, fixture.Procedure.Extractions);
        Assert.Equal(0, fixture.Procedure.Computations);
    }

    [Theory]
    [InlineData("receipt", "CalibrationImportLocalExtractionEvidenceInvalid")]
    [InlineData("computation", "CalibrationImportLocalComputationEvidenceMissing")]
    [InlineData("coefficients", "CalibrationImportCoefficientsNotReproduced")]
    public async Task V134_I03_MissingOrNonReproducibleLocalEvidenceCannotPass(string fault, string reason)
    {
        var fixture = new RevalidationFixture(fault);
        var actual = await fixture.RecomputeAsync();
        Assert.Contains(reason, actual.Failures);
    }

    [Fact]
    public async Task V134_I04_CallerCancellationDoesNotReleaseActualProcedureCapacityOrPixelLoan()
    {
        var fixture = new RevalidationFixture("blocked");
        using var caller = new CancellationTokenSource();
        var first = fixture.RecomputeAsync(caller.Token);
        await fixture.Procedure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.True(fixture.Procedure.Borrowed!.IsLoanActive);
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RecomputeAsync());
            Assert.Equal("CalibrationImportRecomputationBusy", busy.Message);
        }
        finally
        {
            fixture.Procedure.Release.TrySetResult(true);
        }
        await fixture.Procedure.Exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var attempt = 0; attempt < 100 && fixture.Procedure.Borrowed.IsLoanActive; attempt++) await Task.Delay(10);
        Assert.False(fixture.Procedure.Borrowed.IsLoanActive);
    }

    internal sealed class RevalidationFixture
    {
        internal RevalidationFixture(string? fault = null)
        {
            var export = CalibrationExportPackageTests.ExportFixture.Create();
            Package = CalibrationExportPackageCodec.Encode(export.StationId, export.Evidence, export.Policy, export.Images);
            Source = CalibrationExportPackageCodec.Decode(Package);
            var command = new ImportCalibrationPackageCommand(Guid.NewGuid(), Source.Evidence.Header.Command.Invocation,
                Package, "V134 local import");
            Actor = new CalibrationGovernanceActor(Guid.NewGuid(), Guid.NewGuid(), 1);
            Candidate = new ImportedCalibrationCandidate(1, command.CorrelationId, Actor, DateTimeOffset.UnixEpoch.AddHours(1),
                Guid.NewGuid(), Package.ContentHash, Package.Length, Source.Manifest.PackageId, Source.Manifest.SourceStationId,
                Source.Manifest.ContentHash, command.Reason, command.AuthorizationTarget);
            Camera = new CameraSetupSnapshot(Source.Evidence.Header.Binding.LogicalRole, Source.Evidence.Header.Binding,
                new CameraHealthSnapshot(CameraProviderAvailability.Available, CameraConnectionState.Open,
                    CameraConfigurationState.Applied, CameraAcquisitionState.Stopped, new(DateTimeOffset.UtcNow, 1)),
                Source.Evidence.TemporaryConfiguration!.Requested, Source.Evidence.TemporaryConfiguration.Effective);
            Procedure = new ImportProcedure(Source, fault);
            Revalidator = new CalibrationImportRevalidator(new CalibrationProcedureRegistry(new[] { Procedure }));
        }
        internal CalibrationExportPackage Package { get; }
        internal CalibrationExportPackageCodec.CalibrationExportPackageContents Source { get; }
        internal CalibrationRequirement Requirement => Source.Evidence.Header.Command.Plan.Requirement;
        internal CalibrationGovernanceActor Actor { get; }
        internal ImportedCalibrationCandidate Candidate { get; }
        internal CameraSetupSnapshot Camera { get; }
        internal ImportProcedure Procedure { get; }
        internal CalibrationImportRevalidator Revalidator { get; }
        internal Task<CalibrationImportRecomputation> RecomputeAsync(CancellationToken token = default) =>
            Revalidator.RecomputeAsync(Source, Candidate, Requirement, Source.Policy, Camera,
                Source.Manifest.ImagingSetup, TimeSpan.FromSeconds(3), token);
    }

    internal sealed class ImportProcedure : IRegisteredCalibrationProcedure
    {
        private readonly CalibrationExportPackageCodec.CalibrationExportPackageContents _source;
        private readonly string? _fault;
        internal ImportProcedure(CalibrationExportPackageCodec.CalibrationExportPackageContents source, string? fault)
        { _source = source; _fault = fault; }
        public CalibrationProcedureDescriptor Descriptor => _source.Evidence.Header.Command.Plan.Procedure;
        internal int Extractions;
        internal int Computations;
        internal VisionFrame? Borrowed;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ValidateInput(CalibrationProcedureInputPayload payload) =>
            Assert.Equal(_source.Evidence.Header.Command.Plan.Input.ContentHash, payload.ContentHash);
        public async ValueTask<CalibrationExtractionResult> ExtractAsync(CalibrationProcedureInputPayload payload,
            VisionFrame frame, Guid sessionId, Guid frameId, string sourceHash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Extractions);
            Borrowed = frame;
            Assert.True(frame.IsLoanActive);
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, frame.GetRowSpan(0).ToArray());
            Assert.Equal(_source.Images[0].Frame.SourceHash, sourceHash);
            Entered.TrySetResult(true);
            if (_fault == "blocked")
            {
                await Release.Task.ConfigureAwait(false);
                Assert.True(frame.IsLoanActive);
                Exited.TrySetResult(true);
            }
            return new CalibrationExtractionResult(new[] { new CalibrationImageFeature("corner", 1, 1) },
                Array.Empty<CalibrationProcedureDiagnostic>(), _fault == "receipt" ? null :
                    new CalibrationExtractionReceipt(_source.Policy.ExtractionReceiptContract, new byte[] { 1, 2, 3 }));
        }
        public ValueTask<CalibrationProcedureComputationResult> ComputeAsync(CalibrationProcedureInputPayload payload,
            IReadOnlyList<CalibrationObservationInput> observations, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Computations);
            Assert.Single(observations);
            var original = _source.Evidence.Candidate!.Result;
            return ValueTask.FromResult(new CalibrationProcedureComputationResult(_fault == "coefficients" ?
                new CalibrationCoefficientPayload(original.Coefficients.Format, new byte[] { 99 }) : original.Coefficients,
                original.QualityMetrics, original.Diagnostics, _fault == "computation" ? null : original.Evidence));
        }
    }
}
