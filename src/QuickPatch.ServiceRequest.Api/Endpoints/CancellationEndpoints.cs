using QuickPatch.ServiceRequest.Api.Http;
using QuickPatch.ServiceRequest.Api.Security;
using QuickPatch.ServiceRequest.Application.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.Transversal.Http;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Api.Endpoints;

/// <summary>Cuerpo de <c>POST /v1/service-requests/{id}/cancel</c> (contrato 1.5.0).</summary>
public sealed record CancelServiceRequestRequest(string? Reason);

/// <summary>Esquema <c>CancelledServiceRequest</c> (1.5.0).</summary>
public sealed record CancelledServiceRequestResponse(Guid Id, string Status, DateTimeOffset CancelledAt, string CancelledByType);

/// <summary>Cancelación de una solicitud por el cliente o el admin del tenant (RF-36, contrato 1.5.0).</summary>
public static class CancellationEndpoints
{
    public static IEndpointRouteBuilder MapCancellationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{Routes.ServiceRequests}/{{id:guid}}/cancel", CancelAsync).WithName("cancelServiceRequest")
            .RequireAuthorization(AuthenticatedUser.CancelPolicy);
        return app;
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelServiceRequestRequest body,
        HttpContext context,
        CancelServiceRequestHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return ApiResults.InvalidToken();
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
            return ApiResults.ValidationProblem(ex.Errors);
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
}