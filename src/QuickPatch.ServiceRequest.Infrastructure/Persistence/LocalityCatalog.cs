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

/// <summary>Localidad que contiene un punto (<c>ST_Covers</c>, igual que Matching): un punto en el borde cuenta como dentro.</summary>
public sealed class LocalityCatalog(ServiceRequestDbContext db) : ILocalityCatalog
{
    public async Task<Locality?> FindContainingAsync(GeoPoint point, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        var location = new Point(point.Longitude, point.Latitude) { SRID = 4326 };
        var row = await db.Localities.AsNoTracking()
            .Where(l => l.Area.Covers(location))
            .OrderBy(l => l.Code)
            .Select(l => new { l.Code, l.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new Locality(row.Code, row.Name);
    }

    public async Task<IReadOnlyList<Locality>> ListAsync(CancellationToken cancellationToken) =>
        await db.Localities.AsNoTracking()
            .OrderBy(l => l.Code)
            .Select(l => new Locality(l.Code, l.Name))
            .ToListAsync(cancellationToken);
}