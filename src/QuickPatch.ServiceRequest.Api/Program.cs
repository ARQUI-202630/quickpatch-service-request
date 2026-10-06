using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using QuickPatch.ServiceRequest.Api.Endpoints;
using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application;
using QuickPatch.ServiceRequest.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // El contrato declara additionalProperties: false; un campo desconocido es un error de validación (400).
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddHealthChecks();
builder.Services.AddQuickPatchAuthentication(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Probes de k3s (Documento de Infraestructura, sección 5.8).
// live: el proceso responde; ready: además, PostgreSQL responde.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(QuickPatch.ServiceRequest.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapServiceRequestEndpoints();

app.Run();

/// <summary>Punto de entrada; público para las pruebas con WebApplicationFactory.</summary>
public partial class Program;