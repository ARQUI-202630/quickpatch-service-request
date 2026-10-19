using Microsoft.Extensions.DependencyInjection;

using QuickPatch.ServiceRequest.Application.Categories;
using QuickPatch.ServiceRequest.Application.Matching;
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
        services.AddScoped<PreviewLocationHandler>();
        services.AddScoped<GetServiceRequestHandler>();
        services.AddScoped<ListTechnicianServiceRequestsHandler>();
        services.AddScoped<ListTenantServiceRequestsHandler>();
        services.AddScoped<CancelServiceRequestHandler>();
        services.AddScoped<GetTenantSummaryHandler>();
        services.AddScoped<GetPlatformSummaryHandler>();
        services.AddScoped<ApplyCategoryChangedHandler>();
        services.AddScoped<MatchingStatusHandler>();
        return services;
    }
}