using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Cotizaciones pendientes ya vencidas de todos los tenants (RN-Q6). Usa el rol <c>service_request_scheduler</c> (P-06,
/// DD 10.2), que solo lee identificadores, tenant, estado y vencimiento; cada cotización se vence después en su tenant.
/// </summary>
public interface IExpiredQuoteFinder
{
    Task<IReadOnlyList<ExpiredQuoteRef>> FindAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public sealed record ExpiredQuoteRef(Guid QuoteId, Guid TenantId, Guid ServiceRequestId);