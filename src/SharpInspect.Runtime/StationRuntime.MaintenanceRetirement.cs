using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Performance;

namespace SharpInspect.Runtime;

/// <summary>
/// T59 maintenance physical-retirement slice.  It owns exactly two things:
/// a snapshot of every physical owner that exists while the maintenance inhibit
/// is latched, and an observation task that only completes after those owners
/// actually retired.  Ordinary <see cref="StationRuntime.DisposeAsync"/> keeps
/// its existing bounded behaviour and never waits on this witness.
/// </summary>
public sealed partial class StationRuntime
{
    internal const string MaintenancePhysicalRetirementFaultedReason = "MaintenancePhysicalRetirementFaulted";
    internal const string MaintenancePhysicalRetirementRetainedReason = "MaintenancePhysicalRetirementRetained";
    internal const string MaintenancePhysicalRetirementUnverifiedReason = "MaintenancePhysicalRetirementUnverified";

    private MaintenanceRetirementWitness? _maintenanceRetirementWitness;
    private int _maintenancePhysicalRetirementCompleted;

    /// <summary>
    /// Latched only after <see cref="MaintenanceRetirementWitness.WaitAsync"/> observed every
    /// captured and late physical owner retired.  Root maintenance admission must keep new
    /// commands, production work and engineering work closed once this is set.
    /// </summary>
    internal bool MaintenancePhysicalRetirementCompleted =>
        Volatile.Read(ref _maintenancePhysicalRetirementCompleted) != 0;

    /// <summary>Test-only seam for the internal test assembly; identical lock contract as root.</summary>
    internal MaintenanceRetirementWitness CaptureMaintenanceRetirementForTesting()
    {
        lock (_sync) return CaptureMaintenanceRetirementLocked();
    }

    /// <summary>
    /// Root-only API.  Must be called under <c>_sync</c> after the maintenance inhibit closed
    /// new command/production/engineering admission and before any graceful stop or dispose.
    /// This is a snapshot: it starts no cancellation, disposal or new work.
    /// </summary>
    private MaintenanceRetirementWitness CaptureMaintenanceRetirementLocked()
    {
        if (_maintenanceRetirementWitness is { } captured) return captured;

        var physical = new List<Task>();
        var completion = new List<Task>();
        var leases = new List<RecipeActivationCameraLease>();
        void TakePhysical(Task? task) { if (task is not null) physical.Add(task); }
        void TakeCompletion(Task? task) { if (task is not null) completion.Add(task); }
        void TakeLease(RecipeActivationCameraLease? lease) { if (lease is not null) leases.Add(lease); }

        // Production and the accepted-cycle retirement signal.
        TakePhysical(_productionInspectionTask);
        TakePhysical(_productionInspectionOwner?.CycleRetired?.Task);
        TakePhysical(_productionInspectionOwner?.PartIdentityOperation);
        TakePhysical(_productionInspectionOwner?.ExecutionCancellationTask);

        // Preview / manual / qualification owners retire their own camera and algorithm leases.
        TakePhysical(_previewOwner?.Retired.Task);
        TakePhysical(_previewOwner?.Operation);
        TakePhysical(_previewOwner?.ExitWorker);
        TakePhysical(_previewOwner?.Reader);
        TakePhysical(_manualOwner?.Retired.Task);
        TakePhysical(_manualOwner?.Operation);
        TakePhysical(_manualOwner?.ExitWorker);
        TakePhysical(_manualOwner?.PreparationRetirement);
        TakePhysical(_stationQualificationOwner?.Retired.Task);
        TakePhysical(_stationQualificationOwner?.Operation);
        TakePhysical(_stationQualificationOwner?.PreparationRetirement);
        TakePhysical(_activationReservation?.Completion.Task);
        TakePhysical(_recipeRetirementCleanup);
        TakePhysical(_importPhysicalReservation?.Retirement);
        TakePhysical(_productionRecoveryOwner?.Retired.Task);
        TakePhysical(_calibrationWork);

        // Physical file / delivery workers.  Their Completion tasks retain all provider and
        // file handles even when an ordinary bounded shutdown already returned.
        TakePhysical(_storageCapacity?.PhysicalCompletion);
        TakePhysical(_retentionWorker?.Completion);
        TakePhysical(_evidenceReconciliationWorker?.Completion);
        TakePhysical(_outboxWorker?.Completion);
        TakePhysical(_imageFinalizationWorker?.Completion);

        // Elevated diagnostics support operation and bundle delivery.
        TakePhysical(_diagnosticSupportOperation?.Physical);
        TakePhysical(_diagnosticSupportOperation?.Retired.Task);
        TakePhysical(_diagnosticDeliveryRetired?.Task);

        // Camera leases held directly by owners.
        TakeLease(_previewOwner?.Camera);
        TakeLease(_manualOwner?.Camera);
        TakeLease(_stationQualificationOwner?.Camera);
        TakeLease(_productionInspectionOwner?.Camera);
        TakeLease(_importPhysicalReservation?._camera);

        // Non-physical schedulers still own a bounded physical turn each; their termination
        // is observed, but a fault here does not prove a retained device and is only recorded.
        TakeCompletion(_heartbeat);
        TakeCompletion(_storeInitialization);
        TakeCompletion(_completion);
        TakeCompletion(_alarmMaintenance);
        TakeCompletion(_algorithmHungObservation);
        TakeCompletion(_cameraAcquisitionObservation);
        TakeCompletion(_cameraRecoveryObservation);
        TakeCompletion(_recipeActivationStartupTask);
        TakeCompletion(_previewStartupTask);
        TakeCompletion(_manualStartupTask);
        TakeCompletion(_stationQualificationStartupTask);
        TakeCompletion(_recipeSelectionStartup);

        var witness = new MaintenanceRetirementWitness(this, physical.ToArray(), completion.ToArray(),
            leases.ToArray(), _activeActivation?.Algorithm, _productionImageStager, _diagnostics,
            _performance, _cameraSetupRuntime, _cameraAcquisitionService, _cameraRecoveryService,
            _manualOwner, _stationQualificationOwner, _previewOwner, _productionInspectionOwner,
            _productionRecoveryOwner, _importPhysicalReservation, _calibrationWork, _recipeRetirementCleanup);
        _maintenanceRetirementWitness = witness;
        return witness;
    }

