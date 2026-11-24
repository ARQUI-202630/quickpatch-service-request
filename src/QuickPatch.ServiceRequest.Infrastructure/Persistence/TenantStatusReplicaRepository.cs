using Microsoft.EntityFrameworkCore;

using NetTopologySuite.Geometries;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;
using QuickPatch.Transversal.Messaging;
using QuickPatch.Transversal.Persistence;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

public sealed class TenantStatusReplicaRepository(ServiceRequestDbContext db) : ITenantStatusReplicaRepository
{
    public Task<bool> IsInactiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantStatuses.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Status == TenantStatusReplica.Inactive, cancellationToken);

    public Task<TenantStatusReplica?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantStatuses.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    public void Add(TenantStatusReplica tenant) => db.TenantStatuses.Add(tenant);
}