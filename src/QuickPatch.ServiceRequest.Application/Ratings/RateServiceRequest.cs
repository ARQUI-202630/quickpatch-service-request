using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

namespace QuickPatch.ServiceRequest.Application.Ratings;

/// <summary>
/// Calificación del servicio (<c>POST /v1/service-requests/{id}/rating</c>, DD 8.3.11). El tenant y el usuario salen del JWT;
/// el cuerpo solo trae el puntaje y el comentario.
/// </summary>
public sealed record RateServiceRequestCommand(
    Guid TenantId, Guid UserId, Guid ServiceRequestId, int? Score, string? Comment, Guid CorrelationId);

public abstract record RateServiceRequestResult
{
    public sealed record Rated(Rating Rating) : RateServiceRequestResult;

    /// <summary>No existe o es de otro tenant: RLS no la deja ver (404, ADR-005).</summary>
    public sealed record NotFound : RateServiceRequestResult;

    /// <summary>Es del mismo tenant pero no la creó este usuario (403, ADR-027).</summary>
    public sealed record NotOwner : RateServiceRequestResult;

    /// <summary>La solicitud no está <c>pagado</c> (RN-R3).</summary>
    public sealed record InvalidState(string Status) : RateServiceRequestResult;

    /// <summary>La solicitud ya tiene calificación (RN-R2).</summary>
    public sealed record AlreadyRated : RateServiceRequestResult;
}

/// <summary>
/// Guarda la calificación de una solicitud pagada (RF-12, RN-R1 a RN-R3, ADR-027). Califica solo quien creó la solicitud;
/// para una empresa, el usuario que la creó, igual que las demás acciones del dueño. En una sola transacción bajo el tenant
/// del token bloquea la fila de la solicitud, valida el estado y que no esté calificada, guarda la fila de <c>ratings</c> y
/// deja en el Outbox <c>service-request.rated</c> (ADR-007), que Ranking consume para la reputación del técnico. Calificar
/// no cambia el estado de la solicitud (DD 7.4) y no depende de Ranking.
/// </summary>
public sealed class RateServiceRequestHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IRatingRepository ratings,
    IOutbox outbox,
    TimeProvider clock)
{
    public async Task<RateServiceRequestResult> HandleAsync(RateServiceRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Primero el cuerpo: una petición inválida no abre transacción ni bloquea la fila.
        var comment = Rating.Validate(command.Score, command.Comment);

        return await unitOfWork.ExecuteAsync<RateServiceRequestResult>(
            command.TenantId,
            async ct =>
            {
                var request = await serviceRequests.FindByIdAsync(command.ServiceRequestId, ct);
                if (request is null)
                {
                    return new RateServiceRequestResult.NotFound();
                }

                if (request.ClientId != command.UserId)
                {
                    return new RateServiceRequestResult.NotOwner();
                }

                if (request.Status != ServiceRequestStatus.Pagado || request.TechnicianId is null)
                {
                    return new RateServiceRequestResult.InvalidState(request.Status);
                }

                if (await ratings.ExistsAsync(request.Id, ct))
                {
                    return new RateServiceRequestResult.AlreadyRated();
                }

                var rating = Rating.Create(
                    request.TenantId, request.Id, request.TechnicianId.Value, command.Score!.Value, comment, clock.GetUtcNow());
                ratings.Add(rating);
                outbox.Enqueue(ServiceRequestEvents.Rated(request, rating, command.CorrelationId));
                return new RateServiceRequestResult.Rated(rating);
            },
            cancellationToken);
    }
}