    /// <summary>
    /// Bounded physical-retirement observation.  It never claims success because a dispose call
    /// returned, a projection stopped, or a task faulted/canceled; those cases fail closed with a
    /// fixed reason.  The wait itself is bounded by the supplied token only.
    /// </summary>
    internal sealed class MaintenanceRetirementWitness
    {
        private readonly StationRuntime _owner;
        private readonly Task[] _physicalTasks;
        private readonly Task[] _completionTasks;
        private readonly RecipeActivationCameraLease[] _initialLeases;
        private readonly PreparedAlgorithm? _activeAlgorithm;
        private readonly ProductionImageStager? _imageStager;
        private readonly RuntimeDiagnosticService? _diagnostics;
        private readonly RuntimePerformanceMonitor? _performance;
        private readonly CameraSetupRuntime _cameraSetup;
        private readonly CameraAcquisitionService? _cameraAcquisition;
        private readonly CameraRecoveryService? _cameraRecovery;
        private readonly ManualInspectionOwner? _manualOwner;
        private readonly StationQualificationOwner? _qualificationOwner;
        private readonly PreviewSessionOwner? _previewOwner;
        private readonly ProductionInspectionOwner? _productionOwner;
        private readonly ProductionRecoveryOwner? _recoveryOwner;
        private readonly ImportPhysicalReservation? _importPhysical;
        private readonly Task? _capturedCalibrationWork;
        private readonly Task? _capturedRecipeRetirementCleanup;

        private readonly object _sync = new();
        private readonly HashSet<RecipeActivationCameraLease> _observedLeases = new();
        private readonly List<Task<bool>> _leaseObservations = new();
        private readonly List<string> _observations = new();
        private Task<bool>? _acquisitionRetirement;
        private Task<bool>? _recoveryRetirement;
        private Task? _algorithmRetirement;
        private int _completed;

