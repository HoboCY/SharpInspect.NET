using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime;

public static class ServiceCollectionExtensions
{
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

    /// <summary>Explicit managed registration. The caller owns the service provider lifetime.</summary>
    public static IServiceCollection AddSharpInspectRuntime(this IServiceCollection services,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IStationRuntime>(p => new StationRuntime(p.GetService<SqliteCommandStore>(), heartbeatInterval,
            frameBufferPool: p.GetService<FrameBufferPool>(), executionGuard: p.GetService<AlgorithmExecutionGuard>(),
            algorithmExecutionOptions: p.GetService<AlgorithmExecutionOptions>()));
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
            services.TryAddSingleton<ILocalAdministratorRecovery>(p => new LocalAdministratorRecoveryService(
                p.GetRequiredService<SqliteCommandStore>(), options.LocalIdentity,
                p.GetRequiredService<LocalIdentityService>(), p.GetService<IInteractiveSessionService>(),
                p.GetService<IStationRuntime>() as IAdministratorRecoveryRuntimeGate ??
                    new UnavailableAdministratorRecoveryRuntimeGate()));
        }
        services.TryAddSingleton<IStationRuntime>(p => new StationRuntime(p.GetRequiredService<SqliteCommandStore>(), heartbeatInterval,
            p.GetService<IInteractiveSessionService>(), p.GetService<LocalAuthorizationService>(),
            p.GetService<FrameBufferPool>(), p.GetService<AlgorithmExecutionGuard>(),
            p.GetService<AlgorithmExecutionOptions>()));
        return services;
    }
}
