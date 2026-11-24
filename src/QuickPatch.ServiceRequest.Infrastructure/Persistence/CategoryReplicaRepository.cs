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

public sealed class CategoryReplicaRepository(ServiceRequestDbContext db) : ICategoryReplicaRepository
{
    public Task<bool> IsActiveAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Categories.AnyAsync(x => x.CategoryId == categoryId && x.Active, cancellationToken);

    public Task<CategoryReplica?> FindAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Categories.SingleOrDefaultAsync(x => x.CategoryId == categoryId, cancellationToken);

    public void Add(CategoryReplica category) => db.Categories.Add(category);

    public async Task<IReadOnlyList<CategoryReplica>> ListActiveAsync(CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.CategoryId)
            .ToListAsync(cancellationToken);
}