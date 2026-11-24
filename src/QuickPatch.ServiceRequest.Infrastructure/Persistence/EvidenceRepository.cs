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

/// <summary>Evidencias bajo RLS (DD 5.10). La fila solo se inserta: ni el técnico ni nadie la cambia después.</summary>
public sealed class EvidenceRepository(ServiceRequestDbContext db) : IEvidenceRepository
{
    public void Add(ServiceEvidence evidence) => db.Evidence.Add(evidence);

    public Task<int> CountAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Evidence.CountAsync(x => x.ServiceRequestId == serviceRequestId, cancellationToken);

    public async Task<IReadOnlyList<ServiceEvidence>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        await db.Evidence.AsNoTracking()
            .Where(x => x.ServiceRequestId == serviceRequestId)
            .OrderBy(x => x.UploadedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
}