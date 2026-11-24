using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Persistencia de evidencias (tabla <c>service_evidence</c>, DD 5.10), siempre bajo el tenant de la transacción.</summary>
public interface IEvidenceRepository
{
    void Add(ServiceEvidence evidence);

    /// <summary>
    /// Cuántas evidencias tiene la solicitud. Completar la cuenta en la transacción que ya bloqueó la fila de la solicitud
    /// (RN-SR3): así no se cruza con una subida.
    /// </summary>
    Task<int> CountAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>Evidencias de la solicitud, de la más antigua a la más reciente.</summary>
    Task<IReadOnlyList<ServiceEvidence>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken);
}