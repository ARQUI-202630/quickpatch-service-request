using QuickPatch.ServiceRequest.Application.Abstractions;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>
/// Consulta de una solicitud por su cliente. Una solicitud de otro tenant (RLS) o de otro cliente
/// devuelve null, que la API responde como 404 para no revelar que existe.
/// </summary>
public sealed class GetServiceRequestHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests)
{
    public Task<DomainServiceRequest?> HandleAsync(Guid tenantId, Guid clientId, Guid id, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(tenantId, ct => serviceRequests.FindForClientAsync(id, clientId, ct), cancellationToken);
}