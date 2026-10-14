using Microsoft.Extensions.Logging;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Application.Events;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Matching;

public enum MatchingEventHandlingOutcome
{
    /// <summary>La solicitud cambió de estado.</summary>
    Applied,

    /// <summary>La solicitud no estaba en el estado de origen (por ejemplo, ya se canceló); no cambia.</summary>
    Ignored,

    /// <summary>Ya se aplicó un evento de Matching más reciente (ADR-017).</summary>
    Stale,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>La solicitud no existe en el tenant del evento.</summary>
    NotFound,
}

/// <summary>
/// Consumidor de los eventos de Matching que cambian el estado de la solicitud (DD 7.4): con técnico pasa a
/// <c>asignado</c>, sin técnicos pasa a <c>en_espera</c> y al reanudarse vuelve a <c>buscando_tecnico</c>. Es idempotente por <c>eventId</c> y aplica
/// el cambio bajo el tenant del evento (DD 10.3), así que una solicitud de otro tenant no se encuentra.
/// </summary>
public sealed partial class MatchingStatusHandler(
    ITenantUnitOfWork unitOfWork,
    IServiceRequestRepository serviceRequests,
    IProcessedEventRepository processedEvents,
    TimeProvider clock,
    ILogger<MatchingStatusHandler> logger)
{
    public Task<MatchingEventHandlingOutcome> HandleTechnicianAssignedAsync(
        EventEnvelope<TechnicianAssignedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.Data.ServiceRequestId,
            request => request.ApplyTechnicianAssigned(envelope.Data.TechnicianId, envelope.Data.AssignedAt, envelope.OccurredAt),
            cancellationToken);
    }

    public Task<MatchingEventHandlingOutcome> HandleNoTechnicianAvailableAsync(
        EventEnvelope<NoTechnicianAvailableData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.Data.ServiceRequestId,
            request => request.ApplyNoTechnicianAvailable(envelope.OccurredAt), cancellationToken);
    }

    public Task<MatchingEventHandlingOutcome> HandleSearchResumedAsync(
        EventEnvelope<SearchResumedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ApplyAsync(envelope.EventId, envelope.TenantId, envelope.EventType, envelope.Data.ServiceRequestId,
            request => request.ApplySearchResumed(envelope.OccurredAt), cancellationToken);
    }

    private Task<MatchingEventHandlingOutcome> ApplyAsync(
        Guid eventId,
        Guid tenantId,
        string eventType,
        Guid serviceRequestId,
        Func<DomainServiceRequest, MatchingEventOutcome> apply,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(eventId, ct))
                {
                    LogDuplicate(logger, eventId);
                    return MatchingEventHandlingOutcome.Duplicate;
                }

                processedEvents.Add(eventId, tenantId, eventType, clock.GetUtcNow());

                var request = await serviceRequests.FindByIdAsync(serviceRequestId, ct);
                if (request is null)
                {
                    LogNotFound(logger, eventId, serviceRequestId);
                    return MatchingEventHandlingOutcome.NotFound;
                }

                var outcome = apply(request) switch
                {
                    MatchingEventOutcome.Applied => MatchingEventHandlingOutcome.Applied,
                    MatchingEventOutcome.Ignored => MatchingEventHandlingOutcome.Ignored,
                    _ => MatchingEventHandlingOutcome.Stale,
                };
                LogApplied(logger, eventType, serviceRequestId, outcome, request.Status);
                return outcome;
            },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ya aplicado; se descarta.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento {EventId}: la solicitud {ServiceRequestId} no existe en el tenant del evento; se descarta.")]
    private static partial void LogNotFound(ILogger logger, Guid eventId, Guid serviceRequestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventType} para la solicitud {ServiceRequestId}: {Outcome} (estado {Status}).")]
    private static partial void LogApplied(ILogger logger, string eventType, Guid serviceRequestId, MatchingEventHandlingOutcome outcome, string status);
}