using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>Réplica local del estado de los tenants (tabla <c>tenant_status_replica</c>, RN-T1, ADR-017).</summary>
public interface ITenantStatusReplicaRepository
{
    /// <summary>Si la réplica dice que el tenant está inactivo. Un tenant sin fila cuenta como activo.</summary>
    Task<bool> IsInactiveAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantStatusReplica?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(TenantStatusReplica tenant);
}