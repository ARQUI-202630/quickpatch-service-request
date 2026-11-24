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

/// <summary>Calificaciones bajo RLS (DD 5.9). La fila solo se inserta: una calificación no se edita (ADR-027).</summary>
public sealed class RatingRepository(ServiceRequestDbContext db) : IRatingRepository
{
    public void Add(Rating rating) => db.Ratings.Add(rating);

    public Task<bool> ExistsAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Ratings.AnyAsync(x => x.ServiceRequestId == serviceRequestId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Rating>> FindByServiceRequestsAsync(
        IReadOnlyCollection<Guid> serviceRequestIds, CancellationToken cancellationToken)
    {
        if (serviceRequestIds.Count == 0)
        {
            return new Dictionary<Guid, Rating>();
        }

        return await db.Ratings.AsNoTracking()
            .Where(x => serviceRequestIds.Contains(x.ServiceRequestId))
            .ToDictionaryAsync(x => x.ServiceRequestId, cancellationToken);
    }
}