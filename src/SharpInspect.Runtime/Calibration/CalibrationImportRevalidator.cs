using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Recompute untrusted portable evidence without creating or admitting a local camera session.</summary>
internal sealed class CalibrationImportRevalidator
{
    private readonly CalibrationProcedureRegistry _procedures;
    private int _running;
    internal CalibrationImportRevalidator(CalibrationProcedureRegistry procedures) => _procedures = procedures;

    internal async Task<CalibrationImportRecomputation> RecomputeAsync(
        CalibrationExportPackageCodec.CalibrationExportPackageContents source,
        ImportedCalibrationCandidate candidate, CalibrationRequirement requirement,
        CalibrationAcceptancePolicy policy, CameraSetupSnapshot camera, ImagingSetupRevisionReference imaging,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("CalibrationImportRecomputationBusy");
        var bounded = new CancellationTokenSource();
        bounded.CancelAfter(timeout);
        var cancellation = cancellationToken.Register(() => _ = Task.Run(() =>
        {
            try { bounded.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }));
        var actual = Task.Run(async () =>
        {
            var loans = new List<CalibrationBorrowedFrame>();
            try
            {
                var token = bounded.Token;
                token.ThrowIfCancellationRequested();
                ValidateCompatibility(source.Manifest, requirement, policy, camera, imaging);
                var evidence = source.Evidence;
                var plan = evidence.Header.Command.Plan;
                if (plan.Requirement.Kind != requirement.Kind || plan.Requirement.LogicalPurpose != requirement.LogicalPurpose ||
                    plan.Requirement.CoefficientContract != requirement.CoefficientContract)
                    throw new InvalidOperationException("CalibrationImportSourceRequirementMismatch");
                var procedure = _procedures.Resolve(plan.Procedure);
                procedure.ValidateInput(plan.Input);
                if (plan.Procedure.Procedure != policy.ProcedureContract || plan.Procedure.InputContract != policy.InputContract ||
                    plan.Input.InputContract != policy.InputContract)
                    throw new InvalidOperationException("CalibrationImportLocalProcedurePolicyMismatch");
                var observations = new List<CalibrationObservationEvidence>();
                var inputs = new List<CalibrationObservationInput>();
                var excluded = evidence.Exclusions.Select(value => value.FrameId).ToHashSet();
                foreach (var image in source.Images.OrderBy(value => value.Frame.FrameId))
                {
                    token.ThrowIfCancellationRequested();
                    var loan = new CalibrationBorrowedFrame(image);
                    loans.Add(loan);
                    var frame = image.Frame;
                    // The source IDs label the external image and extraction inputs. No local
                    // Session, admission, camera capture, or actor is synthesized from them.
                    var extracted = await procedure.ExtractAsync(plan.Input, loan, frame.SessionId,
                        frame.FrameId, frame.SourceHash, token).ConfigureAwait(false);
                    var observation = new CalibrationObservationEvidence(Guid.NewGuid(), frame,
                        plan.Procedure, plan.Input.ContentHash, extracted);
                    observations.Add(observation);
                    if (!excluded.Contains(frame.FrameId))
                        inputs.Add(new CalibrationObservationInput(loan, extracted.Features, frame.SessionId,
                            frame.FrameId, frame.SourceHash, extracted.Receipt));
                }
                var selection = CalibrationEvidenceSelection.Evaluate(evidence.Header, evidence.Frames,
                    observations, evidence.Exclusions);
                if (!selection.Sufficient) throw new InvalidOperationException(selection.ReasonCode);
                var computed = await procedure.ComputeAsync(plan.Input, inputs, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (computed.Coefficients.Format != requirement.CoefficientContract ||
                    computed.Coefficients.Format != policy.CoefficientContract)
                    throw new InvalidOperationException("CalibrationImportCoefficientContractMismatch");
                var failures = new List<string>();
                if (computed.Evidence is null || computed.Evidence.Format != policy.ComputationEvidenceContract)
                    failures.Add("CalibrationImportLocalComputationEvidenceMissing");
                var included = observations.Where(value => !excluded.Contains(value.Frame.FrameId)).ToArray();
                var invalidObservation = included.Any(value => value.Result.Receipt is null ||
                    value.Result.Receipt.Format != policy.ExtractionReceiptContract ||
                    value.Result.Features.Count < plan.SelectionPolicy.MinimumFeaturesPerFrame);
                if (invalidObservation) failures.Add("CalibrationImportLocalExtractionEvidenceInvalid");
                // A package is not an instruction to silently replace its coefficients.
                // The exact registered implementation must reproduce their canonical content.
                if (computed.Coefficients.ContentHash != evidence.Candidate?.Result.Coefficients.ContentHash)
                    failures.Add("CalibrationImportCoefficientsNotReproduced");
                var metrics = new[]
                {
                    Metric(CalibrationPolicyFactReference.IncludedFrameCount, selection.IncludedFrameCount),
                    Metric(CalibrationPolicyFactReference.SufficientFeatureFrameCount, selection.SufficientFeatureFrameCount),
                    Metric(CalibrationPolicyFactReference.SelectionImageCoverage, selection.ImageCoverage),
                    Metric(CalibrationPolicyFactReference.ExcludedFrameCount, excluded.Count)
                };
                var sections = policy.Sections.Select(section =>
                {
                    if (section.Applicability == CalibrationPolicyApplicability.NotApplicable)
                        return new CalibrationGateSectionResult(section.Category, section.Applicability,
                            Array.Empty<CalibrationMetricGateResult>(), section.NotApplicableReason);
                    var gates = section.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate,
                        IsSelectionFact(gate.Fact.Kind) ? metrics : computed.QualityMetrics)).ToArray();
                    if (section.Category == CalibrationAcceptanceGateCategory.InvalidObservation && invalidObservation)
                        gates = gates.Select(value => new CalibrationMetricGateResult(value.GateId, CalibrationGateOutcome.Failed,
                            value.ActualValue, value.ActualUnit, "CalibrationImportLocalExtractionEvidenceInvalid")).ToArray();
                    return new CalibrationGateSectionResult(section.Category, section.Applicability, gates, null);
                }).ToArray();
                var localProof = EncodeLocalEvidence(candidate, source, policy, requirement, camera, imaging,
                    observations, selection, computed);
                return new CalibrationImportRecomputation(candidate.Reference, requirement, camera.Binding!, imaging,
                    CalibrationFrameGeometry.FromRequested(camera.Requested!),
                    CalibrationFrameGeometry.FromEffective(camera.Effective!), plan.Procedure.Procedure,
                    computed.Coefficients, sections, failures, localProof);
            }
            finally
            {
                foreach (var loan in loans) loan.Dispose();
                cancellation.Dispose();
                bounded.Dispose();
                Interlocked.Exchange(ref _running, 0);
            }
        }, CancellationToken.None);
        // Caller cancellation cannot release image loans or the actual computation slot early.
        _ = actual.ContinueWith(value => { _ = value.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await actual.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateCompatibility(CalibrationExportManifest manifest, CalibrationRequirement requirement,
        CalibrationAcceptancePolicy policy, CameraSetupSnapshot camera, ImagingSetupRevisionReference imaging)
    {
        if (requirement.AcceptancePolicy != policy.Reference || requirement.Kind != policy.Kind ||
            requirement.LogicalPurpose != policy.LogicalPurpose || requirement.CoefficientContract != policy.CoefficientContract)
            throw new InvalidOperationException("CalibrationImportLocalPolicyRequirementMismatch");
        if (camera.Binding is null || camera.Binding.Target != manifest.Device ||
            camera.LogicalRole != requirement.LogicalCameraRole || manifest.ImagingSetup.LogicalCameraRole != requirement.LogicalCameraRole)
            throw new InvalidOperationException("CalibrationImportDeviceIdentityMismatch");
        if (imaging != manifest.ImagingSetup)
            throw new InvalidOperationException("CalibrationImportImagingRevisionMismatch");
        if (camera.Requested is null || CalibrationFrameGeometry.FromRequested(camera.Requested) != manifest.RequestedGeometry)
            throw new InvalidOperationException("CalibrationImportRequestedGeometryMismatch");
        if (camera.Effective is null || CalibrationFrameGeometry.FromEffective(camera.Effective) != manifest.EffectiveGeometry ||
            camera.Health.ProviderAvailability != CameraProviderAvailability.Available ||
            camera.Health.Connection != CameraConnectionState.Open || camera.Health.Configuration != CameraConfigurationState.Applied ||
            camera.Health.Acquisition != CameraAcquisitionState.Stopped)
            throw new InvalidOperationException("CalibrationImportEffectiveGeometryMismatch");
    }

    private static CalibrationQualityMetric Metric(CalibrationPolicyFactReference fact, double value) =>
        new(fact.Key, value, fact.Unit);
    private static bool IsSelectionFact(CalibrationPolicyFactKind kind) => kind is CalibrationPolicyFactKind.IncludedFrameCount or
        CalibrationPolicyFactKind.SufficientFeatureFrameCount or CalibrationPolicyFactKind.SelectionImageCoverage or
        CalibrationPolicyFactKind.ExcludedFrameCount;

    private static CalibrationImportComputationEvidence EncodeLocalEvidence(ImportedCalibrationCandidate candidate,
        CalibrationExportPackageCodec.CalibrationExportPackageContents source, CalibrationAcceptancePolicy policy,
        CalibrationRequirement requirement, CameraSetupSnapshot camera, ImagingSetupRevisionReference imaging,
        IReadOnlyList<CalibrationObservationEvidence> observations, CalibrationSelectionEvaluation selection,
        CalibrationProcedureComputationResult computed)
    {
        var document = new
        {
            Format = "sharpinspect-local-import-recomputation-v1",
            Candidate = candidate.Reference,
            candidate.PackageHash,
            SourceManifestHash = source.Manifest.ContentHash,
            LocalRequirementHash = requirement.ContentHash,
            LocalPolicy = policy.Reference,
            BindingHash = camera.Binding!.RevisionHash,
            ImagingSetup = imaging,
            Procedure = source.Evidence.Header.Command.Plan.Procedure,
            Input = new { source.Evidence.Header.Command.Plan.Input.ContentHash,
                Bytes = Convert.ToBase64String(source.Evidence.Header.Command.Plan.Input.GetBytes()) },
            Observations = observations.OrderBy(value => value.Frame.FrameId).Select(value => new
            {
                value.Frame.FrameId, value.Frame.SourceHash, value.ObservationId, value.InputHash,
                value.Result.Features, value.Result.Diagnostics,
                Receipt = value.Result.Receipt is null ? null : new
                {
                    value.Result.Receipt.Format, value.Result.Receipt.ContentHash,
                    Bytes = Convert.ToBase64String(value.Result.Receipt.GetBytes())
                }
            }).ToArray(),
            Selection = selection,
            Coefficients = new { computed.Coefficients.Format, computed.Coefficients.ContentHash,
                Bytes = Convert.ToBase64String(computed.Coefficients.GetBytes()) },
            computed.QualityMetrics,
            computed.Diagnostics,
            Evidence = computed.Evidence is null ? null : new
            {
                computed.Evidence.Format, computed.Evidence.ContentHash,
                Bytes = Convert.ToBase64String(computed.Evidence.GetBytes())
            }
        };
        return new CalibrationImportComputationEvidence(JsonSerializer.SerializeToUtf8Bytes(document));
    }
}

internal sealed record CalibrationImportRecomputation(ImportedCalibrationCandidateReference Candidate,
    CalibrationRequirement Requirement, CameraBindingRevision Binding, ImagingSetupRevisionReference ImagingSetup,
    CalibrationFrameGeometry RequestedGeometry, CalibrationFrameGeometry EffectiveGeometry,
    RecipeContractReference Procedure, CalibrationCoefficientPayload Coefficients,
    IReadOnlyList<CalibrationGateSectionResult> Sections, IReadOnlyList<string> Failures,
    CalibrationImportComputationEvidence Evidence);
