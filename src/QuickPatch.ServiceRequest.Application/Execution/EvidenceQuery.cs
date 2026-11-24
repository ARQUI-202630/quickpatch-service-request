using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary>Quién consulta las evidencias (contrato 1.7.0): el cliente dueño, el técnico asignado o el <c>admin_tenant</c>.</summary>
public enum EvidenceViewer
{
    Client,
    Technician,
    AdminTenant,
}

public sealed record EvidenceQuery(Guid TenantId, Guid UserId, EvidenceViewer Viewer, Guid ServiceRequestId);