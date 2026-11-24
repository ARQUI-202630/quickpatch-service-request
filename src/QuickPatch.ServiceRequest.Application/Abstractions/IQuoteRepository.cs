using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Persistencia de cotizaciones (tabla <c>quotes</c>, DD 5.11), siempre bajo el tenant de la transacción.</summary>
public interface IQuoteRepository
{
    void Add(Quote quote);

    /// <summary>
    /// Cotizaciones de la solicitud, de la más antigua a la más reciente. Las que se vayan a modificar se leen dentro de la
    /// transacción que ya bloqueó la fila de la solicitud (<see cref="IServiceRequestRepository.FindByIdAsync"/>): ese
    /// bloqueo serializa emitir, responder, vencer y cancelar (RN-Q7).
    /// </summary>
    Task<IReadOnlyList<Quote>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken);
}