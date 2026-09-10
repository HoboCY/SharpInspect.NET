using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Preview;

namespace SharpInspect.Runtime;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one explicitly constructed, non-production acquisition component.
    /// The service provider owns its lifetime; the component exclusively owns its device.
    /// StationRuntime observes protocol facts without granting production admission.
    /// </summary>
    public static IServiceCollection AddSharpInspectCameraAcquisition(this IServiceCollection services,
        Func<IServiceProvider, CameraAcquisitionService> factory)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(factory);
        if (services.Any(item => item.ServiceType == typeof(CameraAcquisitionService) ||
            item.ServiceType == typeof(CameraRecoveryService)))
            throw new ArgumentException("CameraAcquisitionAlreadyRegistered", nameof(services));
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Registers one qualification camera owner with bounded same-device recovery.
    /// The container owns the recovery service, which owns its acquisition services.
    /// The recovery service also owns its dedicated provider; the clock remains
    /// the factory caller's responsibility.
    /// </summary>
    public static IServiceCollection AddSharpInspectCameraRecovery(this IServiceCollection services,
        Func<IServiceProvider, CameraRecoveryService> factory)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(factory);
        if (services.Any(item => item.ServiceType == typeof(CameraAcquisitionService) ||
            item.ServiceType == typeof(CameraRecoveryService)))
            throw new ArgumentException("CameraRecoveryAlreadyRegistered", nameof(services));
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>Registers the bounded computation engine without opening production admission.</summary>
    public static IServiceCollection AddSharpInspectAlgorithmExecution(this IServiceCollection services,
        AlgorithmExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options);
        if (services.Any(item => item.ServiceType == typeof(AlgorithmExecutionGuard) ||
            item.ServiceType == typeof(AlgorithmExecutionOptions) ||
            item.ServiceType == typeof(AlgorithmExecutionService)))
            throw new ArgumentException("AlgorithmExecutionAlreadyRegistered", nameof(services));
        services.AddSingleton(AlgorithmExecutionGuard.CurrentProcess);
        services.AddSingleton(options);
        services.AddSingleton<AlgorithmExecutionService>();
        return services;
    }

    /// <summary>Uses only explicitly registered IVisionAlgorithmFactory services; it does not scan or load assemblies.</summary>
    public static IServiceCollection AddSharpInspectAlgorithmPreparation(this IServiceCollection services,
        AlgorithmPreparationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(item => item.ServiceType == typeof(AlgorithmPreparationService) ||
            item.ServiceType == typeof(AlgorithmPreparationOptions)))
            throw new ArgumentException("AlgorithmPreparationAlreadyRegistered", nameof(services));
        services.AddSingleton(options);
        services.AddSingleton<AlgorithmPreparationService>();
        return services;
    }

    /// <summary>Registers the explicitly sized, bounded frame buffer pool used by acquisition adapters.</summary>
    public static IServiceCollection AddSharpInspectFrameBufferPool(this IServiceCollection services,
        FrameBufferPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(item => item.ServiceType == typeof(FrameBufferPoolOptions) ||
            item.ServiceType == typeof(FrameBufferPool)))
            throw new ArgumentException("FrameBufferPoolAlreadyRegistered", nameof(services));
        services.AddSingleton(options);
        services.AddSingleton<FrameBufferPool>();
        return services;
    }

    /// <summary>
    /// Registers one explicit camera provider.  The Runtime never scans assemblies
    /// or selects a provider by discovery order; all providers are matched by the
    /// four-field <see cref="CameraProviderIdentity"/> value.
    /// </summary>
    public static IServiceCollection AddSharpInspectCameraProvider(this IServiceCollection services,
        ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);
        services.AddSingleton<ICameraProvider>(provider);
        return services;
    }

    /// <summary>Registers bounded camera setup operation settings.</summary>
    public static IServiceCollection AddSharpInspectCameraSetup(this IServiceCollection services,
        CameraSetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(item => item.ServiceType == typeof(CameraSetupOptions)))
            throw new ArgumentException("CameraSetupAlreadyRegistered", nameof(services));
        services.AddSingleton(options);
        return services;
    }

    /// <summary>Explicit managed registration. The caller owns the service provider lifetime.</summary>
    public static IServiceCollection AddSharpInspectCalibrationSessions(this IServiceCollection services,
        CalibrationSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (services.Any(item => item.ServiceType == typeof(CalibrationSessionOptions)))
            throw new ArgumentException("CalibrationSessionsAlreadyRegistered", nameof(services));
        services.AddSingleton(options);
        return services;
    }

    /// <summary>Explicit project verifier registration. It does not confer production or metrological qualification.</summary>
    public static IServiceCollection AddSharpInspectPhysicalCalibrationVerification(this IServiceCollection services,
        PhysicalCalibrationVerificationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        if (services.Any(item => item.ServiceType == typeof(PhysicalCalibrationVerificationRegistry)))
            throw new ArgumentException("PhysicalVerificationRegistryAlreadyRegistered", nameof(services));
        services.AddSingleton(registry);
        return services;
    }

    /// <summary>Explicit verifier for unpublished imported candidates. Registration grants no production authority.</summary>
    public static IServiceCollection AddSharpInspectImportedCalibrationPhysicalVerification(this IServiceCollection services,
        ImportedCalibrationPhysicalVerificationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        if (services.Any(item => item.ServiceType == typeof(ImportedCalibrationPhysicalVerificationRegistry)))
            throw new ArgumentException("ImportedPhysicalVerificationRegistryAlreadyRegistered", nameof(services));
        services.AddSingleton(registry);
        return services;
    }

    /// <summary>Explicit managed registration. The caller owns the service provider lifetime.</summary>
    public static IServiceCollection AddSharpInspectRuntime(this IServiceCollection services,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IStationRuntime>(p => new StationRuntime(p.GetService<SqliteCommandStore>(), heartbeatInterval,
            frameBufferPool: p.GetService<FrameBufferPool>(), executionGuard: p.GetService<AlgorithmExecutionGuard>(),
            algorithmExecutionOptions: p.GetService<AlgorithmExecutionOptions>(),
            cameraProviders: p.GetServices<ICameraProvider>(),
            cameraSetupOptions: p.GetService<CameraSetupOptions>(),
            cameraAcquisitionService: p.GetService<CameraAcquisitionService>(),
            cameraRecoveryService: p.GetService<CameraRecoveryService>(),
            calibrationSessionOptions: p.GetService<CalibrationSessionOptions>(),
            calibrationProcedures: p.GetService<CalibrationProcedureRegistry>(),
            productionStoreOptions: p.GetService<ProductionStoreOptions>(),
            physicalCalibrationVerificationRegistry: p.GetService<PhysicalCalibrationVerificationRegistry>()));
        services.TryAddSingleton<ICameraSetupRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICameraSetupRuntime ??
            throw new InvalidOperationException("CameraSetupRuntimeUnavailable"));
        services.TryAddSingleton<ICameraNetworkMaintenanceRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICameraNetworkMaintenanceRuntime ??
            throw new InvalidOperationException("CameraNetworkMaintenanceRuntimeUnavailable"));
        services.TryAddSingleton<IImagingSetupRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as IImagingSetupRuntime ??
            throw new InvalidOperationException("ImagingSetupRuntimeUnavailable"));
        services.TryAddSingleton<ICalibrationSessionQuery>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationSessionQuery ??
            throw new InvalidOperationException("CalibrationSessionQueryUnavailable"));
        RegisterCalibrationGovernance(services);
        return services;
    }

    /// <summary>Explicit SQLite authority and separate read-only trace capability. Dispose the provider asynchronously.</summary>
    public static IServiceCollection AddSharpInspectSqliteRuntime(this IServiceCollection services,
        ProductionStoreOptions options, TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<SqliteCommandStore>();
        services.TryAddSingleton<ICommandTraceQuery, SqliteCommandTraceQuery>();
        services.TryAddSingleton<IAuditIntegrityQuery, SqliteAuditIntegrityQuery>();
        services.TryAddSingleton<IAlarmHistoryQuery, SqliteAlarmHistoryQuery>();
        if (options.AlgorithmResultArchive is not null)
        {
            services.TryAddSingleton<IAlgorithmResultQuery, SqliteAlgorithmResultQuery>();
            services.TryAddSingleton(p => new AlgorithmResultArchive(p.GetRequiredService<SqliteCommandStore>()));
        }
        if (options.LocalIdentity is not null)
        {
            services.TryAddSingleton(p => new LocalIdentityService(p.GetRequiredService<SqliteCommandStore>(), options.LocalIdentity));
            services.TryAddSingleton<IIdentityProvider>(p => p.GetRequiredService<LocalIdentityService>());
            services.TryAddSingleton<ILocalAdministratorBootstrap>(p => p.GetRequiredService<LocalIdentityService>());
            services.TryAddSingleton<IInteractiveSessionService>(p => new InteractiveSessionService(
                p.GetRequiredService<IIdentityProvider>(), options.LocalIdentity.AuthenticationPolicy,
                p.GetRequiredService<LocalIdentityService>().PersistSessionEventAsync));
            services.TryAddSingleton(p => new LocalAuthorizationService(p.GetRequiredService<SqliteCommandStore>(),
                options.LocalIdentity, p.GetRequiredService<IIdentityProvider>(), p.GetRequiredService<IInteractiveSessionService>()));
            services.TryAddSingleton<IStepUpAuthentication>(p => p.GetRequiredService<LocalAuthorizationService>());
            services.TryAddSingleton<IIdentityAdministrationQuery>(p => p.GetRequiredService<LocalAuthorizationService>());
            if (options.RecipeDrafts is not null)
            {
                // The history capability is an independent read-only connection; resolving it
                // never initializes or opens the command writer.
                services.TryAddSingleton<IRecipeDraftHistoryQuery>(_ => new SqliteRecipeDraftQuery(options));
                services.TryAddSingleton<RecipeDraftService>(p => new RecipeDraftService(
                    p.GetServices<IVisionAlgorithmFactory>(), options,
                    p.GetRequiredService<LocalAuthorizationService>(),
                    p.GetRequiredService<IRecipeDraftHistoryQuery>()));
                services.TryAddSingleton<IRecipeDraftEditor>(p => p.GetRequiredService<RecipeDraftService>());
                services.TryAddSingleton<AlgorithmConfigurationMigrationRegistry>(p =>
                    new AlgorithmConfigurationMigrationRegistry(p.GetServices<IAlgorithmConfigurationMigrator>(),
                        options.CommitTimeout));
                services.TryAddSingleton<IAlgorithmConfigurationMigrationService>(p =>
                    new AlgorithmConfigurationMigrationService(p.GetRequiredService<RecipeDraftService>(),
                        p.GetRequiredService<LocalAuthorizationService>(),
                        p.GetRequiredService<AlgorithmConfigurationMigrationRegistry>(), options));
                if (options.RecipeReleases is not null)
                {
                    services.TryAddSingleton<IReleasedRecipeQuery>(_ => new SqliteReleasedRecipeQuery(options));
                    services.TryAddSingleton<IRecipeReleaseService>(p => new RecipeReleaseService(
                        p.GetRequiredService<RecipeDraftService>(), p.GetRequiredService<LocalAuthorizationService>(),
                        p.GetRequiredService<IReleasedRecipeQuery>(), options,
                        () => p.GetRequiredService<IStationRuntime>().GetSnapshotAsync()));
                    if (options.PlcResultContracts is not null)
                    {
                        services.TryAddSingleton<IPlcResultContractQuery>(_ => new SqlitePlcResultContractQuery(options));
                        services.TryAddSingleton<IPlcResultContractService>(p => new PlcResultContractService(
                            p.GetRequiredService<RecipeDraftService>(), p.GetRequiredService<IReleasedRecipeQuery>(),
                            p.GetRequiredService<IPlcResultContractQuery>(), p.GetRequiredService<LocalAuthorizationService>(), options,
                            token => p.GetRequiredService<IStationRuntime>() is StationRuntime authority
                                ? authority.EnterPlcResultContractChangeAsync(token)
                                : ValueTask.FromResult(new PlcResultContractRuntimeLease(Guid.Empty, () => "PlcResultContractRuntimeUnavailable")),
                            () => p.GetRequiredService<IStationRuntime>().GetSnapshotAsync()));
                        if (options.RecipeActivations is not null)
                        {
                            services.TryAddSingleton<IRecipeActivationQuery>(_ => new SqliteRecipeActivationQuery(options));
                            services.TryAddSingleton(p => new RecipeActivationService(
                                p.GetRequiredService<RecipeDraftService>(), p.GetRequiredService<IReleasedRecipeQuery>(),
                                p.GetRequiredService<IPlcResultContractQuery>(), p.GetRequiredService<IRecipeActivationQuery>(),
                                p.GetRequiredService<LocalAuthorizationService>(), p.GetRequiredService<SqliteCommandStore>(),
                                options, p.GetService<AlgorithmPreparationService>(), p.GetService<AlgorithmPreparationOptions>(),
                                p.GetService<FrameBufferPool>(),
                                (correlation, token) => p.GetRequiredService<IStationRuntime>() is StationRuntime authority
                                    ? authority.ReserveRecipeActivationAsync(correlation, token)
                                    : ValueTask.FromResult(new RecipeActivationRuntimeLease(Guid.Empty,
                                        "RecipeActivationRuntimeUnavailable", token)),
                                () => p.GetRequiredService<IStationRuntime>().GetSnapshotAsync()));
                            services.TryAddSingleton<IRecipeActivationService>(p => p.GetRequiredService<RecipeActivationService>());
                        }
                    }
                }
            }
            services.TryAddSingleton<ILocalAdministratorRecovery>(p => new LocalAdministratorRecoveryService(
                p.GetRequiredService<SqliteCommandStore>(), options.LocalIdentity,
                p.GetRequiredService<LocalIdentityService>(), p.GetService<IInteractiveSessionService>(),
                p.GetService<IStationRuntime>() as IAdministratorRecoveryRuntimeGate ??
                    new UnavailableAdministratorRecoveryRuntimeGate()));
        }
        if (options.PreviewSessions is not null)
            services.TryAddSingleton<IPreviewSessionHistoryQuery>(_ => new SqlitePreviewSessionQuery(options));
        if (options.ManualInspections is not null)
            services.TryAddSingleton<IManualInspectionHistoryQuery>(_ => new SqliteManualInspectionQuery(options));
        if (options.ProductionAdmission is not null)
            services.TryAddSingleton<IProductionAdmissionHistoryQuery>(_ => new SqliteProductionAdmissionHistoryQuery(options));
        services.TryAddSingleton<IStationRuntime>(p =>
        {
            var runtime = new StationRuntime(p.GetRequiredService<SqliteCommandStore>(), heartbeatInterval,
            p.GetService<IInteractiveSessionService>(), p.GetService<LocalAuthorizationService>(),
            p.GetService<FrameBufferPool>(), p.GetService<AlgorithmExecutionGuard>(),
            p.GetService<AlgorithmExecutionOptions>(), p.GetServices<ICameraProvider>(),
            p.GetService<CameraSetupOptions>(), p.GetService<CameraAcquisitionService>(),
            p.GetService<CameraRecoveryService>(), p.GetService<CalibrationSessionOptions>(),
            p.GetService<CalibrationProcedureRegistry>(), options, p.GetService<PhysicalCalibrationVerificationRegistry>());
            if (p.GetService<IRecipeReleaseService>() is { } releases) runtime.ConfigureRecipeReleaseService(releases);
            if (p.GetService<IPlcResultContractService>() is { } contracts) runtime.ConfigurePlcResultContractService(contracts);
            if (options.RecipeActivations is not null)
                runtime.ConfigureRecipeActivationService(p.GetRequiredService<RecipeActivationService>());
            if (options.PreviewSessions is not null)
                runtime.ConfigurePreviewSessions(p.GetService<PreviewSessionOptions>() ?? new PreviewSessionOptions(),
                    p.GetRequiredService<RecipeDraftService>(), options);
            if (options.ManualInspections is not null)
                runtime.ConfigureManualInspectionSessions(
                    p.GetService<SharpInspect.Runtime.Manual.ManualInspectionSessionOptions>() ??
                        new SharpInspect.Runtime.Manual.ManualInspectionSessionOptions(),
                    p.GetRequiredService<RecipeDraftService>(), p.GetService<IReleasedRecipeQuery>(),
                    p.GetService<AlgorithmPreparationService>(), p.GetService<AlgorithmPreparationOptions>(),
                    p.GetService<AlgorithmExecutionOptions>(), options, p.GetService<IFrameAcquisitionClock>());
            runtime.ConfigureCalibrationImports(options, p.GetService<ImportedCalibrationPhysicalVerificationRegistry>());
            return runtime;
        });
        services.TryAddSingleton<ICameraSetupRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICameraSetupRuntime ??
            throw new InvalidOperationException("CameraSetupRuntimeUnavailable"));
        services.TryAddSingleton<ICameraNetworkMaintenanceRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICameraNetworkMaintenanceRuntime ??
            throw new InvalidOperationException("CameraNetworkMaintenanceRuntimeUnavailable"));
        services.TryAddSingleton<IImagingSetupRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as IImagingSetupRuntime ??
            throw new InvalidOperationException("ImagingSetupRuntimeUnavailable"));
        services.TryAddSingleton<ICalibrationSessionQuery>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationSessionQuery ??
            throw new InvalidOperationException("CalibrationSessionQueryUnavailable"));
        RegisterCalibrationGovernance(services);
        return services;
    }

    private static void RegisterCalibrationGovernance(IServiceCollection services)
    {
        services.TryAddSingleton<ICalibrationImportRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationImportRuntime ??
            throw new InvalidOperationException("CalibrationImportUnavailable"));
        services.TryAddSingleton<ICalibrationImportQuery>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationImportQuery ??
            throw new InvalidOperationException("CalibrationImportUnavailable"));
        services.TryAddSingleton<IPreviewSessionService>(p =>
            p.GetRequiredService<IStationRuntime>() as IPreviewSessionService ??
            throw new InvalidOperationException("PreviewSessionServiceUnavailable"));
        services.TryAddSingleton<IManualInspectionSessionService>(p =>
            p.GetRequiredService<IStationRuntime>() as IManualInspectionSessionService ??
            throw new InvalidOperationException("ManualInspectionSessionServiceUnavailable"));
        services.TryAddSingleton<ICalibrationGovernanceRuntime>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationGovernanceRuntime ??
            throw new InvalidOperationException("CalibrationGovernanceUnavailable"));
        services.TryAddSingleton<ICalibrationGovernanceQuery>(p =>
            p.GetRequiredService<IStationRuntime>() as ICalibrationGovernanceQuery ??
            throw new InvalidOperationException("CalibrationGovernanceUnavailable"));
    }
}