        private MaintenanceRetirementWitness(StationRuntime owner, Task[] physicalTasks,
            Task[] completionTasks, RecipeActivationCameraLease[] initialLeases, PreparedAlgorithm? activeAlgorithm,
            ProductionImageStager? imageStager, RuntimeDiagnosticService? diagnostics,
            RuntimePerformanceMonitor? performance, CameraSetupRuntime cameraSetup,
            CameraAcquisitionService? cameraAcquisition, CameraRecoveryService? cameraRecovery,
            ManualInspectionOwner? manualOwner, StationQualificationOwner? qualificationOwner,
            PreviewSessionOwner? previewOwner, ProductionInspectionOwner? productionOwner,
            ProductionRecoveryOwner? recoveryOwner, ImportPhysicalReservation? importPhysical,
            Task? capturedCalibrationWork, Task? capturedRecipeRetirementCleanup)
        {
            _owner = owner;
            _physicalTasks = physicalTasks;
            _completionTasks = completionTasks;
            _initialLeases = initialLeases;
            _activeAlgorithm = activeAlgorithm;
            _imageStager = imageStager;
            _diagnostics = diagnostics;
            _performance = performance;
            _cameraSetup = cameraSetup;
            _cameraAcquisition = cameraAcquisition;
            _cameraRecovery = cameraRecovery;
            _manualOwner = manualOwner;
            _qualificationOwner = qualificationOwner;
            _previewOwner = previewOwner;
            _productionOwner = productionOwner;
            _recoveryOwner = recoveryOwner;
            _importPhysical = importPhysical;
            _capturedCalibrationWork = capturedCalibrationWork;
            _capturedRecipeRetirementCleanup = capturedRecipeRetirementCleanup;
        }

        internal bool Completed => Volatile.Read(ref _completed) != 0;

        /// <summary>Retirement observations recorded while the witness waited (reason codes only).</summary>
        internal IReadOnlyList<string> Observations
        { get { lock (_sync) return _observations.ToArray(); } }

        internal async Task WaitAsync(CancellationToken token)
        {
            if (Completed) return;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = new List<Task>(32);
                var paced = false;

                foreach (var task in _physicalTasks) AddPhysicalPending(pending, task);
                foreach (var task in _completionTasks)
                    if (!task.IsCompleted) pending.Add(task);
                    else _ = task.Exception;

                lock (_owner._sync)
                {
                    AddPhysicalPending(pending, _owner._completion);
                    AddPhysicalPending(pending, _owner._recipeRetirementCleanup);
                    AddPhysicalPending(pending, _owner._calibrationWork);
                    AddPhysicalPending(pending, _owner._diagnosticSupportOperation?.Physical);
                    AddPhysicalPending(pending, _owner._diagnosticSupportOperation?.Retired.Task);
                    AddPhysicalPending(pending, _owner._diagnosticDeliveryRetired?.Task);
                }

                // Owner properties can be assigned after the capture; read them every turn.
                AddPhysicalPending(pending, _manualOwner?.Operation);
                AddPhysicalPending(pending, _manualOwner?.ExitWorker);
                AddPhysicalPending(pending, _manualOwner?.PreparationRetirement);
                AddPhysicalPending(pending, _qualificationOwner?.Operation);
                AddPhysicalPending(pending, _qualificationOwner?.PreparationRetirement);
                AddPhysicalPending(pending, _previewOwner?.Operation);
                AddPhysicalPending(pending, _previewOwner?.ExitWorker);
                AddPhysicalPending(pending, _previewOwner?.Reader);
                AddPhysicalPending(pending, _productionOwner?.CycleRetired?.Task);
                AddPhysicalPending(pending, _productionOwner?.PartIdentityOperation);
                AddPhysicalPending(pending, _productionOwner?.ExecutionCancellationTask);
                AddPhysicalPending(pending, _recoveryOwner?.Retired.Task);
                AddPhysicalPending(pending, _importPhysical?.Retirement);
                AddPhysicalPending(pending, _capturedCalibrationWork);
                AddPhysicalPending(pending, _capturedRecipeRetirementCleanup);

                EnsureLeaseObservations();
                Task<bool>[] leases;
                lock (_sync) leases = _leaseObservations.ToArray();
                foreach (var observation in leases)
                {
                    if (observation.IsCompletedSuccessfully)
                    {
                        if (!observation.Result)
                            throw new InvalidOperationException(MaintenancePhysicalRetirementUnverifiedReason);
                        continue;
                    }
                    if (observation.IsFaulted)
                        throw new InvalidOperationException(MaintenancePhysicalRetirementFaultedReason);
                    if (observation.IsCanceled)
                        throw new InvalidOperationException(MaintenancePhysicalRetirementUnverifiedReason);
                    pending.Add(observation);
                    paced = true;
                }

                var setup = _cameraSetup.CapturePhysicalLifetime();
                if (setup.RetainedDevices != 0)
                    throw new InvalidOperationException(MaintenancePhysicalRetirementRetainedReason);
                foreach (var task in setup.TrackedWork) AddPhysicalPending(pending, task);
                if (setup.RetiringDevices != 0 && setup.TrackedWork.Count == 0) paced = true;

                if (_imageStager is { } stager)
                {
                    AddPhysicalPending(pending, stager.PhysicalCompletion);
                    if (stager.ActiveOperationCount != 0) paced = true;
                }

                if (_diagnostics is { } diagnostics)
                {
                    AddPhysicalPending(pending, diagnostics.ActivationCompletion);
                    if (diagnostics.QueryPhysicalActive) AddPhysicalPending(pending, diagnostics.PhysicalQueryCompletion);
                    if (diagnostics.Pipeline is { } pipeline) AddPhysicalPending(pending, pipeline.PhysicalRetirement);
                }

                if (_performance is { } performance)
                {
                    AddPhysicalPending(pending, performance.PhysicalCompletion);
                    if (performance.PhysicalOperationActive) paced = true;
                }

                if (_activeAlgorithm is { } algorithm &&
                    (_algorithmRetirement ??= algorithm.DisposeAsync().AsTask()) is { } algorithmRetirement)
                    AddPhysicalPending(pending, algorithmRetirement);

                if (_cameraAcquisition is not null &&
                    (_acquisitionRetirement ??= RetireServiceAsync(_cameraAcquisition)) is { } acquisition)
                    CheckServiceRetirement(pending, acquisition);
                if (_cameraRecovery is not null &&
                    (_recoveryRetirement ??= RetireServiceAsync(_cameraRecovery)) is { } recovery)
                    CheckServiceRetirement(pending, recovery);

                if (pending.Count == 0)
                {
                    if (!paced)
                    {
                        Volatile.Write(ref _completed, 1);
                        Volatile.Write(ref _owner._maintenancePhysicalRetirementCompleted, 1);
                        return;
                    }
                    pending.Add(Task.Delay(10, token));
                }

                await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
            }
        }

