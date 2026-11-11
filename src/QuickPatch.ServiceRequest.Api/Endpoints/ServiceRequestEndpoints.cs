using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Alta y consulta de una solicitud (<c>POST /v1/service-requests</c> y <c>GET /v1/service-requests/{id}</c>).</summary>
public static class ServiceRequestEndpoints
{
    public static IEndpointRouteBuilder MapServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Routes.ServiceRequests);
        group.MapPost("/", CreateAsync).WithName("createServiceRequest").RequireAuthorization(AuthenticatedUser.ClientPolicy);
        group.MapGet("/{id:guid}", GetAsync).WithName("getServiceRequest").RequireAuthorization(AuthenticatedUser.ReadPolicy);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateServiceRequestRequest body,
        HttpContext context,
        CreateServiceRequestHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return ApiResults.InvalidToken();
        }

        var errors = new Dictionary<string, string[]>();
        if (body.CategoryId is null)
        {
            errors["categoryId"] = ["La categoría es obligatoria."];
        }

        // Desde 1.3.0 location es opcional (sin ella se geocodifica addressText), pero si llega debe estar completa.
        if (body.Location is not null && (body.Location.Latitude is null || body.Location.Longitude is null))
        {
            errors["location"] = ["La ubicación debe traer latitud y longitud."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem(errors);
        }

        // RN-SR12: la empresa sale del claim company_id. Un empresa_contacto sin empresa en el token no crea nada.
        var companyId = user.Role == AuthenticatedUser.CompanyContactRole ? user.CompanyId : null;
        if (user.Role == AuthenticatedUser.CompanyContactRole && companyId is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                type: Problems.Forbidden,
                title: "El token no trae la empresa del usuario.");
        }

        var command = new CreateServiceRequestCommand(
            user.TenantId,
            user.UserId,
            CorrelationId.Get(context),
            body.CategoryId!.Value,
            body.Description,
            body.Location is null ? null : new GeoPoint(body.Location.Latitude!.Value, body.Location.Longitude!.Value),
            body.AddressText,
            companyId,
            body.SiteName);

        CreateServiceRequestResult result;
        try
        {
            result = await handler.HandleAsync(command, cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ApiResults.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            CreateServiceRequestResult.Created created => Results.Created(
                $"{Routes.ServiceRequests}/{created.ServiceRequest.Id}", ServiceRequestResponse.From(created.ServiceRequest)),
            CreateServiceRequestResult.OutOfCoverage => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.OutOfCoverage,
                title: "La ubicación está fuera del área de cobertura",
                detail: "El servicio opera solo en Bogotá D.C."),
            CreateServiceRequestResult.AddressNotFound => ApiResults.AddressNotFound(),
            CreateServiceRequestResult.TenantInactive => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.TenantInactive,
                title: "El tenant está inactivo",
                detail: "tenant is disabled"),
            _ => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.CategoryUnavailable,
                title: "La categoría no está disponible",
                detail: "La categoría no existe o no está activa en tu empresa."),
        };
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        GetServiceRequestHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return ApiResults.InvalidToken();
        }

        // El token se reenvía a Identity para leer los nombres (identity-internal.v1.yaml); nunca se registra.
        var viewer = new ServiceRequestViewer(
            user.UserId, user.TenantId, user.Role, AuthenticatedUser.BearerToken(context), CorrelationId.Get(context), user.CompanyId);

        var result = await handler.HandleAsync(viewer, id, cancellationToken);
        return result switch
        {
            GetServiceRequestResult.Found found => Results.Ok(ServiceRequestResponse.From(found.ServiceRequest, found.Technician, found.Client, found.Rating)),
            GetServiceRequestResult.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "No puedes ver esta solicitud en su estado actual.",
                detail: "El técnico asignado ve la solicitud solo entre asignado y completado."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe."),
        };
    }
}