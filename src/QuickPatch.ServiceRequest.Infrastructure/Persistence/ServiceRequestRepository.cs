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

public sealed class ServiceRequestRepository(ServiceRequestDbContext db) : IServiceRequestRepository
{
    public void Add(DomainServiceRequest serviceRequest) => db.ServiceRequests.Add(serviceRequest);

    // FOR UPDATE: la fila queda bloqueada hasta el final de la transacción; RLS sigue filtrando por el tenant.
    public Task<DomainServiceRequest?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.ServiceRequests.FromSql($"SELECT * FROM service_requests WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);

    public Task<DomainServiceRequest?> FindInTenantAsync(Guid id, CancellationToken cancellationToken) =>
        db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<DomainServiceRequest?> FindForClientAsync(Guid id, Guid clientId, CancellationToken cancellationToken) =>
        db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, cancellationToken);

    public Task<DomainServiceRequest?> FindForCompanyMemberAsync(Guid id, Guid userId, Guid companyId, CancellationToken cancellationToken) =>
        db.ServiceRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && (x.ClientId == userId || x.CompanyId == companyId), cancellationToken);

    public Task<DomainServiceRequest?> FindForTechnicianAsync(Guid id, Guid technicianId, CancellationToken cancellationToken) =>
        db.ServiceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TechnicianId == technicianId, cancellationToken);

    public async Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTechnicianAsync(
        Guid technicianId, string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.ServiceRequests.AsNoTracking().Where(x => x.TechnicianId == technicianId);
        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.AssignedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<DomainServiceRequest> Items, int Total)> ListForTenantAsync(
        string? status, ListScope? scope, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.ServiceRequests.AsNoTracking();
        if (scope is { CompanyId: { } companyId })
        {
            query = query.Where(x => x.ClientId == scope.ClientId || x.CompanyId == companyId);
        }
        else if (scope is not null)
        {
            query = query.Where(x => x.ClientId == scope.ClientId);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<StatusCountRow>> CountByStatusAsync(DateTimeOffset createdSince, CancellationToken cancellationToken)
    {
        var rows = await db.ServiceRequests.AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Total = g.Count(), CreatedSince = g.Count(x => x.CreatedAt >= createdSince) })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new StatusCountRow(r.Status, r.Total, r.CreatedSince)).ToList();
    }
}