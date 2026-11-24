using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary>
/// Archivo de una evidencia (DD 8.3.10): con las mismas reglas que el listado, lo lee de Garage. Null si la solicitud o la
/// evidencia no son del usuario, o si el objeto ya no existe.
/// </summary>
public sealed class DownloadEvidenceHandler(ListEvidenceHandler list, IEvidenceStorage storage)
{
    public async Task<StoredEvidence?> HandleAsync(EvidenceQuery query, Guid evidenceId, CancellationToken cancellationToken)
    {
        var found = (await list.HandleAsync(query, cancellationToken))?.SingleOrDefault(e => e.Id == evidenceId);
        return found is null ? null : await storage.GetAsync(found.FileUrl, cancellationToken);
    }
}