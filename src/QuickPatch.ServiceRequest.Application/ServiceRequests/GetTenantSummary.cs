using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

public sealed record ServiceRequestSummary(DateTimeOffset GeneratedAt, ServiceRequestCounts Counts);

/// <summary>Conteos del tenant del token (<c>GET /v1/admin/service-requests/summary</c>, contrato 1.4.0, DD 8.3.15), bajo RLS.</summary>
public sealed class GetTenantSummaryHandler(ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, TimeProvider clock)
{
    public async Task<ServiceRequestSummary> HandleAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await unitOfWork.ExecuteAsync(
            tenantId, ct => serviceRequests.CountByStatusAsync(BogotaDay.StartUtc(now), ct), cancellationToken);
        return new ServiceRequestSummary(now, ServiceRequestCounts.From(rows));
    }
}