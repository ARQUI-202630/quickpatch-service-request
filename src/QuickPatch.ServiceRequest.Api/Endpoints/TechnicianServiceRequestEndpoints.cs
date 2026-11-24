using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>
/// Esquema <c>TechnicianServiceRequest</c> (1.2.0): sin dirección, ubicación ni cliente (RN-SR13). Desde 1.10.0 con
/// <c>score</c> y <c>ratedAt</c>, null si no está calificada; el comentario no se le muestra al técnico (SCRUM-100).
/// </summary>
public sealed record TechnicianServiceRequestResponse(
    Guid Id,
    string Status,
    Guid CategoryId,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt,
    int? Score,
    DateTimeOffset? RatedAt)
{
    public static TechnicianServiceRequestResponse From(DomainServiceRequest s, Rating? rating) =>
        new(s.Id, s.Status, s.CategoryId, s.Description, s.CreatedAt, s.AssignedAt, rating?.Score, rating?.CreatedAt);
}

/// <summary>Esquema <c>TechnicianServiceRequestPage</c> (1.2.0).</summary>
public sealed record TechnicianServiceRequestPageResponse(
    IReadOnlyList<TechnicianServiceRequestResponse> Items, int Page, int Size, int TotalItems);

/// <summary>Solicitudes asignadas al técnico autenticado (contrato 1.2.0).</summary>
public static class TechnicianServiceRequestEndpoints
{
    public static IEndpointRouteBuilder MapTechnicianServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.TechnicianServiceRequests, ListForTechnicianAsync).WithName("listTechnicianServiceRequests")
            .RequireAuthorization(AuthenticatedUser.TechnicianPolicy);
        return app;
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
            return ApiResults.InvalidToken();
        }

        // page y size llegan como texto para responder 400 con el campo, y no el 400 vacío del enlazado de enteros.
        var errors = new Dictionary<string, string[]>();
        var pageNumber = ApiResults.ParseOptionalInt(page, "page", errors);
        var pageSize = ApiResults.ParseOptionalInt(size, "size", errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem(errors);
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListTechnicianServiceRequestsQuery(user.TenantId, user.UserId, status, pageNumber, pageSize), cancellationToken);
            return Results.Ok(new TechnicianServiceRequestPageResponse(
                result.Items.Select(x => TechnicianServiceRequestResponse.From(x, result.Ratings.GetValueOrDefault(x.Id))).ToList(), result.Page, result.Size, result.TotalItems));
        }
        catch (DomainValidationException ex)
        {
            return ApiResults.ValidationProblem(ex.Errors);
        }
    }
}