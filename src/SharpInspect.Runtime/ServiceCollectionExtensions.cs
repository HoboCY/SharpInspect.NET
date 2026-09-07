using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime;

public static class ServiceCollectionExtensions
{
    /// <summary>Explicit managed registration. The caller owns the service provider lifetime.</summary>
    public static IServiceCollection AddSharpInspectRuntime(this IServiceCollection services,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IStationRuntime>(p => new StationRuntime(p.GetService<SqliteCommandStore>(), heartbeatInterval));
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
            p.GetService<IInteractiveSessionService>(), p.GetService<LocalAuthorizationService>()));
        return services;
    }
}
