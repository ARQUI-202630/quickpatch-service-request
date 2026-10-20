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

/// <summary>Cuerpo de <c>POST /v1/service-requests/location-preview</c> (contrato 1.3.0).</summary>
public sealed record LocationPreviewRequest(string? AddressText);

/// <summary>Esquema <c>LocationPreview</c> (1.3.0).</summary>
public sealed record LocationPreviewResponse(LocationDto Location, LocalityDto Locality, string MatchedAddress);

public sealed record LocalityDto(string Code, string Name);

/// <summary>
/// Esquema <c>ServiceRequest</c> del contrato (1.1.0: <c>assignedAt</c>, <c>technician</c> y <c>client</c>; 1.5.0: la cancelación;
/// 1.7.0: <c>startedAt</c> y <c>completedAt</c>).
/// </summary>
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
    ClientView? Client,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
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
        client,
        s.CancelledAt,
        s.CancellationReason,
        s.StartedAt,
        s.CompletedAt);
}

/// <summary>Cuerpo de <c>POST /v1/service-requests/{id}/cancel</c> (contrato 1.5.0).</summary>
public sealed record CancelServiceRequestRequest(string? Reason);

/// <summary>Esquema <c>CancelledServiceRequest</c> (1.5.0).</summary>
public sealed record CancelledServiceRequestResponse(Guid Id, string Status, DateTimeOffset CancelledAt, string CancelledByType);

/// <summary>Esquema <c>TechnicianServiceRequest</c> (1.2.0): sin dirección, ubicación ni cliente (RN-SR13).</summary>
public sealed record TechnicianServiceRequestResponse(
    Guid Id,
    string Status,
    Guid CategoryId,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt)
{
    public static TechnicianServiceRequestResponse From(DomainServiceRequest s) =>
        new(s.Id, s.Status, s.CategoryId, s.Description, s.CreatedAt, s.AssignedAt);
}

/// <summary>Esquema <c>TechnicianServiceRequestPage</c> (1.2.0).</summary>
public sealed record TechnicianServiceRequestPageResponse(
    IReadOnlyList<TechnicianServiceRequestResponse> Items, int Page, int Size, int TotalItems);

public static class ServiceRequestEndpoints
{
    public const string BasePath = "/v1/service-requests";
    public const string TechnicianPath = "/v1/technicians/me/service-requests";

    public static IEndpointRouteBuilder MapServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BasePath);
        group.MapPost("/", CreateAsync).WithName("createServiceRequest").RequireAuthorization(AuthenticatedUser.ClientPolicy);
        group.MapPost("/location-preview", PreviewLocationAsync).WithName("previewServiceRequestLocation")
            .RequireAuthorization(AuthenticatedUser.PreviewPolicy);
        group.MapPost("/{id:guid}/cancel", CancelAsync).WithName("cancelServiceRequest").RequireAuthorization(AuthenticatedUser.CancelPolicy);
        group.MapGet("/{id:guid}", GetAsync).WithName("getServiceRequest").RequireAuthorization(AuthenticatedUser.ReadPolicy);
        app.MapGet(TechnicianPath, ListForTechnicianAsync).WithName("listTechnicianServiceRequests")
            .RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
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

        // Desde 1.3.0 location es opcional (sin ella se geocodifica addressText), pero si llega debe estar completa.
        if (body.Location is not null && (body.Location.Latitude is null || body.Location.Longitude is null))
        {
            errors["location"] = ["La ubicación debe traer latitud y longitud."];
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
            body.Location is null ? null : new GeoPoint(body.Location.Latitude!.Value, body.Location.Longitude!.Value),
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
            CreateServiceRequestResult.AddressNotFound => AddressNotFound(),
            _ => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.CategoryUnavailable,
                title: "La categoría no está disponible",
                detail: "La categoría no existe o no está activa en tu empresa."),
        };
    }

    private static async Task<IResult> PreviewLocationAsync(
        LocationPreviewRequest body,
        HttpContext context,
        PreviewLocationHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out _))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        PreviewLocationResult result;
        try
        {
            result = await handler.HandleAsync(body.AddressText, cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }

        return result switch
        {
            PreviewLocationResult.Found found => Results.Ok(new LocationPreviewResponse(
                new LocationDto(found.Location.Latitude, found.Location.Longitude),
                new LocalityDto(found.Locality.Code, found.Locality.Name),
                found.MatchedAddress)),
            PreviewLocationResult.OutOfCoverage => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: Problems.OutOfCoverage,
                title: "La ubicación está fuera del área de cobertura",
                detail: "La dirección quedó fuera de Bogotá D.C. Comparte tu ubicación."),
            _ => AddressNotFound(),
        };
    }

    private static IResult AddressNotFound() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: Problems.AddressNotFound,
        title: "No encontramos la dirección",
        detail: "Comparte tu ubicación para crear la solicitud.");

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelServiceRequestRequest body,
        HttpContext context,
        CancelServiceRequestHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        CancelServiceRequestResult result;
        try
        {
            result = await handler.HandleAsync(
                new CancelServiceRequestCommand(
                    user.TenantId, user.UserId, user.Role == AuthenticatedUser.AdminTenantRole, id, body.Reason, CorrelationId.Get(context)),
                cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }

        return result switch
        {
            CancelServiceRequestResult.Cancelled cancelled => Results.Ok(new CancelledServiceRequestResponse(
                cancelled.ServiceRequest.Id,
                cancelled.ServiceRequest.Status,
                cancelled.ServiceRequest.CancelledAt!.Value,
                cancelled.ServiceRequest.CancelledByType!)),
            CancelServiceRequestResult.InvalidTransition invalid => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.InvalidTransition,
                title: "La solicitud ya no se puede cancelar",
                detail: invalid.Status == ServiceRequestStatus.Cancelado
                    ? "La solicitud ya estaba cancelada."
                    : "El servicio ya inició o terminó."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe."),
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

    private static async Task<IResult> ListForTechnicianAsync(
        HttpContext context,
        ListTechnicianServiceRequestsHandler handler,
        string? status,
        string? page,
        string? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        // page y size llegan como texto para responder 400 con el campo, y no el 400 vacío del enlazado de enteros.
        var errors = new Dictionary<string, string[]>();
        var pageNumber = ParseOptionalInt(page, "page", errors);
        var pageSize = ParseOptionalInt(size, "size", errors);
        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListTechnicianServiceRequestsQuery(user.TenantId, user.UserId, status, pageNumber, pageSize), cancellationToken);
            return Results.Ok(new TechnicianServiceRequestPageResponse(
                result.Items.Select(TechnicianServiceRequestResponse.From).ToList(), result.Page, result.Size, result.TotalItems));
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    internal static int? ParseOptionalInt(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        errors[field] = ["Debe ser un número entero."];
        return null;
    }

    internal static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}