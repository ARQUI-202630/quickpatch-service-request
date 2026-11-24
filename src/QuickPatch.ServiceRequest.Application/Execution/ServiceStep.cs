using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Execution;

/// <summary><c>POST .../start</c> o <c>.../complete</c> (contrato 1.7.0). El técnico y el tenant salen del JWT.</summary>
public sealed record ServiceStepCommand(Guid TenantId, Guid TechnicianId, Guid ServiceRequestId, Guid CorrelationId);

public abstract record ServiceStepResult
{
    public sealed record Done(DomainServiceRequest ServiceRequest) : ServiceStepResult;

    /// <summary>No existe, es de otro tenant (RLS) o está asignada a otro técnico: 404 para no revelarla.</summary>
    public sealed record NotFound : ServiceStepResult;

    /// <summary>La solicitud no está en el estado que exige el paso (RN-SR6 o RN-SR3).</summary>
    public sealed record InvalidTransition(string Status) : ServiceStepResult;

    /// <summary>Completar sin ninguna evidencia (RN-SR3, RN-E1).</summary>
    public sealed record EvidenceRequired : ServiceStepResult;
}