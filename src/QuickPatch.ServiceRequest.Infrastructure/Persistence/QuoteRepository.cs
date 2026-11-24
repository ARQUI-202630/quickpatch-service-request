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

/// <summary>
/// Cotizaciones bajo RLS. Se leen con seguimiento: los casos de uso las modifican en la misma transacción que ya bloqueó la
/// fila de la solicitud.
/// </summary>
public sealed class QuoteRepository(ServiceRequestDbContext db) : IQuoteRepository
{
    public void Add(Quote quote) => db.Quotes.Add(quote);

    public async Task<IReadOnlyList<Quote>> ListAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        await db.Quotes
            .Where(x => x.ServiceRequestId == serviceRequestId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
}