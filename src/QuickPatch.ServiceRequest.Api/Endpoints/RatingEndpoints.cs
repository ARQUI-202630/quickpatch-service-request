using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.Ratings;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests/{id}/rating</c> (DD 8.3.11).</summary>
public sealed record RateServiceRequestRequest(int? Score, string? Comment);

/// <summary>Calificación guardada, como la respuesta <c>201</c> de DD 8.3.11.</summary>
public sealed record RatingResponse(Guid ServiceRequestId, int Score, string? Comment, DateTimeOffset RatedAt)
{
    public static RatingResponse From(Rating r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.ServiceRequestId, r.Score, r.Comment, r.CreatedAt);
    }
}

/// <summary>Calificación del servicio por su dueño cuando ya está pagado (RF-12, RN-R1 a RN-R3, ADR-027).</summary>
public static class RatingEndpoints
{
    public static IEndpointRouteBuilder MapRatingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{ServiceRequestEndpoints.BasePath}/{{id:guid}}/rating", RateAsync)
            .WithName("rateServiceRequest")
            .RequireAuthorization(AuthenticatedUser.ClientPolicy);
        return app;
    }

    private static async Task<IResult> RateAsync(
        Guid id,
        RateServiceRequestRequest body,
        HttpContext context,
        RateServiceRequestHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        RateServiceRequestResult result;
        try
        {
            result = await handler.HandleAsync(
                new RateServiceRequestCommand(user.TenantId, user.UserId, id, body.Score, body.Comment, CorrelationId.Get(context)),
                cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ServiceRequestEndpoints.ValidationProblem(ex.Errors);
        }

        return result switch
        {
            RateServiceRequestResult.Rated rated => Results.Json(
                RatingResponse.From(rated.Rating), statusCode: StatusCodes.Status201Created),
            RateServiceRequestResult.NotOwner => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Solo quien creó la solicitud puede calificarla."),
            RateServiceRequestResult.InvalidState => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.InvalidTransition,
                title: "La solicitud todavía no se puede calificar",
                detail: "Se califica cuando el servicio ya está pagado."),
            RateServiceRequestResult.AlreadyRated => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.AlreadyRated,
                title: "La solicitud ya fue calificada",
                detail: "Cada solicitud se califica una sola vez."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe."),
        };
    }
}