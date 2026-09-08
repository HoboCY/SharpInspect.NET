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
            productionStoreOptions: p.GetService<ProductionStoreOptions>()));
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
            }
            services.TryAddSingleton<ILocalAdministratorRecovery>(p => new LocalAdministratorRecoveryService(
                p.GetRequiredService<SqliteCommandStore>(), options.LocalIdentity,
                p.GetRequiredService<LocalIdentityService>(), p.GetService<IInteractiveSessionService>(),
                p.GetService<IStationRuntime>() as IAdministratorRecoveryRuntimeGate ??
                    new UnavailableAdministratorRecoveryRuntimeGate()));
        }
        services.TryAddSingleton<IStationRuntime>(p => new StationRuntime(p.GetRequiredService<SqliteCommandStore>(), heartbeatInterval,
            p.GetService<IInteractiveSessionService>(), p.GetService<LocalAuthorizationService>(),
            p.GetService<FrameBufferPool>(), p.GetService<AlgorithmExecutionGuard>(),
            p.GetService<AlgorithmExecutionOptions>(), p.GetServices<ICameraProvider>(),
            p.GetService<CameraSetupOptions>(), p.GetService<CameraAcquisitionService>(),
            p.GetService<CameraRecoveryService>(), p.GetService<CalibrationSessionOptions>(),
            p.GetService<CalibrationProcedureRegistry>(), options));
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
        return services;
    }
}
