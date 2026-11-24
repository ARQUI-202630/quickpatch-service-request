using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Persistencia de calificaciones (tabla <c>ratings</c>, DD 5.9), siempre bajo el tenant de la transacción.</summary>
public interface IRatingRepository
{
    void Add(Rating rating);

    /// <summary>
    /// Si la solicitud ya tiene calificación (RN-R2). Se consulta en la transacción que ya bloqueó la fila de la solicitud;
    /// el índice único sobre <c>service_request_id</c> respalda la regla en la base.
    /// </summary>
    Task<bool> ExistsAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>Calificaciones de esas solicitudes en el tenant actual, por solicitud; las no calificadas no aparecen (SCRUM-100).</summary>
    Task<IReadOnlyDictionary<Guid, Rating>> FindByServiceRequestsAsync(IReadOnlyCollection<Guid> serviceRequestIds, CancellationToken cancellationToken);
}