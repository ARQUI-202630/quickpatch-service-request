using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

public sealed record TenantServiceRequestCounts(Guid TenantId, ServiceRequestCounts Counts);

public sealed record PlatformServiceRequestSummary(DateTimeOffset GeneratedAt, IReadOnlyList<TenantServiceRequestCounts> Tenants);

/// <summary>
/// Conteos de cada tenant para el <c>admin_plataforma</c> (<c>GET /v1/platform/service-requests/summary</c>, contrato 1.4.0;
/// DD 10.4, DEP-15). Solo conteos; un tenant sin solicitudes no aparece.
/// </summary>
public sealed class GetPlatformSummaryHandler(IPlatformServiceRequestCounter counter, TimeProvider clock)
{
    public async Task<PlatformServiceRequestSummary> HandleAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await counter.CountByTenantAsync(tenantId, BogotaDay.StartUtc(now), cancellationToken);
        var tenants = rows
            .GroupBy(r => r.TenantId)
            .OrderBy(g => g.Key)
            .Select(g => new TenantServiceRequestCounts(
                g.Key, ServiceRequestCounts.From(g.Select(r => new StatusCountRow(r.Status, r.Total, r.CreatedSince)))))
            .ToList();
        return new PlatformServiceRequestSummary(now, tenants);
    }
}