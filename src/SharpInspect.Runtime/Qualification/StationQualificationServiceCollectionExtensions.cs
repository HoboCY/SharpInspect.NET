using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

public static class StationQualificationServiceCollectionExtensions
{
    /// <summary>
    /// Selects the explicit loopback Modbus development profile. The private
    /// facility lease still proves isolation and owns restoration; requests and
    /// acknowledgements for this entry are read from the TCP server by Runtime.
    /// </summary>
    public static IServiceCollection AddSharpInspectModbusStationQualificationSessions(
        this IServiceCollection services, StationQualificationPlan plan,
        IStationQualificationFacility facility, ModbusQualificationProfile profile,
        StationQualificationSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Harness.DevelopmentOnly || plan.ProfileHash != profile.ContentHash ||
            plan.TransientControllerConfiguration.EndpointBindingHash != profile.EndpointBindingHash ||
            !plan.ScenarioIds.Contains(profile.ScenarioId, StringComparer.Ordinal))
            throw new ArgumentException("QualificationModbusProfileBindingMismatch", nameof(profile));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModbusQualificationProfile)))
            throw new InvalidOperationException("QualificationModbusAlreadyRegistered");
        services.AddSharpInspectStationQualificationSessions(plan, facility, options);
        services.AddSingleton(profile);
        return services;
    }

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
