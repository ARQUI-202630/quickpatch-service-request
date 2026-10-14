using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests</c> (contrato <c>service-request.v1.yaml</c>).</summary>
public sealed record CreateServiceRequestRequest(Guid? CategoryId, string? Description, LocationDto? Location, string? AddressText);

public sealed record LocationDto(double? Latitude, double? Longitude);

/// <summary>Esquema <c>ServiceRequest</c> del contrato (1.1.0: <c>assignedAt</c>, <c>technician</c> y <c>client</c>).</summary>
public sealed record ServiceRequestResponse(
    Guid Id,
    string Status,
    Guid CategoryId,
    Guid? TechnicianId,
    string Description,
    LocationDto Location,
    string AddressText,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt,
    TechnicianView? Technician,
    ClientView? Client)
{
    public static ServiceRequestResponse From(DomainServiceRequest s, TechnicianView? technician = null, ClientView? client = null) => new(
        s.Id,
        s.Status,
        s.CategoryId,
        s.TechnicianId,
        s.Description,
        new LocationDto(s.Location.Latitude, s.Location.Longitude),
        s.AddressText,
        s.CreatedAt,
        s.AssignedAt,
        technician,
        client);
}

public static class ServiceRequestEndpoints
{
    public const string BasePath = "/v1/service-requests";

    public static IEndpointRouteBuilder MapServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BasePath);
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
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var errors = new Dictionary<string, string[]>();
        if (body.CategoryId is null)
        {
            errors["categoryId"] = ["La categoría es obligatoria."];
        }

        if (body.Location?.Latitude is null || body.Location.Longitude is null)
        {
            errors["location"] = ["La ubicación con latitud y longitud es obligatoria."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        var command = new CreateServiceRequestCommand(
            user.TenantId,
            user.UserId,
            CorrelationId.Get(context),
            body.CategoryId!.Value,
            body.Description,
            new GeoPoint(body.Location!.Latitude!.Value, body.Location.Longitude!.Value),
            body.AddressText);

        CreateServiceRequestResult result;
        try
        {
            result = await handler.HandleAsync(command, cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }

        return result switch
        {
            CreateServiceRequestResult.Created created => Results.Created(
                $"{BasePath}/{created.ServiceRequest.Id}", ServiceRequestResponse.From(created.ServiceRequest)),
            CreateServiceRequestResult.OutOfCoverage => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.OutOfCoverage,
                title: "La ubicación está fuera del área de cobertura",
                detail: "El servicio opera solo en Bogotá D.C."),
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
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        // El token se reenvía a Identity para leer los nombres (identity-internal.v1.yaml); nunca se registra.
        var token = context.Request.Headers.Authorization.ToString();
        token = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token["Bearer ".Length..].Trim() : string.Empty;
        var viewer = new ServiceRequestViewer(user.UserId, user.TenantId, user.Role, token, CorrelationId.Get(context));

        var result = await handler.HandleAsync(viewer, id, cancellationToken);
        return result switch
        {
            GetServiceRequestResult.Found found => Results.Ok(ServiceRequestResponse.From(found.ServiceRequest, found.Technician, found.Client)),
            GetServiceRequestResult.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "No puedes ver esta solicitud en su estado actual.",
                detail: "El técnico asignado ve la solicitud solo entre asignado y completado."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe."),
        };
    }

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}