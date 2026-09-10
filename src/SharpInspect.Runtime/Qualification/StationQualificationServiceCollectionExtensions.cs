using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

public static class StationQualificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers one immutable qualification plan and versioned trusted-host
    /// facility. Registration supplies no production acceptance or arm authority.
    /// Also enable ProductionStoreOptions.StationQualifications for durable sessions.
    /// </summary>
    public static IServiceCollection AddSharpInspectStationQualificationSessions(this IServiceCollection services,
        StationQualificationPlan plan, IStationQualificationFacility facility,
        StationQualificationSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facility);
        var actualOptions = options ?? new StationQualificationSessionOptions();
        actualOptions.Validate();
        if (facility.Identity != plan.Harness) throw new ArgumentException("StationQualificationHarnessIdentityMismatch", nameof(facility));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IStationQualificationFacility) ||
            descriptor.ServiceType == typeof(StationQualificationPlan)))
            throw new InvalidOperationException("StationQualificationAlreadyRegistered");
        services.AddSingleton(plan);
        services.AddSingleton<IStationQualificationFacility>(facility);
        services.AddSingleton(actualOptions);
        services.TryAddSingleton<IStationQualificationSessionService>(provider =>
            provider.GetRequiredService<IStationRuntime>() as IStationQualificationSessionService ??
            throw new InvalidOperationException("StationQualificationSessionServiceUnavailable"));
        return services;
    }
}
