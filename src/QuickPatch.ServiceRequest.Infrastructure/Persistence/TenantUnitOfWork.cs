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

/// <summary>Transacción por tenant (DD, sección 10.2) con la implementación común de ADR-020.</summary>
public sealed class TenantUnitOfWork(ServiceRequestDbContext db) : ITenantUnitOfWork
{
    public Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        TenantTransaction.ExecuteAsync(db, tenantId, work, cancellationToken);
}