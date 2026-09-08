using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

public static class CalibrationServiceCollectionExtensions
{
    /// <summary>Registers development diagnostics only. Supplied fixture coefficients cannot satisfy production admission.</summary>
    public static IServiceCollection AddSharpInspectCalibrationRequirementChecks(this IServiceCollection services,
        CalibrationFixtureCatalog? fixtures = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(item => item.ServiceType == typeof(ICalibrationRequirementResolver)))
            throw new ArgumentException("CalibrationRequirementResolverAlreadyRegistered", nameof(services));
        services.AddSingleton<ICalibrationRequirementResolver>(provider => new CalibrationRequirementResolver(
            provider.GetRequiredService<ICameraSetupRuntime>(), provider.GetRequiredService<IImagingSetupRuntime>(), fixtures));
        return services;
    }
}
