using Microsoft.Extensions.DependencyInjection;

using QuickPatch.ServiceRequest.Application.Categories;
using QuickPatch.ServiceRequest.Application.ServiceRequests;

namespace QuickPatch.ServiceRequest.Application;

/// <summary>
/// Capa de aplicación: casos de uso, comandos y consultas (SDD 6.3).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CreateServiceRequestHandler>();
        services.AddScoped<GetServiceRequestHandler>();
        services.AddScoped<ApplyCategoryChangedHandler>();
        return services;
    }
}