using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary>
/// Evidencias de la solicitud (RF-15). Null si la solicitud no existe, es de otro tenant o no es del usuario: la API
/// responde 404. El <c>admin_tenant</c> ve las de cualquier solicitud de su tenant (RLS).
/// </summary>
public sealed class ListEvidenceHandler(
    ITenantUnitOfWork unitOfWork, IServiceRequestRepository serviceRequests, IEvidenceRepository evidence)
{
    public async Task<IReadOnlyList<ServiceEvidence>?> HandleAsync(EvidenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await unitOfWork.ExecuteAsync<IReadOnlyList<ServiceEvidence>?>(
            query.TenantId,
            async ct =>
            {
                var request = query.Viewer switch
                {
                    EvidenceViewer.Client => await serviceRequests.FindForClientAsync(query.ServiceRequestId, query.UserId, ct),
                    EvidenceViewer.Technician => await serviceRequests.FindForTechnicianAsync(query.ServiceRequestId, query.UserId, ct),
                    _ => await serviceRequests.FindInTenantAsync(query.ServiceRequestId, ct),
                };
                return request is null ? null : await evidence.ListAsync(request.Id, ct);
            },
            cancellationToken);
    }
}