        private static void AddPhysicalPending(List<Task> pending, Task? task)
        {
            if (task is null || task.IsCompletedSuccessfully) return;
            if (task.IsFaulted) throw new InvalidOperationException(MaintenancePhysicalRetirementFaultedReason);
            if (task.IsCanceled) throw new InvalidOperationException(MaintenancePhysicalRetirementUnverifiedReason);
            pending.Add(task);
        }

        private void CheckServiceRetirement(List<Task> pending, Task<bool> retirement)
        {
            if (retirement.IsCompletedSuccessfully)
            {
                if (!retirement.Result)
                    throw new InvalidOperationException(MaintenancePhysicalRetirementUnverifiedReason);
                return;
            }
            if (retirement.IsFaulted)
                throw new InvalidOperationException(MaintenancePhysicalRetirementFaultedReason);
            if (retirement.IsCanceled)
                throw new InvalidOperationException(MaintenancePhysicalRetirementUnverifiedReason);
            pending.Add(retirement);
        }

        private void EnsureLeaseObservations()
        {
            lock (_sync)
            {
                Observe(_initialLeases);
                Observe(_manualOwner?.Camera);
                Observe(_qualificationOwner?.Camera);
                Observe(_previewOwner?.Camera);
                Observe(_productionOwner?.Camera);

                void Observe(RecipeActivationCameraLease? lease)
                {
                    if (lease is null || !_observedLeases.Add(lease)) return;
                    _leaseObservations.Add(ObserveLeaseRetirementAsync(lease));
                }
            }
        }

        private async Task<bool> ObserveLeaseRetirementAsync(RecipeActivationCameraLease lease)
        {
            while (true)
            {
                var observation = await lease.WaitForManualRetirementAsync(
                    TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
                if (observation.Completed)
                {
                    lock (_sync) _observations.Add("CameraLease:" + observation.ReasonCode);
                    return observation.SafeToReplace;
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private async Task<bool> RetireServiceAsync(CameraAcquisitionService service)
        {
            var observation = await service.RetireAsync().ConfigureAwait(false);
            lock (_sync) _observations.Add("CameraAcquisition:" + observation.ReasonCode);
            return observation.SafeToReplace;
        }

        private async Task<bool> RetireServiceAsync(CameraRecoveryService service)
        {
            var observation = await service.RetireAsync().ConfigureAwait(false);
            lock (_sync) _observations.Add("CameraRecovery:" + observation.ReasonCode);
            return observation.SafeToReplace;
        }
    }
}
