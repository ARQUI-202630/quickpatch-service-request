using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Quotes;

/// <summary>Quien consulta las cotizaciones: el cliente dueño o el técnico asignado (contrato 1.6.0).</summary>
public sealed record ListQuotesQuery(Guid TenantId, Guid UserId, bool IsTechnician, Guid ServiceRequestId);

/// <summary>
/// Cotizaciones de la solicitud, de la más antigua a la más reciente (RF-34, RF-35). Null si la solicitud no existe, es de
/// otro tenant o no es del usuario (ni su cliente ni su técnico asignado): la API responde 404.
/// </summary>
public sealed class ListQuotesHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IQuoteRepository quotes)
{
    public async Task<IReadOnlyList<NumberedQuote>?> HandleAsync(ListQuotesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await unitOfWork.ExecuteAsync<IReadOnlyList<NumberedQuote>?>(
            query.TenantId,
            async ct =>
            {
                var request = query.IsTechnician
                    ? await serviceRequests.FindForTechnicianAsync(query.ServiceRequestId, query.UserId, ct)
                    : await serviceRequests.FindForClientAsync(query.ServiceRequestId, query.UserId, ct);
                if (request is null)
                {
                    return null;
                }

                var list = await quotes.ListAsync(request.Id, ct);
                return list.Select((q, i) => new NumberedQuote(q, i + 1)).ToList();
            },
            cancellationToken);
    }
}